using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Printing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls.Tools;

/// <summary>表示倍率の選択 (TOOL-02 の仕様 3)。<see cref="Percent"/> が 0 ならページ全体、-1 なら幅に合わせる。</summary>
internal sealed record PreviewZoom(string Label, int Percent)
{
    public static PreviewZoom FitPage => new(Loc.Get("Print_Zoom_FitPage"), 0);

    public static PreviewZoom FitWidth => new(Loc.Get("Print_Zoom_FitWidth"), -1);

    public static IReadOnlyList<int> Percents { get; } = [50, 75, 100, 150, 200, 300, 400];
}

/// <summary>
/// 印刷プレビュー (TOOL-02): 印刷の計画の 1 ページ (または 2 ページ見開き) を、用紙の形の白いキャンバスに描く。表示しているページだけを作る
/// (データはそのページの分だけ読む)。前 / 次 / 先頭 / 末尾、ページ番号の入力、PageUp・PageDown・Home・End、倍率、ページの下の「12 / 3,456 ページ」。
/// </summary>
internal sealed class PrintPreview : UserControl
{
    private readonly ScrollViewer _scroll = new();
    private readonly StackPanel _pages = new() { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _info = new();
    private readonly TextBox _pageBox = new() { Width = 70 };
    private readonly ComboBox _zoom = new() { MinWidth = 130 };
    private readonly CheckBox _twoPages = new();
    private readonly Button _first;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _last;
    private PrintPlan? _plan;
    private IPrintData? _data;
    private PrintFields? _fields;
    private SystemFontMetrics? _fonts;
    private long _page;

    public PrintPreview()
    {
        _first = Nav("First", "Print_Preview_First", "", () => GoTo(0));
        _previous = Nav("Previous", "Print_Preview_Previous", "", () => GoTo(_page - Step));
        _next = Nav("Next", "Print_Preview_Next", "", () => GoTo(_page + Step));
        _last = Nav("Last", "Print_Preview_Last", "", () => GoTo(long.MaxValue));

        AutomationProperties.SetAutomationId(_pageBox, "Print_PageBox");
        AutomationProperties.SetName(_pageBox, Loc.Get("Print_Preview_PageNumber"));
        _pageBox.FlowDirection = FlowDirection.LeftToRight;
        _pageBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && long.TryParse(_pageBox.Text.Replace(",", string.Empty), NumberStyles.None, CultureInfo.CurrentCulture, out long number))
            {
                GoTo(number - 1);
                e.Handled = true;
                _scroll.Focus(FocusState.Programmatic);
            }
        };

        _zoom.Items.Add(PreviewZoom.FitPage.Label);
        _zoom.Items.Add(PreviewZoom.FitWidth.Label);
        foreach (int percent in PreviewZoom.Percents)
        {
            _zoom.Items.Add(percent.ToString(CultureInfo.CurrentCulture) + "%");
        }

