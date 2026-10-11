using System.Globalization;
using System.Text;

namespace HexEditor.Core.View;

/// <summary>文字表の不正な行の理由 (VIEW-23 の「エラー」)。</summary>
public enum TableLineError
{
    /// <summary><c>=</c> がない。</summary>
    NoEquals,

    /// <summary>左辺が Hex でない。</summary>
    NotHex,

    /// <summary>左辺が奇数桁。</summary>
    OddDigits,

    /// <summary>左辺が 8 バイトを超える (または空)。</summary>
    TooLong,

    /// <summary>右辺が空。</summary>
    EmptyValue,
}

/// <summary>文字表の読み込みの結果 (VIEW-23)。<see cref="Table"/> が null なら読み込まない (<see cref="Failure"/> に理由)。</summary>
public sealed record TableLoadResult(TableFile? Table, IReadOnlyList<(int Line, TableLineError Error)> InvalidLines, TableLoadFailure? Failure);

/// <summary>文字表を読み込まない理由 (VIEW-23 の「エラー」「巨大ファイル」)。</summary>
public enum TableLoadFailure
{
    /// <summary>有効なエントリが 0 件。</summary>
    NoEntries,

    /// <summary>エントリが上限 (65,536) を超える。</summary>
    TooManyEntries,
}

/// <summary>
/// 独自の文字表 (ROM の改造で広く使われる <c>.tbl</c> 形式。VIEW-23)。1 行に <c>16 進バイト列=文字列</c>、<c>/XX=文字列</c> (終端記号)、
/// <c>*XX</c> (改行記号。<c>↵</c> で表示)。解読は最長一致 (仕様 3)。
/// </summary>
public sealed class TableFile
{
    /// <summary>エントリ数の上限 (VIEW-23 の「巨大ファイル」)。</summary>
    public const int MaxEntries = 65536;

    /// <summary>左辺の最大のバイト数 (仕様 1)。</summary>
    public const int MaxKeyBytes = 8;

    /// <summary>改行記号の表示 (仕様 1)。</summary>
    public const string NewLineSymbol = "↵";

    private readonly Dictionary<ulong, string>[] _byLength = new Dictionary<ulong, string>[MaxKeyBytes + 1];
    private readonly Dictionary<string, byte[]> _reverse = new(StringComparer.Ordinal);

    private TableFile(string name)
    {
        Name = name;
        for (int i = 0; i <= MaxKeyBytes; i++)
        {
            _byLength[i] = [];
        }
    }

    /// <summary>文字表の名前 (ファイル名)。</summary>
    public string Name { get; }

    /// <summary>有効なエントリの数。</summary>
    public int Count { get; private set; }

    /// <summary>最も長い左辺のバイト数。</summary>
    public int LongestKey { get; private set; }

    /// <summary>
    /// ファイルの内容を読む。文字コードは UTF-8 (BOM の有無どちらも可) と UTF-16 LE (BOM 付き) (仕様 2)。不正な行は行番号付きで返し、
    /// それ以外の行は読み込む。<c>#</c> で始まる行と空行は無視する (不正な行に数えない)。
    /// </summary>
    public static TableLoadResult Parse(ReadOnlySpan<byte> content, string name)
    {
        string text = content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE
            ? Encoding.Unicode.GetString(content[2..])
            : content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
                ? Encoding.UTF8.GetString(content[3..])
                : Encoding.UTF8.GetString(content);
        return Parse(text, name);
    }

    /// <summary>文字列から読む (<see cref="Parse(ReadOnlySpan{byte}, string)"/> と同じ規則)。</summary>
    public static TableLoadResult Parse(string text, string name)
    {
        var table = new TableFile(name);
        var invalid = new List<(int, TableLineError)>();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string key;
            string value;
            if (line.StartsWith('*'))
            {
                // 改行記号: 右辺はない。
                key = line[1..].Trim();
                value = NewLineSymbol;
            }
            else
            {
                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    invalid.Add((i + 1, TableLineError.NoEquals));
                    continue;
                }

                key = line[..eq].Trim();
                if (key.StartsWith('/'))
                {
                    // 終端記号: 右辺の文字列で表示する。
                    key = key[1..];
                }

                value = line[(eq + 1)..];
                if (value.Length == 0)
                {
                    invalid.Add((i + 1, TableLineError.EmptyValue));
                    continue;
                }
            }

            if (key.Length == 0 || !key.All(Uri.IsHexDigit))
            {
                invalid.Add((i + 1, key.Length == 0 ? TableLineError.TooLong : TableLineError.NotHex));
                continue;
            }

            if (key.Length % 2 != 0)
            {
                invalid.Add((i + 1, TableLineError.OddDigits));
                continue;
            }

            if (key.Length / 2 > MaxKeyBytes)
            {
                invalid.Add((i + 1, TableLineError.TooLong));
                continue;
            }

