using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Coloring;
using HexEditor.Core.Engine;

namespace HexEditor.App.ViewModels;

/// <summary>凡例の項目の種類 (INSP-34 の仕様 3)。</summary>
public enum LegendItemKind
{
    Rule,
    Origin,
    Group,
}

/// <summary>凡例の 1 項目: 色見本・枠線の形・名前・表示範囲での一致件数。</summary>
public sealed partial class LegendItem : ObservableObject
{
    public required LegendItemKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>色付けルールの項目のルール (解釈したもの)。</summary>
    public CompiledColoringRule? Rule { get; init; }

    public Microsoft.UI.Xaml.Media.Brush? Swatch { get; init; }

    public Microsoft.UI.Xaml.Media.Brush? Stroke { get; init; }

    /// <summary>枠線の形 (null は実線)。</summary>
    public Microsoft.UI.Xaml.Media.DoubleCollection? Dash { get; init; }

    /// <summary>枠線の形の文字 (「実線」「破線」など。色・形だけに頼らない)。</summary>
    public string ShapeText { get; init; } = string.Empty;

    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TotalText { get; set; } = string.Empty;

    public bool CanNavigate => Kind == LegendItemKind.Rule;

    public string AutomationName => string.Join(", ", new[] { Name, ShapeText, CountText, TotalText }.Where(s => s.Length > 0));

    public string AutomationId => Kind switch
    {
        LegendItemKind.Rule => "Legend_Rule_" + Name,
        LegendItemKind.Origin => "Legend_Origin_" + Name,
        _ => "Legend_Group_" + Name,
    };
}

/// <summary>
/// 凡例 (INSP-34 の仕様 3): 有効な色付けルール、表示中の注釈の出どころ、ブックマークのグループを、色見本・枠線の形・名前で並べる。
/// 各項目に表示範囲での一致件数を出す。
/// </summary>
public sealed partial class LegendViewModel : ObservableObject
{
    public ObservableCollection<LegendItem> Items { get; } = [];

    /// <summary>今のドキュメントの付随データ (ウィンドウが渡す)。</summary>
    public Func<DocumentAnnotations?>? Current { get; set; }

    /// <summary>表示範囲 [開始, 終了) (ウィンドウが渡す)。</summary>
    public Func<(long Start, long End)?>? VisibleRange { get; set; }

    /// <summary>ハイコントラストで色を使わないか (ルールの順に枠線の形を割り当てる。INSP-34 の仕様 4)。</summary>
    public Func<bool>? ShapesOnly { get; set; }

    /// <summary>パネルが表示されている (表示していなければ数えない)。</summary>
    public bool IsActive { get; set; }

    /// <summary>項目を作り直し、表示範囲での件数を数え直す。</summary>
    public void Refresh()
    {
        if (!IsActive || Current?.Invoke() is not { } a)
        {
            Items.Clear();
            return;
        }

        (long start, long end) = VisibleRange?.Invoke() ?? (0, 0);
        DocumentSnapshot snapshot = a.Document.Document.Current;
        bool shapes = ShapesOnly?.Invoke() ?? false;
        var items = new List<LegendItem>();
        IReadOnlyList<CompiledColoringRule> rules = a.Coloring.Rules.Rules;
        for (int i = 0; i < rules.Count; i++)
        {
            ColoringRule rule = rules[i].Rule;
            int shape = shapes ? i : rule.Border switch { ColoringBorder.Dashed => 1, ColoringBorder.Dotted => 2, _ => 0 };
            bool hasBorder = shapes || rule.Border != ColoringBorder.None;
            uint? color = rule.Background ?? rule.Foreground;
            items.Add(new LegendItem
            {
                Kind = LegendItemKind.Rule,
                Name = rule.Name,
                Rule = rules[i],
                Swatch = !shapes && color is { } c ? MainWindow.RuleBrush(c) : null,
                Stroke = hasBorder ? (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"] : null,
                Dash = MainWindow.RuleDash(shape) is { } dash ? [.. dash] : null,
                ShapeText = hasBorder ? Loc.Get("Legend_Shape" + (shape % 4)) : string.Empty,
                CountText = Loc.Format("Legend_Count", ColoringEngine.CountVisible(snapshot, rules[i], start, end)),
            });
        }

        foreach (IAnnotationSource source in a.Layer.Sources.Where(s => s.Origin != AnnotationOrigin.Bookmark && a.Layer.Display.IsVisible(s.Origin)))
        {
            var visible = new List<Annotation>();
            source.Query(start, end, visible);
            string name = Loc.Get("Annotations_Origin_" + source.Origin) + (source.DisplayName.Length > 0 ? " (" + source.DisplayName + ")" : string.Empty);
            items.Add(new LegendItem
            {
                Kind = LegendItemKind.Origin,
                Name = name,
                ShapeText = Loc.Get("Annotations_Style_" + a.Layer.Display.StyleOf(source.Origin)),
                CountText = Loc.Format("Legend_Count", visible.Count),
            });
        }

        if (a.Layer.Display.IsVisible(AnnotationOrigin.Bookmark))
        {
            foreach (BookmarkGroup group in a.Bookmarks.Groups.Where(g => a.Bookmarks.IsGroupVisible(g.Path)).OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase))
            {
                int count = a.Bookmarks.Overlapping(start, end).Count(b => BookmarkGroups.IsWithin(b.Group, group.Path));
                items.Add(new LegendItem
                {
                    Kind = LegendItemKind.Group,
                    Name = group.Path,
                    Swatch = group.Color is { } color ? MainWindow.RuleBrush(color.ExportRgb) : null,
                    CountText = Loc.Format("Legend_Count", count),
                });
            }
        }

        // 同じ項目なら件数だけを書き換える (フォーカスを保つ)。
        if (Items.Count == items.Count && Items.Zip(items).All(p => p.First.Kind == p.Second.Kind && p.First.Name == p.Second.Name && p.First.Rule?.Rule == p.Second.Rule?.Rule))
        {
            for (int i = 0; i < items.Count; i++)
            {
                Items[i].CountText = items[i].CountText;
            }

            return;
        }

        Items.Clear();
        foreach (LegendItem item in items)
        {
            Items.Add(item);
        }
    }

    /// <summary>件数の表示 (全体)。</summary>
    public static string TotalText(long count) => Loc.Format("Legend_Total", count.ToString("N0", CultureInfo.CurrentCulture));
}
