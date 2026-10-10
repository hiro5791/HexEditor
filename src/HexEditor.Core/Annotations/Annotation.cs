using System.Runtime.CompilerServices;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Annotations;

/// <summary>
/// 注釈の出どころの種類 (INSP-32 の仕様 2)。出どころごとに表示 / 非表示と描き方を切り替える。描く層 (VIEW-17) は
/// ブックマークが層 7、テンプレートが層 9、それ以外 (YARA・すべて検索の結果・スクリプト・プラグイン) が層 8。
/// </summary>
public enum AnnotationOrigin
{
    Bookmark,
    Yara,
    SearchResults,
    Template,
    Script,
}

/// <summary>注釈の描き方 (INSP-32 の仕様 4)。</summary>
public enum AnnotationStyle
{
    Background,
    Border,
    Underline,
}

/// <summary>
/// 注釈 1 つ (INSP-32 の仕様 1): 範囲 (開始・長さ)、ラベル (最大 256 文字)、色 (0xRRGGBB。null は出どころの既定の色)、説明 (Markdown、最大 64 KB)。
/// <see cref="Tag"/> は出どころの機能が使う値 (一致の番号など)。
/// </summary>
public sealed record Annotation(long Start, long Length, string Label, uint? Rgb = null, string Description = "")
{
    public const int MaxLabelLength = 256;
    public const int MaxDescriptionLength = 64 * 1024;

    public long End => Start + Length;

    public object? Tag { get; init; }

    /// <summary>長さ・文字数の上限に合わせたもの。</summary>
    public Annotation Normalized() => this with
    {
        Start = Math.Max(0, Start),
        Length = Math.Max(0, Length),
        Label = Label.Length <= MaxLabelLength ? Label : Label[..MaxLabelLength],
        Description = Description.Length <= MaxDescriptionLength ? Description : Description[..MaxDescriptionLength],
    };

    /// <summary>offset のバイトを含むか (長さ 0 の注釈はその位置だけ)。</summary>
    public bool Contains(long offset) => Length == 0 ? offset == Start : offset >= Start && offset < End;

    /// <summary>[start, end) と重なるか。</summary>
    public bool Overlaps(long start, long end) => Length == 0 ? Start >= start && Start < end : Start < end && End > start;
}

/// <summary>
/// 注釈の出どころ (共通の注釈レイヤーに登録するもの)。出どころの機能 (YARA、テンプレート、スクリプトなど) が実装するか、
/// <see cref="AnnotationSet"/> を使う。<see cref="Query"/> は表示のたびに UI スレッドから呼ぶので、ブロックしないこと。
/// </summary>
public interface IAnnotationSource
{
    /// <summary>ドキュメントの中で一意の名前 (例: <c>yara</c>、<c>script:annotate.js</c>)。</summary>
    string Id { get; }

    AnnotationOrigin Origin { get; }

    /// <summary>凡例・ツールチップに出す名前 (例: ルールファイルの名前)。空なら出どころの種類の名前。</summary>
    string DisplayName { get; }

    /// <summary>[start, end) と重なる注釈を <paramref name="output"/> に加える。</summary>
    void Query(long start, long end, List<Annotation> output);

    /// <summary>注釈が変わった (描き直す)。</summary>
    event EventHandler? Changed;
}

/// <summary>表示している注釈 1 つと、重なりの段 (0 が最も外側。INSP-32 の仕様 4)。</summary>
public readonly record struct PlacedAnnotation(Annotation Annotation, IAnnotationSource Source, int Level);

/// <summary>注釈の列 (INSP-32 の仕様 4) の 1 行分: その行で始まる注釈の先頭のラベルと、ほかの件数。</summary>
public readonly record struct AnnotationRowLabel(string Label, int More, IAnnotationSource Source);

/// <summary>
/// 出どころごとの表示の設定 (アプリ全体で共通。表示 > 注釈 のサブメニュー)。表示 / 非表示と描き方。
/// </summary>
public sealed class AnnotationDisplay
{
    private readonly HashSet<AnnotationOrigin> _hidden = [];
    private readonly Dictionary<AnnotationOrigin, AnnotationStyle> _styles = [];

    public event EventHandler? Changed;

    /// <summary>注釈の列を表示する (INSP-32 の仕様 4。既定は非表示)。</summary>
    public bool ShowColumn { get; private set; }

