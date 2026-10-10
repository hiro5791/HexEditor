using System.Globalization;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Hashing;

/// <summary>期待値との照合の結果 (ANA-21 の仕様 3)。</summary>
public enum HashMatch
{
    /// <summary>不一致。</summary>
    None,

    /// <summary>一致。</summary>
    Match,

    /// <summary>バイト順を逆にした値と一致 (64 bit 以下の値のみ)。</summary>
    MatchReversed,
}

/// <summary>
/// 利用者が入力した期待値 (ANA-21 の仕様 2)。<see cref="FileName"/> と <see cref="AlgorithmHint"/> はチェックサムファイルの 1 行の形で
/// 入力された場合だけ持つ。
/// </summary>
public sealed record ExpectedHash(byte[] Value, string? FileName = null, string? AlgorithmHint = null)
{
    private static readonly Regex BsdLine = new(@"^\s*(?<alg>[A-Za-z0-9/\-_]+)\s*\((?<name>.*)\)\s*=\s*(?<value>\S+)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex GnuLine = new(@"^\s*\\?(?<value>[0-9A-Fa-f]+|[A-Za-z0-9+/]+=*)\s+(?<mode>[ *])?(?<name>.+?)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 期待値を解釈する。Hex (大文字・小文字、空白・<c>:</c>・<c>-</c> 区切り、<c>0x</c> 接頭辞)、Base64、チェックサムファイルの 1 行
    /// (<c>&lt;値&gt; *&lt;ファイル名&gt;</c>、<c>&lt;値&gt;  &lt;ファイル名&gt;</c>、<c>SHA256 (&lt;ファイル名&gt;) = &lt;値&gt;</c>) を受け付ける。
    /// </summary>
    public static bool TryParse(string? text, out ExpectedHash? expected)
    {
        expected = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        byte[]? value;
        Match bsd = BsdLine.Match(text);
        if (bsd.Success && TryParseValue(bsd.Groups["value"].Value, out value))
        {
            expected = new ExpectedHash(value!, bsd.Groups["name"].Value, bsd.Groups["alg"].Value);
            return true;
        }

        // GNU 形式の行は、値とファイル名の間が「空白 2 つ」か「空白と *」。1 つの空白は Hex の区切りとみなす。
        Match gnu = GnuLine.Match(text);
        bool looksLikeLine = text.Contains("  ", StringComparison.Ordinal) || text.Contains(" *", StringComparison.Ordinal);
        if (looksLikeLine && gnu.Success && TryParseValue(gnu.Groups["value"].Value, out value))
        {
            expected = new ExpectedHash(value!, gnu.Groups["name"].Value);
            return true;
        }

        if (TryParseValue(text, out value))
        {
            expected = new ExpectedHash(value!);
            return true;
        }

        return false;
    }

    /// <summary>値だけ (Hex または Base64) を解釈する。Hex として読めるものは Hex を優先する。</summary>
    public static bool TryParseValue(string text, out byte[]? value)
    {
        value = null;
        string t = text.Trim();
        string hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t;
        hex = hex.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal);
        if (hex.Length > 0 && hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit))
        {
            value = Convert.FromHexString(hex);
            return true;
        }

        if (t.Length >= 4 && t.Length % 4 == 0 && !t.Contains(' ', StringComparison.Ordinal))
        {
            try
            {
                value = Convert.FromBase64String(t);
                return value.Length > 0;
            }
            catch (FormatException)
            {
                value = null;
            }
        }

        return false;
    }

    /// <summary>計算した値と比べる (ANA-21 の仕様 3)。</summary>
    public HashMatch Compare(ReadOnlySpan<byte> actual, bool numeric)
    {
        if (actual.SequenceEqual(Value))
        {
            return HashMatch.Match;
        }

        if (numeric && actual.Length <= 8 && actual.Length == Value.Length && actual.Length > 1)
        {
            byte[] reversed = Value.ToArray();
            Array.Reverse(reversed);
            if (actual.SequenceEqual(reversed))
            {
                return HashMatch.MatchReversed;
            }
        }

        return HashMatch.None;
    }

    /// <summary>結果の行と比べる。</summary>
    public HashMatch Compare(HashResultRow row) => Compare(row.Value, row.IsNumeric);
}

