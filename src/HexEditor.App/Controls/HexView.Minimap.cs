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
            _minimapWidth = Math.Clamp(value, MinimapView.MinimumWidth, MinimapView.MaximumWidth);
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

    /// <summary>表示しない印 (VIEW-35 の仕様 6。viewport / cursor / selection / search / bookmark / modified / difference / classification)。</summary>
    public IReadOnlySet<string> HiddenMinimapMarks
    {
        get => _hiddenMinimapMarks;
        set
        {
            _hiddenMinimapMarks = value;
            if (_minimap is not null)
            {
                _minimap.ShowViewport = !value.Contains("viewport");
                _minimap.Redraw();
            }
        }
    }

    private IReadOnlySet<string> _hiddenMinimapMarks = new HashSet<string>();

    /// <summary>
    /// 差分の提供元 (比較 ANA が設定する。この側のオフセットと長さ、差分の種類)。null なら差分の印はない。削除はこの側では長さ 0 の位置。
    /// </summary>
    public Func<IEnumerable<(long Offset, long Length, Core.Compare.DiffKind Kind)>>? MinimapDifferences { get; set; }

    /// <summary>設定「正確に計算」(VIEW-35 の仕様 5)。オンなら、新しく開いた・切り替えたドキュメントも正確に計算する。</summary>
    public bool MinimapExact
    {
        get => _minimapExact;
        set
        {
            _minimapExact = value;
            if (_minimap is not null)
            {
                _minimap.ExactWanted = value;
                _minimap.RequestExactIfNeeded();
            }
        }
    }

    private bool _minimapExact;

    /// <summary>「正確に計算」を実行する (ウィンドウが処理センターで行う)。</summary>
    public Action<MinimapView>? MinimapExactRunner { get; set; }

    /// <summary>ミニマップの境界のドラッグで幅を変えた (VIEW-35 の仕様 1。ウィンドウが全ドキュメント共通の設定に保存する)。</summary>
    public event EventHandler<double>? MinimapWidthCommitted;

    /// <summary>ミニマップの右クリックメニューの項目 (ウィンドウが作る)。</summary>
    public Action<HexView, MenuFlyout>? MinimapMenuOpening { get; set; }

    private void EnsureMinimap()
    {
        if (_minimap is not null)
        {
            return;
        }

        _minimap = new MinimapView
        {
            Width = _minimapWidth,
            Visibility = Visibility.Collapsed,
            ShowViewport = !_hiddenMinimapMarks.Contains("viewport"),
            ExactWanted = _minimapExact,
        };
        Grid.SetRow(_minimap, 1);
        Grid.SetColumn(_minimap, 1);
        ((Grid)Content).Children.Add(_minimap);
        _minimap.Marks = MinimapMarksNow;
        _minimap.ShowClassification = () => !HiddenMinimapMarks.Contains("classification");
        _minimap.HighContrast = IsHighContrast;
        _minimap.ByteTheme = _byteTheme;
        _minimap.MenuOpening = menu => MinimapMenuOpening?.Invoke(this, menu);
        _minimap.Navigated += (_, _) => Focus(FocusState.Programmatic);

        // ホイールは Hex ビューと同じ縦スクロール (VIEW-35 の仕様 7。VIEW-28 の行数と蓄積)。
        _minimap.Wheel = delta => Wheel(delta, horizontal: false, shift: false);
        _minimap.WidthCommitted += (_, width) =>
        {
            _minimapWidth = Math.Clamp(width, MinimapView.MinimumWidth, MinimapView.MaximumWidth);
            MinimapWidthCommitted?.Invoke(this, _minimapWidth);
        };
        _minimap.ExactNeeded += (sender, _) => MinimapExactRunner?.Invoke((MinimapView)sender!);
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
