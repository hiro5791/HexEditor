using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.FileTypes;

/// <summary>判定のためにデータを読む (判定の基準の位置からの相対位置)。</summary>
public interface IMagicData
{
    /// <summary>基準の位置から末尾までの長さ。</summary>
    long Length { get; }

    /// <summary>
    /// <paramref name="offset"/> から読む。読めたバイト数を返す (範囲外・読み込んでいない部分は 0 か短い数)。
    /// </summary>
    int Read(long offset, Span<byte> destination);
}

/// <summary>数値の型 (判定条件の数値の比較・間接オフセット)。</summary>
public enum MagicNumberType
{
    U8,
    U16LE,
    U16BE,
    U32LE,
    U32BE,
    U64LE,
    U64BE,
}

/// <summary>条件の位置。負の値は末尾から。<see cref="IndirectType"/> があれば、その位置の数値 + <see cref="Add"/> を位置とする。</summary>
public sealed record MagicOffset(long Offset, MagicNumberType? IndirectType = null, long Add = 0)
{
    public bool IsDirect => IndirectType is null;

    /// <summary>実際の位置 (読めなければ null)。</summary>
    public long? Resolve(IMagicData data)
    {
        long at = Offset < 0 ? data.Length + Offset : Offset;
        if (at < 0)
        {
            return null;
        }

        if (IndirectType is not MagicNumberType type)
        {
            return at;
        }

        return MagicCondition.ReadNumber(data, at, type) is ulong value && value < long.MaxValue / 2 ? (long)value + Add : null;
    }
}

/// <summary>判定条件 (ANA-17 の仕様 1)。<see cref="Evaluate"/> は一致した位置と一致の強さ (固定バイトの数) を加える。</summary>
public abstract record MagicCondition
{
    /// <summary>一致するか。一致したら <paramref name="matches"/> に位置を、<paramref name="score"/> に固定バイトの数を加える。</summary>
    public abstract bool Evaluate(IMagicData data, List<(long Offset, int Length)> matches, ref int score);

    internal static ulong? ReadNumber(IMagicData data, long at, MagicNumberType type)
    {
        int size = type switch
        {
            MagicNumberType.U8 => 1,
            MagicNumberType.U16LE or MagicNumberType.U16BE => 2,
            MagicNumberType.U32LE or MagicNumberType.U32BE => 4,
            _ => 8,
        };
        Span<byte> b = stackalloc byte[8];
        if (data.Read(at, b[..size]) < size)
        {
            return null;
        }

        return type switch
        {
            MagicNumberType.U8 => b[0],
            MagicNumberType.U16LE => BinaryPrimitives.ReadUInt16LittleEndian(b),
            MagicNumberType.U16BE => BinaryPrimitives.ReadUInt16BigEndian(b),
            MagicNumberType.U32LE => BinaryPrimitives.ReadUInt32LittleEndian(b),
            MagicNumberType.U32BE => BinaryPrimitives.ReadUInt32BigEndian(b),
            MagicNumberType.U64LE => BinaryPrimitives.ReadUInt64LittleEndian(b),
            _ => BinaryPrimitives.ReadUInt64BigEndian(b),
        };
    }
}

/// <summary>
/// バイト列 (ワイルドカード <c>??</c> 可) または文字列の一致。<see cref="Search"/> が 0 より大きければ、位置から
/// <see cref="Search"/> バイトの範囲のどこかで一致すればよい。
/// </summary>
public sealed record BytesCondition(MagicOffset Offset, byte[] Value, bool[] Wildcard, bool IgnoreCase = false, int Search = 0) : MagicCondition
{
    /// <summary>ワイルドカードでないバイトの数。</summary>
    public int FixedCount => Wildcard.Count(w => !w);

    /// <summary>先頭から最初のワイルドカードまでの固定のバイト列 (埋め込まれた形式の探索に使う)。</summary>
    public ReadOnlySpan<byte> FixedPrefix
    {
        get
        {
            int n = Array.IndexOf(Wildcard, true);
            return Value.AsSpan(0, n < 0 ? Value.Length : n);
        }
    }

    public override bool Evaluate(IMagicData data, List<(long Offset, int Length)> matches, ref int score)
    {
        if (Offset.Resolve(data) is not long at)
        {
            return false;
        }

        int window = Value.Length + Math.Max(0, Search);
        byte[] buffer = new byte[window];
        int read = data.Read(at, buffer);
        for (int shift = 0; shift + Value.Length <= read && shift <= Math.Max(0, Search); shift++)
        {
            if (MatchesAt(buffer.AsSpan(shift)))
            {
                matches.Add((at + shift, Value.Length));
                score += FixedCount;
                return true;
            }
        }

        return false;
    }