/// <summary>チェックサムファイルの 1 行 (ANA-21 の仕様 4)。</summary>
public sealed record ChecksumEntry(int LineNumber, string FileName, byte[] Value, string? AlgorithmId);

/// <summary>チェックサムファイル (<c>.md5</c>、<c>.sha1</c>、<c>.sha256</c>、<c>.sha512</c>、<c>.sfv</c>) の読み込みと照合 (ANA-21 の仕様 4)。</summary>
public static class ChecksumFile
{
    /// <summary>拡張子からアルゴリズムを決める。分からなければ null (値の長さで決める)。</summary>
    public static string? AlgorithmForExtension(string? extension) =>
        extension?.TrimStart('.').ToLowerInvariant() switch
        {
            "md5" => "md5",
            "sha1" => "sha1",
            "sha256" => "sha256",
            "sha384" => "sha384",
            "sha512" => "sha512",
            "sfv" => "crc32",
            _ => null,
        };

    /// <summary>値の長さからアルゴリズムを決める (拡張子で決まらない場合)。</summary>
    public static string? AlgorithmForLength(int bytes) => bytes switch
    {
        4 => "crc32",
        16 => "md5",
        20 => "sha1",
        32 => "sha256",
        48 => "sha384",
        64 => "sha512",
        _ => null,
    };

    /// <summary>
    /// 内容を読む。GNU 形式 (<c>&lt;値&gt; *&lt;名前&gt;</c>、<c>&lt;値&gt;  &lt;名前&gt;</c>)、BSD 形式 (<c>SHA256 (&lt;名前&gt;) = &lt;値&gt;</c>)、
    /// SFV (<c>&lt;名前&gt; &lt;CRC-32&gt;</c>、<c>;</c> はコメント) を受け付ける。読めない行は飛ばす。
    /// </summary>
    public static IReadOnlyList<ChecksumEntry> Parse(string content, string? extension)
    {
        bool sfv = string.Equals(extension?.TrimStart('.'), "sfv", StringComparison.OrdinalIgnoreCase);
        string? byExtension = AlgorithmForExtension(extension);
        var entries = new List<ChecksumEntry>();
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (sfv)
            {
                int space = line.LastIndexOf(' ');
                if (space > 0 && ExpectedHash.TryParseValue(line[(space + 1)..], out byte[]? crc) && crc!.Length == 4)
                {
                    entries.Add(new ChecksumEntry(i + 1, line[..space].Trim(), crc, "crc32"));
                }

                continue;
            }

            if (ExpectedHash.TryParse(line, out ExpectedHash? e) && e!.FileName is { } name)
            {
                string? algorithm = e.AlgorithmHint is { } hint ? HashCatalog.Find(hint)?.Id : null;
                algorithm ??= byExtension ?? AlgorithmForLength(e.Value.Length);
                entries.Add(new ChecksumEntry(i + 1, name, e.Value, algorithm));
            }
        }

        return entries;
    }

    /// <summary>
    /// ドキュメントのファイル名に対応する行を探す (ファイル名の部分だけを、大文字・小文字を区別しない序数比較で比べる)。
    /// 見つからなければ null (ANA-21 の「エラー」の「このファイルの行が見つかりません」)。
    /// </summary>
    public static ChecksumEntry? FindEntry(IReadOnlyList<ChecksumEntry> entries, string documentFileName)
    {
        string name = Path.GetFileName(documentFileName);
        return entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FileName.Replace('\\', '/').Split('/')[^1]), name,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// チェックサムファイルで検証するアルゴリズムと期待値 (行が決まった後)。アルゴリズムが分からない行は null。
    /// </summary>
    public static (HashAlgorithmInfo Algorithm, ExpectedHash Expected)? Resolve(ChecksumEntry entry) =>
        entry.AlgorithmId is { } id && HashCatalog.Find(id) is { } algorithm
            ? (algorithm, new ExpectedHash(entry.Value, entry.FileName))
            : null;

    /// <summary>行の一覧に出す文字列 (ANA-21 の「エラー」の「ファイル内の行を選べる一覧」)。</summary>
    public static string Describe(ChecksumEntry entry) =>
        string.Create(CultureInfo.InvariantCulture, $"{entry.LineNumber}: {entry.FileName}");
}
