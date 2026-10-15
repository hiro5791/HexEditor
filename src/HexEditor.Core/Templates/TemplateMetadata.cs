using System.Globalization;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates;

/// <summary>
/// 評価の上限 (TPL-17 の仕様 4)。プラグマで上げられるのは <see cref="DefaultMaximum"/> (設定「テンプレートの上限の最大値」) まで。
/// </summary>
public sealed record TemplateLimits
{
    public const long MiB = 1024 * 1024;

    /// <summary>1 つのファイルの大きさの上限 (TPL-01 の仕様 4)。</summary>
    public const int MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>インポートを含めた合計の上限。</summary>
    public const long MaxTotalBytes = 32 * MiB;

    public static TemplateLimits Default { get; } = new();

    /// <summary>設定「テンプレートの上限の最大値」の既定 (既定値の 10 倍、memory は 1 GB)。</summary>
    public static TemplateLimits DefaultMaximum { get; } = new()
    {
        Steps = 1_000_000_000,
        Recursion = 2_560,
        Array = 1_000_000_000,
        Patterns = 100_000_000,
        Memory = 1024 * MiB,
        Section = 2560 * MiB,
        SectionTotal = 10 * 1024 * MiB,
        TimeSeconds = 0,
    };

    public long Steps { get; init; } = 100_000_000;

    public int Recursion { get; init; } = 256;

    public long Array { get; init; } = 100_000_000;

    public long Patterns { get; init; } = 10_000_000;

    public long Memory { get; init; } = 512 * MiB;

    public long Section { get; init; } = 256 * MiB;

    public long SectionTotal { get; init; } = 1024 * MiB;

    /// <summary>評価の時間 (秒)。0 は無制限。</summary>
    public double TimeSeconds { get; init; }
}

/// <summary>マジックの条件 (TPL-17 の仕様 3)。負のオフセットは末尾からの位置。</summary>
public sealed record TemplateMagic(long Offset, byte[] Bytes, byte[] Mask);

/// <summary>自動適用の判定に使うデータ (TPL-30 の仕様 1: 先頭と末尾それぞれ 64 KB と、ファイル形式の判定の結果)。</summary>
public sealed record TemplateMatchInput(long Length, ReadOnlyMemory<byte> Head, ReadOnlyMemory<byte> Tail, string? Extension, string? Mime)
{
    public const int WindowBytes = 64 * 1024;

    public static TemplateMatchInput FromBytes(byte[] data, string? extension = null, string? mime = null)
    {
        int head = Math.Min(data.Length, WindowBytes);
        int tail = Math.Min(data.Length, WindowBytes);
        return new TemplateMatchInput(data.Length, data.AsMemory(0, head), data.AsMemory(data.Length - tail, tail), extension, mime);
    }

    public bool TryRead(long offset, int count, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (offset < 0)
        {
            offset += Length;
        }

        if (offset < 0 || offset + count > Length)
        {
            return false;
        }

        if (offset + count <= Head.Length)
        {
            bytes = Head.Span.Slice((int)offset, count);
            return true;
        }

        long tailStart = Length - Tail.Length;
        if (offset >= tailStart)
        {
            bytes = Tail.Span.Slice((int)(offset - tailStart), count);
            return true;
        }

        return false;
    }
}

/// <summary>自動適用の条件の判定結果 (TPL-30 の仕様 2 の点数)。</summary>
public sealed record TemplateMatch(bool Magic, bool Mime, bool Extension)
{
    public int Score => (Magic ? 3 : 0) + (Mime ? 2 : 0) + (Extension ? 1 : 0);

    /// <summary>拡張子だけの一致は候補にするが自動では適用しない。</summary>
    public bool CanAutoApply => Magic || Mime;
}

/// <summary>テンプレートのメタ情報と自動適用の条件 (TPL-17)。メインのファイルのプラグマだけを使う。</summary>
public sealed class TemplateMetadata
{
    public string? Name { get; internal set; }

    public Dictionary<string, string> LocalizedNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Description { get; internal set; }

    public Dictionary<string, string> LocalizedDescriptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Author { get; internal set; }

    public string? Version { get; internal set; }

    public string? License { get; internal set; }

    public Version? MinAppVersion { get; internal set; }

    /// <summary>既定のエンディアン (true はビッグエンディアン)。</summary>
    public bool? BigEndian { get; internal set; }

    public bool? MsbFirst { get; internal set; }

    public string? Encoding { get; internal set; }

    public List<TemplateMagic> Magic { get; } = [];

    public List<TemplateMagic> MagicAll { get; } = [];

    public List<string> Mimes { get; } = [];

    public List<string> Extensions { get; } = [];

    public int Priority { get; internal set; }

    public bool AutoApply { get; internal set; } = true;

    /// <summary>拡張関数 (<c>extern fn</c>) を使う (TPL-19 の仕様 3。「スクリプトを使用」の表示)。</summary>
    public bool UsesExtensions { get; internal set; }

    /// <summary>Kaitai Struct (.ksy) から読み込んだ (TPL-33 の「画面」の印)。</summary>
    public bool IsKaitai { get; internal set; }

    /// <summary>プラグマで要求した上限 (最大値で切り詰める前)。</summary>
    internal Dictionary<string, (double Value, TemplateSourceLocation At)> RequestedLimits { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 表示名 (TPL-17 の仕様 2)。UI の言語のタグ (例: <c>ja</c>、<c>zh-Hans</c>) に一致する <c>name.&lt;タグ&gt;</c> があれば
    /// それを、なければ言語だけで一致するもの、なければ <c>name</c>、なければファイル名を使う。
    /// </summary>
    public string GetDisplayName(CultureInfo uiCulture, string fileName)
    {
        string? n = Localized(LocalizedNames, uiCulture) ?? Name;
        return string.IsNullOrEmpty(n) ? Path.GetFileNameWithoutExtension(fileName) : n;
    }

    public string? GetDescription(CultureInfo uiCulture) => Localized(LocalizedDescriptions, uiCulture) ?? Description;

    private static string? Localized(Dictionary<string, string> map, CultureInfo culture)
    {
        for (CultureInfo c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (map.TryGetValue(c.Name, out string? v))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>自動適用の条件を調べる (TPL-17 の仕様 3、TPL-30 の仕様 2)。条件を 1 つも持たない場合は null。</summary>
    public TemplateMatch? Match(TemplateMatchInput input)
    {
        if (Magic.Count == 0 && MagicAll.Count == 0 && Mimes.Count == 0 && Extensions.Count == 0)
        {
            return null;
        }

        bool magic = false;
        if (Magic.Count > 0 || MagicAll.Count > 0)
        {
            bool any = Magic.Count == 0 || Magic.Any(m => MatchesMagic(m, input));
            bool all = MagicAll.All(m => MatchesMagic(m, input));
            magic = any && all;
        }

        bool mime = input.Mime is not null && Mimes.Any(m => string.Equals(m, input.Mime, StringComparison.OrdinalIgnoreCase));
        string? ext = input.Extension?.TrimStart('.');
        bool extension = ext is not null && Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        if (!magic && !mime && !extension)
        {
            return null;
        }

        return new TemplateMatch(magic, mime, extension);
    }

    private static bool MatchesMagic(TemplateMagic m, TemplateMatchInput input) =>
        input.TryRead(m.Offset, m.Bytes.Length, out ReadOnlySpan<byte> bytes) && HexPattern.Matches(bytes, m.Bytes, m.Mask);
}
