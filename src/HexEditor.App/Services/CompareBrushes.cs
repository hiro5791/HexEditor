using HexEditor.Core.Compare;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Services;

/// <summary>
/// 差分の色と形 (ANA-04 の仕様 3、Themes/ComparePalette.xaml)。背景は配色 (UI-28) の「差分 (追加・削除・変更)」に値があればその色、
/// なければテーマの色。ハイコントラストでは配色を使わず、システム色の背景と色以外の印で区別する。
/// </summary>
public static class CompareBrushes
{
    /// <summary>「変更」の点線の枠の模様 (同じ配列を返す。描画で、変わったときだけ設定し直すため)。</summary>
    public static readonly IReadOnlyList<double> ChangedDash = [1, 2];

    /// <summary>揃えるための空白 (挿入・削除の相手側) の資源名。</summary>
    public const string PaddingKey = "ComparePaddingBrush";

    /// <summary>差分の種類の背景の資源名。</summary>
    public static string BackgroundKey(DiffKind kind) => kind switch
    {
        DiffKind.Changed => "CompareChangedBrush",
        DiffKind.Inserted => "CompareInsertedBrush",
        DiffKind.Deleted => "CompareDeletedBrush",
        _ => "CompareUnreadableBrush",
    };

    /// <summary>差分の種類の印 (差分マップ・縦線・取り消し線・枠) の資源名。</summary>
    public static string MarkKey(DiffKind kind) => kind switch
    {
        DiffKind.Changed => "CompareChangedMarkBrush",
        DiffKind.Inserted => "CompareInsertedMarkBrush",
        DiffKind.Deleted => "CompareDeletedMarkBrush",
        _ => "CompareUnreadableMarkBrush",
    };

    /// <summary>種類ごとの色以外の印 (仕様 3 の表)。変更は点線の枠 (<see cref="ChangedDash"/>) で、HexMark では表さない。</summary>
    public static Controls.HexMark MarkOf(DiffKind kind) => kind switch
    {
        DiffKind.Inserted => Controls.HexMark.LeftBar,
        DiffKind.Deleted => Controls.HexMark.Strike,
        DiffKind.Unreadable => Controls.HexMark.Hatch,
        _ => Controls.HexMark.None,
    };

    public static Brush Background(DiffKind kind, FrameworkElement scope, bool highContrast, ColorScheme? scheme)
    {
        if (!highContrast && scheme is not null && kind != DiffKind.Unreadable)
        {
            SchemeElement element = kind switch
            {
                DiffKind.Changed => SchemeElement.DiffChanged,
                DiffKind.Inserted => SchemeElement.DiffAdded,
                _ => SchemeElement.DiffRemoved,
            };
            if (scheme.Get(element, scope.ActualTheme == ElementTheme.Dark) is { } c)
            {
                return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(c.A, c.R, c.G, c.B));
            }
        }

        return AnnotationBrushes.Get(BackgroundKey(kind), scope, highContrast);
    }

    public static Brush Mark(DiffKind kind, FrameworkElement scope, bool highContrast) =>
        AnnotationBrushes.Get(MarkKey(kind), scope, highContrast);

    public static Brush Padding(FrameworkElement scope, bool highContrast) => AnnotationBrushes.Get(PaddingKey, scope, highContrast);

    public static Brush PaddingMark(FrameworkElement scope, bool highContrast) => AnnotationBrushes.Get("ComparePaddingMarkBrush", scope, highContrast);
}
