using System.Globalization;

namespace HexEditor.Core.Templates.Presentation;

/// <summary>色分けの色の種類 (TPL-13 の <c>color</c> 属性、TPL-23 の仕様 2)。</summary>
public enum TemplateColorKind
{
    /// <summary>自動の色、または <c>accent1</c>〜<c>accent12</c> (テーマごとに定義した 12 色。<see cref="TemplateColor.Index"/> が 0〜11)。</summary>
    Accent,

    /// <summary>名前付きの色 (<c>red</c>〜<c>gray</c>。<see cref="TemplateColor.Index"/> が <see cref="TemplateColoring.NamedColors"/> の位置)。</summary>
    Named,

    /// <summary><c>#RRGGBB</c> (ハイコントラストでは使わない)。</summary>
    Custom,
}

/// <summary>色分けの色 1 つ。<see cref="Explicit"/> は <c>color</c> 属性で指定した色 (隣どうしが同じでも変えない)。</summary>
public readonly record struct TemplateColor(TemplateColorKind Kind, int Index, uint Rgb, bool Explicit)
{
    public static TemplateColor Auto(int index) => new(TemplateColorKind.Accent, ((index % TemplateColoring.AutoColors) + TemplateColoring.AutoColors) % TemplateColoring.AutoColors, 0, false);

    /// <summary>リソースの名前の一部 (<c>Accent3</c>、<c>Red</c>) か <c>#RRGGBB</c>。</summary>
    public string Name => Kind switch
    {
        TemplateColorKind.Accent => "Accent" + (Index + 1).ToString(CultureInfo.InvariantCulture),
        TemplateColorKind.Named => char.ToUpperInvariant(TemplateColoring.NamedColors[Index][0]) + TemplateColoring.NamedColors[Index][1..],
        _ => "#" + Rgb.ToString("X6", CultureInfo.InvariantCulture),
    };

    /// <summary>同じ見た目の色か (自動の色と <c>accentN</c> は同じ 12 色を使う)。</summary>
    public bool SameAs(TemplateColor other) => Kind == other.Kind && (Kind == TemplateColorKind.Custom ? Rgb == other.Rgb : Index == other.Index);
}

/// <summary>色分けの範囲 1 つ (重なりを解いた後。最も深いパターンの色。TPL-23 の仕様 1)。</summary>
public sealed record TemplatePaintSegment(long Start, long Length, TemplateColor Color, TemplateNode Node)
{
    public long End => Start + Length;
}

/// <summary>
/// テンプレートの色分け (TPL-23)。表示範囲に重なるパターンだけを評価器から取り (<see cref="TemplateResult.NodesInRange"/>)、重なりを解いて
/// 色の付いた範囲の並びにする:
/// <list type="bullet">
/// <item>重なる場合は最も深い (子の) パターン、親子でないパターンどうしは後に宣言したパターンの色 (TPL-09 の仕様 6)。</item>
/// <item>自動の色は 12 色を順に使い (兄弟の順番で 1 つずつずらす)、互いに接する範囲が同じ色になる場合は色を半周ずらす (仕様 2)。</item>
/// <item><c>no_color</c> は塗らない (親の色が見える)。<c>highlight_hidden</c> は部分木ごと塗らない。</item>
/// </list>
/// 区間の索引は評価器が持つ (固定長の要素の配列は重なる要素だけを作る) ので、表示範囲の大きさに比例する時間で済む。
/// </summary>
public static class TemplateColoring
{
    /// <summary>自動の色の数 (仕様 2)。</summary>
    public const int AutoColors = 12;

    /// <summary>既定の不透明度 (仕様 3。設定で変えられる)。</summary>
    public const double DefaultOpacity = 0.35;

    /// <summary>名前付きの色 (TPL-13 の <c>color</c> 属性)。</summary>
    public static IReadOnlyList<string> NamedColors { get; } = ["red", "orange", "yellow", "green", "teal", "blue", "purple", "pink", "gray"];

    /// <summary>直前の範囲との比較のために、表示範囲より前から求める量 (色を半周ずらす判定を表示範囲によらず同じにする)。</summary>
    public const int LookBehind = 256;

    /// <summary>1 回に塗る範囲の上限 (バイト)。これを超える表示範囲は呼び出し側が分けて求める。</summary>
    public const int MaxSpan = 4 * 1024 * 1024;

    /// <summary>
    /// [<paramref name="start"/>, <paramref name="end"/>) に重なる色分けの範囲 (オフセットの順)。評価器のスレッドで評価する
    /// (UI スレッドからは <see cref="TemplateResult.InvokeAsync{T}(Func{T})"/> で呼ぶ)。
    /// </summary>
    public static IReadOnlyList<TemplatePaintSegment> Segments(TemplateResult result, long start, long end, TemplateSection? section = null) =>
        result.Invoke(() => SegmentsCore(result, start, end, section));