    public bool IsVisible(AnnotationOrigin origin) => !_hidden.Contains(origin);

    /// <summary>描き方 (既定はブックマークとテンプレートが背景色、YARA・検索の結果・スクリプトが枠線)。</summary>
    public AnnotationStyle StyleOf(AnnotationOrigin origin) =>
        _styles.TryGetValue(origin, out AnnotationStyle style) ? style : DefaultStyle(origin);

    public static AnnotationStyle DefaultStyle(AnnotationOrigin origin) =>
        origin is AnnotationOrigin.Bookmark or AnnotationOrigin.Template ? AnnotationStyle.Background : AnnotationStyle.Border;

    public void SetVisible(AnnotationOrigin origin, bool visible)
    {
        if (visible ? _hidden.Remove(origin) : _hidden.Add(origin))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetStyle(AnnotationOrigin origin, AnnotationStyle style)
    {
        if (StyleOf(origin) != style)
        {
            _styles[origin] = style;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetShowColumn(bool show)
    {
        if (ShowColumn != show)
        {
            ShowColumn = show;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>非表示の出どころ (保存用)。</summary>
    public IReadOnlyCollection<AnnotationOrigin> Hidden => _hidden;

    /// <summary>描き方を変えた出どころ (保存用)。</summary>
    public IReadOnlyDictionary<AnnotationOrigin, AnnotationStyle> Styles => _styles;
}

/// <summary>
/// 共通の注釈レイヤー (INSP-32)。ドキュメント 1 つにつき 1 つ (<see cref="For"/>)。出どころを登録し、表示範囲の注釈を重なりの段付きで
/// 取り出す。出どころごとに区間の索引を持つので、注釈が合計 1,000 万件あっても表示範囲の取り出しは件数によらない時間で済む。
/// <para>使い方 (YARA・テンプレート・スクリプトなどの機能):</para>
/// <code>
/// var set = new AnnotationSet("yara", AnnotationOrigin.Yara, "rules.yar");
/// set.AddRange(matches.Select(m =&gt; new Annotation(m.Offset, m.Length, m.Rule, null, m.Description)));
/// AnnotationLayer.For(document).Register(set);   // 表示される
/// AnnotationLayer.For(document).Unregister("yara");  // 結果一覧を閉じたら消す (INSP-32 の仕様 3)
/// </code>
/// </summary>
public sealed class AnnotationLayer
{
    /// <summary>重なりを線で区別する段の上限 (INSP-32 の仕様 4)。超える分は描かない (ツールチップには出す)。</summary>
    public const int MaxLevels = 4;

    private static readonly ConditionalWeakTable<Document, AnnotationLayer> Attached = new();

    private readonly List<IAnnotationSource> _sources = [];

    public AnnotationLayer(AnnotationDisplay? display = null)
    {
        Display = display ?? SharedDisplay;
        Display.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>アプリ全体の表示の設定 (表示 > 注釈)。</summary>
    public static AnnotationDisplay SharedDisplay { get; } = new();

    /// <summary>ドキュメントの注釈レイヤー (なければ作る)。</summary>
    public static AnnotationLayer For(Document document) => Attached.GetValue(document, _ => new AnnotationLayer());

    /// <summary>ドキュメントの注釈レイヤー (作らない)。</summary>
    public static AnnotationLayer? Find(Document document) => Attached.TryGetValue(document, out AnnotationLayer? layer) ? layer : null;

    public AnnotationDisplay Display { get; }

    /// <summary>出どころ・注釈・表示の設定が変わった (描き直す)。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<IAnnotationSource> Sources => _sources;

    /// <summary>出どころを登録する。同じ ID があれば置き換える。</summary>
    public void Register(IAnnotationSource source)
    {
        int index = _sources.FindIndex(s => s.Id == source.Id);
        if (index >= 0)
        {
            _sources[index].Changed -= Source_Changed;
            _sources[index] = source;
        }
        else
        {
            _sources.Add(source);
        }

        source.Changed += Source_Changed;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string id)
    {
        int index = _sources.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            return false;
        }

        _sources[index].Changed -= Source_Changed;
        _sources.RemoveAt(index);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IAnnotationSource? Find(string id) => _sources.FirstOrDefault(s => s.Id == id);

    private void Source_Changed(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>表示している出どころ。</summary>
    private IEnumerable<IAnnotationSource> VisibleSources(Func<AnnotationOrigin, bool>? origins) =>
        _sources.Where(s => Display.IsVisible(s.Origin) && (origins is null || origins(s.Origin)));

    /// <summary>
    /// [start, end) と重なる、表示している出どころの注釈と重なりの段 (INSP-32 の仕様 4)。段は、同じ層 (出どころの種類の組) の中で
    /// その注釈を含むほかの注釈の数 (外側が 0)。内側の範囲ほど後ろ (上) に並べる。段が 4 以上のものは除く。
    /// </summary>
    public IReadOnlyList<PlacedAnnotation> QueryVisible(long start, long end, Func<AnnotationOrigin, bool>? origins = null)
    {
        var items = new List<(Annotation A, IAnnotationSource S)>();
        var buffer = new List<Annotation>();
        foreach (IAnnotationSource source in VisibleSources(origins))
        {
            buffer.Clear();
            source.Query(start, end, buffer);
            foreach (Annotation a in buffer)
            {
                items.Add((a, source));
            }
        }

        // 外側 (長い) から内側の順。同じ長さは開始位置の順。
        items.Sort(static (x, y) => x.A.Length != y.A.Length ? y.A.Length.CompareTo(x.A.Length) : x.A.Start.CompareTo(y.A.Start));
        var placed = new List<PlacedAnnotation>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Annotation a = items[i].A;
            int level = 0;
            for (int j = 0; j < i && level < MaxLevels; j++)
            {
                Annotation outer = items[j].A;
                if (outer.Start <= a.Start && outer.End >= a.End && SameLayer(items[j].S.Origin, items[i].S.Origin))
                {
                    level++;
                }
            }

            if (level < MaxLevels)
            {
                placed.Add(new PlacedAnnotation(a, items[i].S, level));
            }
        }

        return placed;
    }

    /// <summary>同じ層 (VIEW-17) に描く出どころか。</summary>
    private static bool SameLayer(AnnotationOrigin a, AnnotationOrigin b) => LayerOf(a) == LayerOf(b);

    /// <summary>VIEW-17 の層の順位 (ブックマーク 7、注釈 8、テンプレート 9)。</summary>
    public static int LayerOf(AnnotationOrigin origin) => origin switch
    {
        AnnotationOrigin.Bookmark => 7,
        AnnotationOrigin.Template => 9,
        _ => 8,
    };

    /// <summary>
    /// そのバイトを含むすべての注釈 (表示している出どころ。段の上限を超えたものも含む。ツールチップと「カーソル位置の説明を表示」。
    /// INSP-32 の仕様 5)。内側の範囲ほど先。
    /// </summary>
    public IReadOnlyList<(Annotation Annotation, IAnnotationSource Source)> At(long offset)
    {
        var result = new List<(Annotation, IAnnotationSource)>();
        var buffer = new List<Annotation>();
        foreach (IAnnotationSource source in VisibleSources(null))
        {
            buffer.Clear();
            source.Query(offset, offset + 1, buffer);
            foreach (Annotation a in buffer.Where(a => a.Contains(offset)))
            {
                result.Add((a, source));
            }
        }

        result.Sort(static (x, y) => x.Item1.Length.CompareTo(y.Item1.Length));
        return result;
    }

    /// <summary>
    /// 注釈の列 (INSP-32 の仕様 4): その行 [rowStart, rowEnd) で始まる注釈の、先頭のラベルとほかの件数。注釈がなければ null。
    /// 先頭は開始位置が最も前のもの (同じなら外側)。
    /// </summary>
    public AnnotationRowLabel? RowLabel(long rowStart, long rowEnd, Func<AnnotationOrigin, bool>? origins = null)
    {
        var starting = new List<(Annotation A, IAnnotationSource S)>();
        var buffer = new List<Annotation>();
        foreach (IAnnotationSource source in VisibleSources(origins))
        {
            buffer.Clear();
            source.Query(rowStart, rowEnd, buffer);
            foreach (Annotation a in buffer.Where(a => a.Start >= rowStart && a.Start < rowEnd))
            {
                starting.Add((a, source));
            }
        }

        if (starting.Count == 0)
        {
            return null;
        }

        (Annotation first, IAnnotationSource firstSource) = starting.OrderBy(x => x.A.Start).ThenByDescending(x => x.A.Length).First();
        return new AnnotationRowLabel(first.Label, starting.Count - 1, firstSource);
    }
}