            int length = key.Length / 2;
            ulong code = ulong.Parse(key, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            if (table._byLength[length].TryAdd(code, value))
            {
                table.Count++;
            }
            else
            {
                table._byLength[length][code] = value;
            }

            byte[] bytes = Convert.FromHexString(key);
            table._reverse.TryAdd(value, bytes);
            table.LongestKey = Math.Max(table.LongestKey, length);
            if (table.Count > MaxEntries)
            {
                return new TableLoadResult(null, invalid, TableLoadFailure.TooManyEntries);
            }
        }

        return table.Count == 0
            ? new TableLoadResult(null, invalid, TableLoadFailure.NoEntries)
            : new TableLoadResult(table, invalid, null);
    }

    /// <summary>
    /// <paramref name="data"/> の先頭で最長一致する項目 (仕様 3)。一致すればそのバイト数と文字列、しなければ 0 と null。
    /// </summary>
    public (int Length, string? Text) Match(ReadOnlySpan<byte> data)
    {
        for (int length = Math.Min(LongestKey, data.Length); length >= 1; length--)
        {
            if (_byLength[length].Count == 0)
            {
                continue;
            }

            ulong code = 0;
            for (int i = 0; i < length; i++)
            {
                code = (code << 8) | data[i];
            }

            if (_byLength[length].TryGetValue(code, out string? text))
            {
                return (length, text);
            }
        }

        return (0, null);
    }

    /// <summary>文字列を表のバイト列にする (右辺の最長一致。表せない部分があれば false)。テキスト列への入力用。</summary>
    public bool TryEncode(string text, out byte[] bytes)
    {
        var result = new List<byte>();
        int at = 0;
        int longest = _reverse.Keys.Count == 0 ? 0 : _reverse.Keys.Max(k => k.Length);
        while (at < text.Length)
        {
            bool found = false;
            for (int length = Math.Min(longest, text.Length - at); length >= 1; length--)
            {
                if (_reverse.TryGetValue(text.Substring(at, length), out byte[]? code))
                {
                    result.AddRange(code);
                    at += length;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                bytes = [];
                return false;
            }
        }

        bytes = [.. result];
        return true;
    }

    /// <summary>バイト列を文字列にする (コピー用。一致しないバイトは U+FFFD)。</summary>
    public string Decode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        int at = 0;
        while (at < data.Length)
        {
            (int length, string? text) = Match(data[at..]);
            if (length == 0)
            {
                sb.Append('�');
                at++;
            }
            else
            {
                sb.Append(text);
                at += length;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// 読み込んだ文字表の一覧 (VIEW-23 の仕様 6)。文字表は設定フォルダの <c>tables</c> にコピーして保存し、次回以降も文字コードの一覧の
/// 「独自」から選べる。文字コードの名前は <c>tbl:ファイル名</c>。
/// </summary>
public static class TableEncodings
{
    public const string IdPrefix = "tbl:";

    public const string FolderName = "tables";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, TableFile> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>文字表を保存するフォルダ (アプリが起動時に設定フォルダの <c>tables</c> を設定する)。null なら保存しない。</summary>
    public static string? Folder { get; set; }

    /// <summary>文字表の文字コードの名前。</summary>
    public static string IdOf(string fileName) => IdPrefix + fileName;

    /// <summary>保存してある文字表の名前 (ファイル名) の一覧。</summary>
    public static IReadOnlyList<string> Names
    {
        get
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                foreach (string name in Loaded.Keys)
                {
                    names.Add(name);
                }
            }

            if (Folder is { } folder && Directory.Exists(folder))
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        names.Add(Path.GetFileName(file));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return [.. names];
        }
    }

    /// <summary>
    /// 文字表のファイルを読み込み、設定フォルダにコピーする (仕様 6)。結果に読み込めたかと不正な行。
    /// </summary>
    public static TableLoadResult Import(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        TableLoadResult result = TableFile.Parse(content, name);
        if (result.Table is null)
        {
            return result;
        }

        if (Folder is { } folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, name), content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        lock (Gate)
        {
            Loaded[name] = result.Table;
        }

        TextEncoding.ForgetTable(IdOf(name));
        return result;
    }

    /// <summary>名前 (ファイル名) の文字表。読み込んでいなければ設定フォルダから読む。なければ null。</summary>
    public static TableFile? Find(string name)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue(name, out TableFile? table))
            {
                return table;
            }
        }

        if (Folder is not { } folder)
        {
            return null;
        }

        string path = Path.Combine(folder, Path.GetFileName(name));
        try
        {
            if (!File.Exists(path) || TableFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)).Table is not { } read)
            {
                return null;
            }

            lock (Gate)
            {
                Loaded[name] = read;
            }

            return read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>テスト用: 読み込んだ文字表を忘れる。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Loaded.Clear();
        }
    }
}
