using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>ミニマップ (VIEW-35) を Hex ビューの縦スクロールバーの左に置く。表示内容・範囲・幅はウィンドウが設定から渡す。</summary>
public sealed partial class HexView
{
    private MinimapView? _minimap;

    /// <summary>ミニマップ (表示したことがなければ null)。</summary>
    internal MinimapView? Minimap => _minimap;

    /// <summary>ミニマップを表示する (VIEW-35。既定は非表示)。</summary>
    public bool MinimapVisible
    {
        get => _minimap?.Visibility == Visibility.Visible;
        set
        {
            if (value == MinimapVisible)
            {
                return;
            }

            if (value)
            {
                EnsureMinimap();
                _minimap!.Visibility = Visibility.Visible;
                _minimap.Editor = _editor;
                _minimap.Restart(force: true);
            }
            else if (_minimap is not null)
            {
                _minimap.Visibility = Visibility.Collapsed;
                _minimap.Editor = null;
            }
        }
    }

    /// <summary>ミニマップの幅 (40〜200 epx、既定 80。仕様 1)。</summary>
    public double MinimapWidth
    {
        get => _minimapWidth;
        set
        {
            _minimapWidth = Math.Clamp(value, 40, 200);
            if (_minimap is not null)
            {
                _minimap.Width = _minimapWidth;
            }
        }
    }

    private double _minimapWidth = 80;

    /// <summary>ミニマップの表示内容と範囲を設定する。</summary>
    public void SetMinimapMode(MinimapContent content, MinimapRange range)
    {
        EnsureMinimap();
        _minimap!.Content = content;
        _minimap.Range = range;
    }

    /// <summary>表示しない印 (VIEW-35 の仕様 6。cursor / selection / search / bookmark / modified / difference)。</summary>
    public IReadOnlySet<string> HiddenMinimapMarks { get; set; } = new HashSet<string>();

    /// <summary>差分の範囲の提供元 (比較 ANA が設定する。[start, end) に重なる差分)。null なら差分の印はない。</summary>
    public Func<IEnumerable<(long Offset, long Length)>>? MinimapDifferences { get; set; }

    /// <summary>ミニマップの右クリックメニューの項目 (ウィンドウが作る)。</summary>
    public Action<HexView, MenuFlyout>? MinimapMenuOpening { get; set; }

    private void EnsureMinimap()
    {
        if (_minimap is not null)
        {
            return;
        }

        _minimap = new MinimapView { Width = _minimapWidth, Visibility = Visibility.Collapsed };
        Grid.SetRow(_minimap, 1);
        Grid.SetColumn(_minimap, 1);
        ((Grid)Content).Children.Add(_minimap);
        _minimap.Marks = MinimapMarksNow;
        _minimap.HighContrast = IsHighContrast;
        _minimap.ByteTheme = _byteTheme;
        _minimap.MenuOpening = menu => MinimapMenuOpening?.Invoke(this, menu);
        _minimap.Navigated += (_, _) => Focus(FocusState.Programmatic);
    }

    /// <summary>ミニマップの印の今の位置。</summary>
    private MinimapMarks MinimapMarksNow()
    {
        EditorState? editor = _editor;
        Palette? palette = _palette;
        var bookmarks = new List<(long, Brush)>();
        var modified = new List<(long, long)>();
        if (editor is not null && _minimap is { ActualHeight: > 0 } minimap)
        {
            if (!HiddenMinimapMarks.Contains("bookmark") && BookmarkMarkerSource is { } source)
            {
                // ピクセル行ごとに、その範囲から始まるブックマークを問い合わせる (件数によらない)。
                MinimapComputer c = minimap.Computer;
                for (int i = 0; i < c.RowCount && bookmarks.Count < 2000; i++)
                {
                    (long start, long length) = c.RangeOf(i);
                    if (source(start, start + Math.Max(1, length)) is { } brush)
                    {
                        bookmarks.Add((start, brush));
                    }
                }
            }

            if (!HiddenMinimapMarks.Contains("modified"))
            {
                foreach ((long offset, long length) in editor.Document.Current.EnumerateModifiedRanges())
                {
                    modified.Add((offset, length));
                    if (modified.Count >= 2000)
                    {
                        break;
                    }
                }
            }
        }

        Brush fallback = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        return new MinimapMarks(
            !HiddenMinimapMarks.Contains("cursor"),
            !HiddenMinimapMarks.Contains("selection"),
            !HiddenMinimapMarks.Contains("search"),
            !HiddenMinimapMarks.Contains("bookmark"),
            !HiddenMinimapMarks.Contains("modified"),
            !HiddenMinimapMarks.Contains("difference") && MinimapDifferences is not null,
            _searchMarkers,
            bookmarks,
            modified,
            [.. MinimapDifferences?.Invoke() ?? []],
            palette?.CursorMarker ?? fallback,
            palette?.Selection ?? fallback,
            palette?.SearchMarker ?? fallback,
            palette?.Modified ?? fallback,
            palette?.Difference ?? fallback);
    }

    /// <summary>ミニマップの描画モデル (テスト用)。表示していなければ null。</summary>
    internal System.Text.Json.Nodes.JsonObject? ReadMinimap()
    {
        if (_minimap is null)
        {
            return null;
        }

        System.Text.Json.Nodes.JsonObject model = _minimap.ReadModel();
        if (_minimap.Visibility == Visibility.Visible)
        {
            Windows.Foundation.Rect bounds = _minimap.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, _minimap.ActualWidth, _minimap.ActualHeight));
            model["left"] = bounds.X;
            model["top"] = bounds.Y;
        }

        return model;
    }
}