        _zoom.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_zoom, "Print_Zoom");
        AutomationProperties.SetName(_zoom, Loc.Get("Print_Preview_Zoom"));
        _zoom.SelectionChanged += (_, _) => Redraw();

        _twoPages.Content = Loc.Get("Print_Preview_TwoPages");
        AutomationProperties.SetAutomationId(_twoPages, "Print_TwoPages");
        _twoPages.Click += (_, _) =>
        {
            Redraw();
            UpdateInfo();
        };

        _info.VerticalAlignment = VerticalAlignment.Center;
        _info.FlowDirection = FlowDirection.LeftToRight;
        AutomationProperties.SetAutomationId(_info, "Print_PageInfo");

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (UIElement element in new UIElement[] { _first, _previous, _pageBox, _next, _last, _zoom, _twoPages })
        {
            toolbar.Children.Add(element);
        }

        _scroll.Content = _pages;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.IsTabStop = true;
        _scroll.TabFocusNavigation = KeyboardNavigationMode.Local;
        _scroll.HorizontalScrollMode = ScrollMode.Auto;
        _scroll.VerticalScrollMode = ScrollMode.Auto;
        _scroll.Background = ToolParts.ThemeBrush("LayerFillColorDefaultBrush");
        _scroll.SizeChanged += (_, _) =>
        {
            if (_zoom.SelectedIndex <= 1)
            {
                Redraw();
            }
        };
        _scroll.KeyDown += Scroll_KeyDown;
        AutomationProperties.SetAutomationId(_scroll, "Print_Preview");
        AutomationProperties.SetName(_scroll, Loc.Get("Print_Preview_Name"));

        var root = new Grid { RowSpacing = 6 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(toolbar);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        Grid.SetRow(_info, 2);
        root.Children.Add(_info);
        Content = root;
    }

    /// <summary>表示しているページが変わった (0 から)。</summary>
    public event EventHandler? PageChanged;

    /// <summary>ページの描画が終わった(テスト・計測用)。</summary>
    public event EventHandler? Rendered;

    private int Step => _twoPages.IsChecked == true ? 2 : 1;

    public long Page => _page;

    public long PageCount => _plan?.PrintedPages ?? 0;

    /// <summary>描画しているページの文字(テスト用: 行ごとに改行)。</summary>
    public string PageText { get; private set; } = string.Empty;

    /// <summary>描画しているページのメタ(読めなかったバイトがあったか)。</summary>
    public bool HadUnreadable { get; private set; }

    public string InfoText => _info.Text;

    public bool TwoPages
    {
        get => _twoPages.IsChecked == true;
        set
        {
            _twoPages.IsChecked = value;
            Redraw();
            UpdateInfo();
        }
    }

    public void FocusPreview() => _scroll.Focus(FocusState.Programmatic);

    /// <summary>テスト用: 倍率の選択 (0 = ページ全体、-1 = 幅に合わせる、それ以外は %)。</summary>
    public void SetZoom(int percent)
    {
        _zoom.SelectedIndex = percent switch
        {
            0 => 0,
            -1 => 1,
            _ => 2 + Math.Max(0, PreviewZoom.Percents.ToList().IndexOf(percent)),
        };
    }

    private static Button Nav(string name, string key, string glyph, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetAutomationId(button, "Print_" + name);
        AutomationProperties.SetName(button, Loc.Get(key));
        ToolTipService.SetToolTip(button, Loc.Get(key));
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>計画・データ・ヘッダの値を設定して、今のページを描き直す(設定を変えたとき)。</summary>
    public void SetContent(PrintPlan plan, IPrintData data, PrintFields fields, SystemFontMetrics fonts)
    {
        _plan = plan;
        _data = data;
        _fields = fields;
        _fonts = fonts;
        _page = Math.Clamp(_page, 0, Math.Max(0, plan.PrintedPages - 1));
        Redraw();
        UpdateInfo();
    }

    public void GoTo(long page)
    {
        if (_plan is null || _plan.PrintedPages == 0)
        {
            return;
        }

        long last = _plan.PrintedPages - 1;
        long target = Math.Clamp(page, 0, last);
        if (TwoPages && target > 0 && target % 2 == 1 && page != long.MaxValue)
        {
            // 見開きでは偶数番目 (1 始まりで奇数) のページを左に置く。
            target--;
        }

        if (target == _page && _pages.Children.Count > 0)
        {
            UpdateInfo();
            return;
        }

        _page = target;
        Redraw();
        UpdateInfo();
        PageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Scroll_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.PageDown:
                GoTo(_page + Step);
                break;
            case VirtualKey.PageUp:
                GoTo(_page - Step);
                break;
            case VirtualKey.Home:
                GoTo(0);
                break;
            case VirtualKey.End:
                GoTo(long.MaxValue);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void UpdateInfo()
    {
        long count = PageCount;
        long shown = count == 0 ? 0 : _page + 1;
        string numbers = shown.ToString("N0", CultureInfo.CurrentCulture);
        if (TwoPages && count > 0 && _page + 1 < count)
        {
            numbers += "-" + (_page + 2).ToString("N0", CultureInfo.CurrentCulture);
        }

        _info.Text = Loc.Format("Print_Preview_Info", numbers, count.ToString("N0", CultureInfo.CurrentCulture));
        _pageBox.Text = shown.ToString(CultureInfo.CurrentCulture);
        _first.IsEnabled = _previous.IsEnabled = count > 0 && _page > 0;
        _next.IsEnabled = _last.IsEnabled = count > 0 && _page + Step < count;
    }

    private double Scale(PrintPlan plan)
    {
        int index = _zoom.SelectedIndex;
        double pageWidth = plan.PageWidth * (TwoPages ? 2 : 1) + (TwoPages ? 12 / (96.0 / 72.0) : 0);
        if (index >= 2)
        {
            return PreviewZoom.Percents[index - 2] / 100.0 * 96.0 / 72.0;
        }

        double availableWidth = Math.Max(120, (_scroll.ActualWidth > 0 ? _scroll.ActualWidth : 360) - 28);
        double availableHeight = Math.Max(120, (_scroll.ActualHeight > 0 ? _scroll.ActualHeight : 420) - 28);
        double byWidth = availableWidth / pageWidth;
        return index == 1 ? byWidth : Math.Min(byWidth, availableHeight / plan.PageHeight);
    }

    private static Windows.UI.Color Paper => AnnotationBrushes.FromRgb(0xFFFFFF);

    private void Redraw()
    {
        _pages.Children.Clear();
        PageText = string.Empty;
        HadUnreadable = false;
        if (_plan is null || _data is null || _fields is null || _fonts is null || _plan.PrintedPages == 0)
        {
            Rendered?.Invoke(this, EventArgs.Empty);
            return;
        }

        double scale = Scale(_plan);
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < (TwoPages ? 2 : 1); i++)
        {
            long index = _page + i;
            if (index >= _plan.PrintedPages)
            {
                break;
            }

            PrintPage page = _plan.Render(index, _data, _fields);
            HadUnreadable |= page.HadUnreadable;
            _pages.Children.Add(Draw(page, scale));
            foreach (PrintRun run in page.Runs)
            {
                text.Append(run.Text).Append('\n');
            }
        }

        PageText = text.ToString();
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    private Border Draw(PrintPage page, double scale)
    {
        var canvas = new Canvas { Width = page.Width * scale, Height = page.Height * scale, Background = new SolidColorBrush(Paper) };
        double ascent = _fonts!.Primary.Font.Ascent / (double)_fonts.Primary.Font.UnitsPerEm;
        var family = new FontFamily(_fonts.Primary.Family);
        foreach (PrintRect rect in page.Rects)
        {
            var shape = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = rect.Width * scale,
                Height = rect.Height * scale,
                Fill = new SolidColorBrush(AnnotationBrushes.FromRgb(((uint)rect.Color.R << 16) | ((uint)rect.Color.G << 8) | rect.Color.B)),
            };
            Canvas.SetLeft(shape, rect.X * scale);
            Canvas.SetTop(shape, rect.Y * scale);
            canvas.Children.Add(shape);
        }

        foreach (PrintLine line in page.Lines)
        {
            var shape = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = line.X1 * scale,
                Y1 = line.Y1 * scale,
                X2 = line.X2 * scale,
                Y2 = line.Y2 * scale,
                StrokeThickness = Math.Max(0.75, line.Thickness * scale),
                Stroke = new SolidColorBrush(AnnotationBrushes.FromRgb(0)),
            };
            canvas.Children.Add(shape);
        }

        foreach (PrintRun run in page.Runs)
        {
            var block = new TextBlock
            {
                Text = run.Text,
                FontFamily = run.Font == PrintFontKind.Primary ? family : new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"),
                FontSize = run.Size * scale,
                Foreground = new SolidColorBrush(AnnotationBrushes.FromRgb(((uint)run.Color.R << 16) | ((uint)run.Color.G << 8) | run.Color.B)),
                TextWrapping = TextWrapping.NoWrap,
                IsHitTestVisible = false,
                FlowDirection = FlowDirection.LeftToRight,
                IsTextScaleFactorEnabled = false,
            };
            Canvas.SetLeft(block, run.X * scale);
            Canvas.SetTop(block, (run.Baseline - run.Size * ascent) * scale);
            canvas.Children.Add(block);
        }

        return new Border
        {
            Child = canvas,
            BorderThickness = new Thickness(1),
            BorderBrush = ToolParts.ThemeBrush("CardStrokeColorDefaultBrush"),
        };
    }
}