    private bool MatchesAt(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < Value.Length; i++)
        {
            if (Wildcard[i])
            {
                continue;
            }

            byte a = s[i];
            byte b = Value[i];
            if (a != b && !(IgnoreCase && a < 0x80 && b < 0x80 && char.ToLowerInvariant((char)a) == char.ToLowerInvariant((char)b)))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>比較の演算子。<c>&amp;</c> は「マスクしたビットがすべて立っている」。</summary>
public enum MagicOperator
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    AllBits,
}

/// <summary>数値の比較。<see cref="Mask"/> があれば比べる前に AND する。</summary>
public sealed record NumberCondition(MagicOffset Offset, MagicNumberType Type, MagicOperator Operator, ulong Value, ulong? Mask = null) : MagicCondition
{
    public override bool Evaluate(IMagicData data, List<(long Offset, int Length)> matches, ref int score)
    {
        if (Offset.Resolve(data) is not long at || ReadNumber(data, at, Type) is not ulong v)
        {
            return false;
        }

        if (Mask is ulong mask)
        {
            v &= mask;
        }

        bool ok = Operator switch
        {
            MagicOperator.Equal => v == Value,
            MagicOperator.NotEqual => v != Value,
            MagicOperator.Less => v < Value,
            MagicOperator.LessOrEqual => v <= Value,
            MagicOperator.Greater => v > Value,
            MagicOperator.GreaterOrEqual => v >= Value,
            _ => (v & Value) == Value,
        };
        if (ok)
        {
            int size = Type switch
            {
                MagicNumberType.U8 => 1,
                MagicNumberType.U16LE or MagicNumberType.U16BE => 2,
                MagicNumberType.U32LE or MagicNumberType.U32BE => 4,
                _ => 8,
            };
            matches.Add((at, size));

            // 等しいことの確認は固定のバイト列と同じ強さ、範囲の比較は弱い一致として数える。
            score += Operator == MagicOperator.Equal ? size : 1;
        }

        return ok;
    }
}

/// <summary>すべての条件 (AND)。</summary>
public sealed record AllCondition(IReadOnlyList<MagicCondition> Items) : MagicCondition
{
    public override bool Evaluate(IMagicData data, List<(long Offset, int Length)> matches, ref int score)
    {
        int mark = matches.Count;
        int s = 0;
        foreach (MagicCondition c in Items)
        {
            if (!c.Evaluate(data, matches, ref s))
            {
                matches.RemoveRange(mark, matches.Count - mark);
                return false;
            }
        }

        score += s;
        return true;
    }
}

/// <summary>どれかの条件 (OR)。最初に一致したものを使う。</summary>
public sealed record AnyCondition(IReadOnlyList<MagicCondition> Items) : MagicCondition
{
    public override bool Evaluate(IMagicData data, List<(long Offset, int Length)> matches, ref int score)
    {
        foreach (MagicCondition c in Items)
        {
            if (c.Evaluate(data, matches, ref score))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>条件の否定。</summary>
public sealed record NotCondition(MagicCondition Item) : MagicCondition
{
    public override bool Evaluate(IMagicData data, List<(long Offset, int Length)> matches, ref int score)
    {
        int s = 0;
        var scratch = new List<(long, int)>();
        return !Item.Evaluate(data, scratch, ref s);
    }
}

/// <summary>シグネチャデータベースの 1 項目 (ANA-17 の仕様 1)。</summary>
public sealed record FileFormat(string Id, string Name, string Mime, IReadOnlyList<string> Extensions, int Priority, MagicCondition Match)
{
    /// <summary>出どころ (内蔵 / 利用者のファイル名 / プラグイン)。</summary>
    public string Source { get; init; } = FileTypeDatabase.BuiltInSource;

    /// <summary>埋め込まれた形式の探索に使う、先頭 (オフセット 0) の固定のバイト列 (4 バイト以上ある場合だけ)。</summary>
    public byte[]? Anchor
    {
        get
        {
            BytesCondition? first = Match switch
            {
                BytesCondition b => b,
                AllCondition all => all.Items.FirstOrDefault() as BytesCondition,
                _ => null,
            };
            if (first is { Offset: { IsDirect: true, Offset: 0 }, Search: 0, IgnoreCase: false } && first.FixedPrefix.Length >= FileTypeDatabase.MinAnchor)
            {
                return first.FixedPrefix.ToArray();
            }

            return null;
        }
    }
}

/// <summary>利用者のデータベースの読み込みエラー (ANA-17 の「エラー」)。</summary>
public sealed record MagicLoadError(string FileName, int Line, string Message);

/// <summary>
/// シグネチャデータベース (ANA-17 の仕様 1)。本プロジェクトが自前で作成・保守する JSON (FileTypes/magic.json) を内蔵し、
/// 利用者は同じ形式の JSON を設定フォルダの <c>magic/</c> に置いて追加できる。
/// </summary>
/// <remarks>
/// JSON の形: <c>{ "formats": [ { "id", "name", "mime", "extensions": [..], "priority": 0〜100 (既定 50), "match": 条件 } ] }</c>。
/// 条件は次のどれか: <c>{ "offset": n, "bytes": "89 50 ?? 47" }</c>、<c>{ "offset": n, "string": "PK", "ignoreCase": bool }</c>
/// (どちらも <c>"search": n</c> で範囲の探索)、<c>{ "offset": n, "type": "u16le", "op": "==", "value": n, "mask": n }</c>、
/// <c>{ "all": [..] }</c>、<c>{ "any": [..] }</c>、<c>{ "not": 条件 }</c>。offset の代わりに
/// <c>"offsetFrom": { "offset": n, "type": "u32le", "add": n }</c> で間接の位置を書ける。負の offset は末尾から。
/// </remarks>
public sealed class FileTypeDatabase
{
    public const string BuiltInSource = "builtin";

    /// <summary>埋め込まれた形式の探索に使う固定のバイト列の最小の長さ (ANA-17 の仕様 6)。</summary>
    public const int MinAnchor = 4;

    /// <summary>利用者のデータベースを置くフォルダの名前。</summary>
    public const string UserFolderName = "magic";

    private static readonly Lazy<FileTypeDatabase> BuiltInLazy = new(LoadBuiltIn);

    public FileTypeDatabase(IReadOnlyList<FileFormat> formats, IReadOnlyList<MagicLoadError>? errors = null)
    {
        Formats = formats;
        Errors = errors ?? [];
    }

    public IReadOnlyList<FileFormat> Formats { get; }

    /// <summary>読み込めなかった利用者のファイル。</summary>
    public IReadOnlyList<MagicLoadError> Errors { get; }

    /// <summary>内蔵のデータベース。</summary>
    public static FileTypeDatabase BuiltIn => BuiltInLazy.Value;

    /// <summary>内蔵のデータベースに、利用者のフォルダの JSON を加えたもの。構文エラーのファイルは読み飛ばしてエラーに記録する。</summary>
    public static FileTypeDatabase WithUserFolder(string? folder)
    {
        var formats = new List<FileFormat>(BuiltIn.Formats);
        var errors = new List<MagicLoadError>();
        if (folder is not null && Directory.Exists(folder))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(path);
                try
                {
                    formats.AddRange(Parse(File.ReadAllText(path), name));
                }
                catch (JsonException e)
                {
                    errors.Add(new MagicLoadError(UserFolderName + "/" + name, (int)(e.LineNumber ?? 0) + 1, e.Message));
                }
                catch (FormatException e)
                {
                    errors.Add(new MagicLoadError(UserFolderName + "/" + name, 0, e.Message));
                }
                catch (IOException e)
                {
                    errors.Add(new MagicLoadError(UserFolderName + "/" + name, 0, e.Message));
                }
            }
        }

        return new FileTypeDatabase(formats, errors);
    }

    private static FileTypeDatabase LoadBuiltIn()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HexEditor.FileTypes.magic.json")
            ?? throw new InvalidOperationException("内蔵のシグネチャデータベースがありません。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new FileTypeDatabase(Parse(reader.ReadToEnd(), BuiltInSource));
    }

    /// <summary>JSON を読む。構文の誤りは <see cref="JsonException"/> (行番号付き)、内容の誤りは <see cref="FormatException"/>。</summary>
    public static IReadOnlyList<FileFormat> Parse(string json, string source)
    {
        JsonNode root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            ?? throw new FormatException("空のデータベースです。");
        var result = new List<FileFormat>();
        foreach (JsonNode? node in root["formats"]?.AsArray() ?? throw new FormatException("\"formats\" がありません。"))
        {
            if (node is not JsonObject o)
            {
                continue;
            }

            string id = o["id"]?.GetValue<string>() ?? throw new FormatException("\"id\" がありません。");
            result.Add(new FileFormat(
                id,
                o["name"]?.GetValue<string>() ?? id,
                o["mime"]?.GetValue<string>() ?? "application/octet-stream",
                [.. (o["extensions"]?.AsArray() ?? []).Select(e => e!.GetValue<string>().TrimStart('.').ToLowerInvariant())],
                o["priority"]?.GetValue<int>() ?? 50,
                ParseCondition(o["match"] ?? throw new FormatException($"{id}: \"match\" がありません。")))
            {
                Source = source,
            });
        }

        return result;
    }

    private static MagicCondition ParseCondition(JsonNode node)
    {
        if (node is JsonArray list)
        {
            return new AllCondition([.. list.Select(n => ParseCondition(n!))]);
        }

        if (node is not JsonObject o)
        {
            throw new FormatException("条件はオブジェクトか配列です。");
        }

        if (o["all"] is JsonArray all)
        {
            return new AllCondition([.. all.Select(n => ParseCondition(n!))]);
        }

        if (o["any"] is JsonArray any)
        {
            return new AnyCondition([.. any.Select(n => ParseCondition(n!))]);
        }

        if (o["not"] is { } not)
        {
            return new NotCondition(ParseCondition(not));
        }

        MagicOffset offset = o["offsetFrom"] is JsonObject from
            ? new MagicOffset(from["offset"]?.GetValue<long>() ?? 0, ParseType(from["type"]?.GetValue<string>()), from["add"]?.GetValue<long>() ?? 0)
            : new MagicOffset(o["offset"]?.GetValue<long>() ?? 0);
        int search = o["search"]?.GetValue<int>() ?? 0;
        if (o["bytes"]?.GetValue<string>() is { } hex)
        {
            (byte[] value, bool[] wildcard) = ParseHex(hex);
            return new BytesCondition(offset, value, wildcard, false, search);
        }

        if (o["string"]?.GetValue<string>() is { } text)
        {
            byte[] value = Encoding.UTF8.GetBytes(text);
            return new BytesCondition(offset, value, new bool[value.Length], o["ignoreCase"]?.GetValue<bool>() ?? false, search);
        }

        if (o["type"]?.GetValue<string>() is { } type)
        {
            return new NumberCondition(offset, ParseType(type), ParseOperator(o["op"]?.GetValue<string>() ?? "=="),
                ParseNumber(o["value"]), o["mask"] is { } mask ? ParseNumber(mask) : null);
        }

        throw new FormatException("条件の種類が分かりません: " + o.ToJsonString());
    }

    private static ulong ParseNumber(JsonNode? node)
    {
        if (node is null)
        {
            throw new FormatException("\"value\" がありません。");
        }

        if (node.GetValueKind() == JsonValueKind.String)
        {
            string s = node.GetValue<string>().Trim();
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.Parse(s, CultureInfo.InvariantCulture);
        }

        return node.GetValue<ulong>();
    }

    private static MagicNumberType ParseType(string? s) => s?.ToLowerInvariant() switch
    {
        "u8" => MagicNumberType.U8,
        "u16le" => MagicNumberType.U16LE,
        "u16be" => MagicNumberType.U16BE,
        "u32le" => MagicNumberType.U32LE,
        "u32be" => MagicNumberType.U32BE,
        "u64le" => MagicNumberType.U64LE,
        "u64be" => MagicNumberType.U64BE,
        _ => throw new FormatException("数値の型が分かりません: " + s),
    };

    private static MagicOperator ParseOperator(string s) => s switch
    {
        "==" or "=" => MagicOperator.Equal,
        "!=" => MagicOperator.NotEqual,
        "<" => MagicOperator.Less,
        "<=" => MagicOperator.LessOrEqual,
        ">" => MagicOperator.Greater,
        ">=" => MagicOperator.GreaterOrEqual,
        "&" => MagicOperator.AllBits,
        _ => throw new FormatException("演算子が分かりません: " + s),
    };

    /// <summary>"89 50 ?? 47" (空白は省略可) を読む。</summary>
    internal static (byte[] Value, bool[] Wildcard) ParseHex(string hex)
    {
        string compact = new([.. hex.Where(c => !char.IsWhiteSpace(c))]);
        if (compact.Length == 0 || compact.Length % 2 != 0)
        {
            throw new FormatException("バイト列の桁数が奇数です: " + hex);
        }

        byte[] value = new byte[compact.Length / 2];
        bool[] wildcard = new bool[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            string pair = compact.Substring(i * 2, 2);
            if (pair == "??")
            {
                wildcard[i] = true;
            }
            else
            {
                value[i] = byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        return (value, wildcard);
    }
}
