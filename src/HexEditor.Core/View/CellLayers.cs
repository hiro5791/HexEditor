namespace HexEditor.Core.View;

/// <summary>Hex ビューのセルに付く強調表示の層 (VIEW-17 の仕様 5 の表)。値が小さいほど手前 (優先)。</summary>
public enum CellLayer
{
    /// <summary>カーソルと対応位置 (VIEW-06)。</summary>
    Cursor = 1,

    /// <summary>選択範囲 (マルチ選択を含む。EDIT-01〜EDIT-07)。</summary>
    Selection = 2,

    /// <summary>注目している範囲 (INSP-18、ANA-23、TPL-26、TPL-32)。</summary>
    Focus = 3,

    /// <summary>現在の検索の一致 (FIND-12)。</summary>
    CurrentMatch = 4,

    /// <summary>その他の検索の一致 (FIND-12)。</summary>
    Match = 5,

    /// <summary>変更されたバイト (VIEW-15)。</summary>
    Modified = 6,

    /// <summary>ブックマーク (INSP-23)。</summary>
    Bookmark = 7,

    /// <summary>注釈 (INSP-32)。</summary>
    Annotation = 8,

    /// <summary>テンプレートの範囲の色分け (TPL-23)。</summary>
    Template = 9,

    /// <summary>色付けルール (INSP-33)。</summary>
    ColorRule = 10,

    /// <summary>差分 (ANA-02〜ANA-04、VIEW-39 の「違いを強調」)。</summary>
    Difference = 11,

    /// <summary>バイトテーマ (VIEW-17)。</summary>
    ByteTheme = 12,

    /// <summary>ゼロのグレー表示 (VIEW-13)。</summary>
    Zero = 13,

    /// <summary>現在行 (VIEW-06)。</summary>
    CurrentRow = 14,

    /// <summary>レコードの交互色 (VIEW-18)。</summary>
    Record = 15,

    /// <summary>列の交互色 (VIEW-14)。</summary>
    AlternateColumn = 16,

    /// <summary>通常の背景・文字色 (VIEW-01)。</summary>
    Normal = 17,
}

/// <summary>セルに重ねる枠・線・模様の種類 (VIEW-17 の仕様 5 の「枠・線・模様」の列)。種類が違えば同時に描く。</summary>
public enum CellDecorationKind
{
    /// <summary>枠 (カーソル、注目している範囲、検索の一致、色付けルール)。</summary>
    Frame,

    /// <summary>変更されたバイトの下線 (実線・2 本線・点線)。</summary>
    Underline,

    /// <summary>
    /// セルの下端の高さ 2 px の帯 (ブックマーク・注釈・テンプレート・色付けルールの背景が、選択範囲・検索の一致に隠れたとき。仕様 6)。
    /// </summary>
    Band,

    /// <summary>読み取れない範囲の斜線 (VIEW-03。ほかの層の背景の上に必ず描く。仕様 7)。</summary>
    Hatch,

    /// <summary>差分の種類ごとの模様 (層 11)。</summary>
    Pattern,

    /// <summary>行・境界の線 (現在行・レコード・列の交互色のハイコントラストの代わり)。</summary>
    Line,

    /// <summary>挿入モードのカーソルの縦棒。</summary>
    Bar,
}

/// <summary>1 つの層がセルに付けるもの。色は指定しなければ null。<see cref="ValueDependent"/> は値で決まる層 (仕様 7)。</summary>
public readonly record struct LayerPaint(CellLayer Layer, SchemeColor? Background = null, SchemeColor? Foreground = null,
    CellDecorationKind? Decoration = null, SchemeColor? DecorationColor = null, bool ValueDependent = false);

/// <summary>セルに描く枠・線・模様 1 つ (描く順に並べる)。</summary>
public readonly record struct CellDecoration(CellDecorationKind Kind, CellLayer Layer, SchemeColor? Color);

/// <summary>重ねた結果のセルの見た目。</summary>
public sealed record CellAppearance(SchemeColor? Background, CellLayer? BackgroundLayer, SchemeColor? Foreground, CellLayer? ForegroundLayer,
    IReadOnlyList<CellDecoration> Decorations);

/// <summary>
/// 色の重ね順 (VIEW-17 の仕様 5〜7) に従ってセルの見た目を決める。Hex ビューの描画 (HexView) はこの規則を背景・文字・図形の重ね方で
/// 実現する。ここは規則そのものを 1 か所で定める部品 (単体テストの対象)。
/// </summary>
public static class CellLayers
{
    /// <summary>背景が隠れたときに下端の帯として残す層 (層 7〜10)。</summary>
    public static bool KeepsBand(CellLayer layer) => layer is >= CellLayer.Bookmark and <= CellLayer.ColorRule;

    /// <summary>帯を残す原因になる、手前の背景の層 (層 2〜5)。</summary>
    public static bool HidesWithBand(CellLayer layer) => layer is >= CellLayer.Selection and <= CellLayer.Match;

    /// <summary>
    /// 層を重ねる。<paramref name="unreadable"/> は読み取れないバイト (値で決まる層を使わず、斜線の模様を背景の上に描く)。
    /// </summary>
    public static CellAppearance Resolve(IEnumerable<LayerPaint> paints, bool unreadable = false)
    {
        var ordered = paints.Where(p => !(unreadable && p.ValueDependent)).OrderBy(p => (int)p.Layer).ToList();
        LayerPaint? back = ordered.FirstOrDefault(p => p.Background is not null) is { Background: not null } b ? b : null;
        LayerPaint? fore = ordered.FirstOrDefault(p => p.Foreground is not null) is { Foreground: not null } f ? f : null;

        // 描く順 (奥から): 斜線 → 帯 → 枠・線・模様 (同じ種類は手前の層のもの) → 下線。
        var decorations = new List<CellDecoration>();
        if (unreadable)
        {
            decorations.Add(new CellDecoration(CellDecorationKind.Hatch, CellLayer.Normal, null));
        }

        // 層 7〜10 の背景が層 2〜5 の背景に隠れたら、下端に帯として残す (仕様 6)。
        if (back is { } shown && HidesWithBand(shown.Layer)
            && ordered.FirstOrDefault(p => KeepsBand(p.Layer) && p.Background is not null) is { Background: { } bandColor } hidden)
        {
            decorations.Add(new CellDecoration(CellDecorationKind.Band, hidden.Layer, bandColor));
        }

        var seen = new HashSet<CellDecorationKind>();
        foreach (LayerPaint p in ordered)
        {
            if (p.Decoration is { } kind && kind != CellDecorationKind.Underline && seen.Add(kind))
            {
                decorations.Add(new CellDecoration(kind, p.Layer, p.DecorationColor));
            }
        }

        // 変更されたバイトの下線は、どの層が重なっても必ず描く (仕様 6)。帯のすぐ下になるよう最後に置く。
        if (ordered.FirstOrDefault(p => p.Layer == CellLayer.Modified) is { Layer: CellLayer.Modified } modified)
        {
            decorations.Add(new CellDecoration(CellDecorationKind.Underline, CellLayer.Modified, modified.DecorationColor ?? modified.Foreground));
        }

        return new CellAppearance(back?.Background, back?.Layer, fore?.Foreground, fore?.Layer, decorations);
    }
}