    private static List<TemplatePaintSegment> SegmentsCore(TemplateResult result, long start, long end, TemplateSection? section)
    {
        var output = new List<TemplatePaintSegment>();
        long from = Math.Max(0, start - LookBehind);
        long to = Math.Min(end, from + MaxSpan);
        if (to <= from)
        {
            return output;
        }

        IReadOnlyList<(TemplateNode Node, int Depth)> nodes = result.NodesInRange(from, to - from, section, maxDepth: 32, maxNodes: 200_000);
        int span = (int)(to - from);
        var owners = new int[span];
        Array.Fill(owners, -1);
        var painted = new List<(TemplateNode Node, TemplateColor Color)>();
        var colors = new Dictionary<TemplateNode, TemplateColor>(ReferenceEqualityComparer.Instance);
        var skipped = new HashSet<TemplateNode>(ReferenceEqualityComparer.Instance);
        foreach ((TemplateNode node, _) in nodes)
        {
            // 前から順 (親が子より先、先に宣言したものが先) に塗り重ねる
            if (node.Parent is { } p && skipped.Contains(p))
            {
                skipped.Add(node);
                continue;
            }

            if (node.Attributes.HighlightHidden)
            {
                skipped.Add(node);
                continue;
            }

            if (node.Kind is TemplateNodeKind.Computed or TemplateNodeKind.Error or TemplateNodeKind.Section || node.Size <= 0)
            {
                continue;
            }

            TemplateColor color = ColorOf(node, colors);
            if (node.Attributes.NoColor)
            {
                continue;
            }

            long a = Math.Max(node.Offset, from);
            long b = Math.Min(node.End, to);
            if (a >= b)
            {
                continue;
            }

            int id = painted.Count;
            painted.Add((node, color));
            owners.AsSpan((int)(a - from), (int)(b - a)).Fill(id);
        }

        // 同じ持ち主の続く範囲をまとめる
        int i = 0;
        TemplatePaintSegment? previous = null;
        while (i < span)
        {
            int owner = owners[i];
            int j = i + 1;
            while (j < span && owners[j] == owner)
            {
                j++;
            }

            if (owner >= 0)
            {
                (TemplateNode node, TemplateColor color) = painted[owner];
                if (previous is not null && previous.End == from + i && !color.Explicit && color.SameAs(previous.Color))
                {
                    // 自動の色は兄弟の順に 1 つずつずれるので、半周ずらせば次の範囲の色とも重ならない
                    color = TemplateColor.Auto(color.Index + (AutoColors / 2));
                }

                var segment = new TemplatePaintSegment(from + i, j - i, color, node);
                if (segment.End > start)
                {
                    output.Add(segment);
                }

                previous = segment;
            }
            else
            {
                previous = null;
            }

            i = j;
        }

        return output;
    }

    /// <summary>
    /// パターンの色: <c>color</c> 属性 (<c>accentN</c>・名前付き・<c>#RRGGBB</c>) があればそれ、なければ自動の色
    /// (親の色の次から、兄弟の順番だけずらす。隣の兄弟は必ず違う色になる)。
    /// </summary>
    private static TemplateColor ColorOf(TemplateNode node, Dictionary<TemplateNode, TemplateColor> cache)
    {
        if (cache.TryGetValue(node, out TemplateColor known))
        {
            return known;
        }

        TemplateColor color;
        if (Parse(node.Attributes.Color) is { } explicitColor)
        {
            color = explicitColor;
        }
        else
        {
            TemplateNode? parent = node.Parent;
            int parentIndex = parent is null || parent.Kind == TemplateNodeKind.Application ? -1 : ColorOf(parent, cache).Index;
            long ordinal = node.ArrayIndex >= 0 ? node.ArrayIndex : OrdinalOf(node);
            color = TemplateColor.Auto((int)((parentIndex + 1 + (ordinal % AutoColors)) % AutoColors));
        }

        cache[node] = color;
        return color;
    }

    private static long OrdinalOf(TemplateNode node)
    {
        if (node.Parent is not { } parent)
        {
            return 0;
        }

        IReadOnlyList<TemplateNode> siblings = parent.Children;
        int n = Math.Min(siblings.Count, 100_000);
        for (int i = 0; i < n; i++)
        {
            if (ReferenceEquals(siblings[i], node))
            {
                return i;
            }
        }

        // 見つからない (捨てた部分木を作り直した) 場合はオフセットから決める (同じ位置なら同じ色)
        return node.Offset;
    }

    /// <summary><c>color("…")</c> の値を色にする。不明な名前は null (自動の色)。</summary>
    public static TemplateColor? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string n = name.Trim();
        if (n.StartsWith("accent", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(n.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int accent) && accent is >= 1 and <= AutoColors)
        {
            return new TemplateColor(TemplateColorKind.Accent, accent - 1, 0, true);
        }

        for (int i = 0; i < NamedColors.Count; i++)
        {
            if (string.Equals(NamedColors[i], n, StringComparison.OrdinalIgnoreCase))
            {
                return new TemplateColor(TemplateColorKind.Named, i, 0, true);
            }
        }

        if (n.Length == 7 && n[0] == '#' && uint.TryParse(n.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return new TemplateColor(TemplateColorKind.Custom, 0, rgb, true);
        }

        return null;
    }

    /// <summary>
    /// 不透明度 <paramref name="opacity"/> で背景に重ねた色 (0xRRGGBB)。テーマの色の値から Hex ビューの背景の色を求める
    /// (コントラストの検査 TC-TPL-13-03 と描画で共通)。
    /// </summary>
    public static uint Blend(uint rgb, uint background, double opacity)
    {
        static byte Mix(uint fg, uint bg, double a) => (byte)Math.Round((fg * a) + (bg * (1 - a)));
        double a = Math.Clamp(opacity, 0, 1);
        return ((uint)Mix((rgb >> 16) & 0xFF, (background >> 16) & 0xFF, a) << 16)
            | ((uint)Mix((rgb >> 8) & 0xFF, (background >> 8) & 0xFF, a) << 8)
            | Mix(rgb & 0xFF, background & 0xFF, a);
    }

    /// <summary>WCAG のコントラスト比。</summary>
    public static double ContrastRatio(uint rgb1, uint rgb2)
    {
        static double Channel(uint c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double Luminance(uint rgb) =>
            (0.2126 * Channel((rgb >> 16) & 0xFF)) + (0.7152 * Channel((rgb >> 8) & 0xFF)) + (0.0722 * Channel(rgb & 0xFF));

        double l1 = Luminance(rgb1);
        double l2 = Luminance(rgb2);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
