using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace HexEditor.App.Controls.Charts;

/// <summary>
/// グラフの描画上の項目 (棒、ブロック、帯の区間)。UI 要素は作らず、UI オートメーションの子 (仮想の要素) として見せる (06 の 0.4)。
/// </summary>
public interface IChartItems
{
    int ItemCount { get; }

    string ItemName(int index);

    string ItemAutomationId(int index);

    /// <summary>描画面 (グラフの要素) の中の位置。</summary>
    Rect ItemBounds(int index);

    /// <summary>描画の情報 (模様のリソース名など)。</summary>
    string ItemStatus(int index);

    /// <summary>項目を実行する (棒のダブルクリックと同じ)。実行できなければ false。</summary>
    bool InvokeItem(int index);

    /// <summary>選べる項目 (エントロピーグラフのブロック)。選べなければ -1 を返す。</summary>
    int SelectedItem { get; }

    bool CanSelect { get; }

    void SelectItem(int index);
}

/// <summary>グラフの UI オートメーション: 名前と要約、子は項目ごとの仮想の要素 (上限 <see cref="MaxChildren"/> 個)。</summary>
public sealed partial class ChartAutomationPeer(FrameworkElement owner, IChartItems items, string className) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    public const int MaxChildren = 4096;

    private List<AutomationPeer>? _children;
    private int _childrenFor = -1;

    internal FrameworkElement Owner => owner;

    internal IChartItems Items => items;

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => false;

    public IRawElementProviderSimple[] GetSelection() =>
        items.CanSelect && items.SelectedItem >= 0 && items.SelectedItem < Math.Min(items.ItemCount, MaxChildren)
            ? [ProviderFromPeer(Children()[items.SelectedItem])]
            : [];

    internal void Invalidate() => _children = null;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection && items.CanSelect ? this : base.GetPatternCore(patternInterface);

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        items.CanSelect ? AutomationControlType.List : AutomationControlType.Image;

    protected override string GetClassNameCore() => className;

    protected override bool IsKeyboardFocusableCore() => owner is Control { IsTabStop: true };

    protected override IList<AutomationPeer> GetChildrenCore() => Children();

    internal List<AutomationPeer> Children()
    {
        int count = Math.Min(items.ItemCount, MaxChildren);
        if (_children is null || _childrenFor != count)
        {
            _children = [.. Enumerable.Range(0, count).Select(i => (AutomationPeer)new ChartItemAutomationPeer(this, i))];
            _childrenFor = count;
        }

        return _children;
    }

    internal Rect ToScreen(Rect local)
    {
        if (owner.XamlRoot is null)
        {
            return local;
        }

        Rect bounds = owner.TransformToVisual(null).TransformBounds(local);
        double scale = owner.XamlRoot.RasterizationScale;
        var origin = new NativePoint();
        nint hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(owner.XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (hwnd != 0)
        {
            ClientToScreen(hwnd, ref origin);
        }

        return new Rect(origin.X + (bounds.X * scale), origin.Y + (bounds.Y * scale), bounds.Width * scale, bounds.Height * scale);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    internal AutomationPeer? Sibling(ChartItemAutomationPeer item, int delta)
    {
        List<AutomationPeer> list = Children();
        int i = item.Index + delta;
        return i >= 0 && i < list.Count ? list[i] : null;
    }

    internal IRawElementProviderSimple Provider(AutomationPeer peer) => ProviderFromPeer(peer);

    /// <summary>選んでいる項目が変わったことを知らせる。</summary>
    internal void RaiseSelected(int index)
    {
        List<AutomationPeer> list = Children();
        if (index >= 0 && index < list.Count)
        {
            list[index].RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        }
    }
}

/// <summary>グラフの項目 1 つ (名前、境界、描画の情報、実行・選択)。</summary>
public sealed partial class ChartItemAutomationPeer(ChartAutomationPeer chart, int index) : AutomationPeer, IInvokeProvider, ISelectionItemProvider
{
    internal int Index => index;

    public bool IsSelected => chart.Items.SelectedItem == index;

    public IRawElementProviderSimple SelectionContainer => chart.Provider(chart);

    public void Invoke() => chart.Items.InvokeItem(index);

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
    }

    public void Select() => chart.Items.SelectItem(index);

    protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Invoke => this,
        PatternInterface.SelectionItem when chart.Items.CanSelect => this,
        _ => null,
    };

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        chart.Items.CanSelect ? AutomationControlType.ListItem : AutomationControlType.DataItem;

    protected override string GetClassNameCore() => "ChartItem";

    protected override string GetAutomationIdCore() => chart.Items.ItemAutomationId(index);

    protected override string GetNameCore() => chart.Items.ItemName(index);

    protected override string GetItemStatusCore() => chart.Items.ItemStatus(index);

    protected override Rect GetBoundingRectangleCore() => chart.ToScreen(chart.Items.ItemBounds(index));

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsKeyboardFocusableCore() => false;

    protected override bool IsOffscreenCore() => chart.Items.ItemBounds(index).Width <= 0;

    protected override bool IsEnabledCore() => true;

    protected override AutomationPeer GetLabeledByCore() => null!;

    protected override object? NavigateCore(AutomationNavigationDirection direction) => direction switch
    {
        AutomationNavigationDirection.Parent => chart,
        AutomationNavigationDirection.NextSibling => chart.Sibling(this, 1),
        AutomationNavigationDirection.PreviousSibling => chart.Sibling(this, -1),
        _ => null,
    };

    protected override IList<AutomationPeer> GetChildrenCore() => [];
}

/// <summary>グラフの共通の道具: テーマの色、ハイコントラストの判定、色以外で区別するための模様。</summary>
internal static class ChartSupport
{
    private static readonly AccessibilitySettings Accessibility = new();

    /// <summary>テスト用の模擬のハイコントラスト。</summary>
    public static bool ForcedHighContrast { get; set; }

    public static bool IsHighContrast => ForcedHighContrast || Accessibility.HighContrast;

    public static Brush Brush(string key, FrameworkElement scope) => AnnotationBrushes.Get(key, scope, IsHighContrast);

    public static Windows.UI.Color Color(string key, FrameworkElement scope) =>
        Brush(key, scope) is SolidColorBrush s ? s.Color : default;

    /// <summary>
    /// 模様の形 (リソース名 Pattern_*): 斜線、交差、点、横線、縦線、市松、なし。<paramref name="r"/> の中を <paramref name="step"/> の間隔で描く。
    /// </summary>
    public static Geometry Pattern(string pattern, Rect r, double step = 6)
    {
        var group = new GeometryGroup();
        void Line(double x1, double y1, double x2, double y2) =>
            group.Children.Add(new LineGeometry { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2) });
        double right = r.X + r.Width;
        double bottom = r.Y + r.Height;
        switch (pattern)
        {
            case "Pattern_Diagonal":
            case "Pattern_Cross":
                for (double d = -r.Height; d < r.Width; d += step)
                {
                    // 右上がりの線 (傾き 1) を矩形で切る。
                    double x1 = r.X + Math.Max(0, d);
                    double y1 = bottom - Math.Max(0, -d);
                    double t = Math.Min(right - x1, y1 - r.Y);
                    Line(x1, y1, x1 + t, y1 - t);
                    if (pattern == "Pattern_Cross")
                    {
                        // 右下がりの線 (上下を反転)。
                        Line(x1, r.Y + (bottom - y1), x1 + t, r.Y + (bottom - y1) + t);
                    }
                }

                break;
            case "Pattern_Horizontal":
                for (double y = r.Y + (step / 2); y < bottom; y += step)
                {
                    Line(r.X, y, right, y);
                }

                break;
            case "Pattern_Vertical":
                for (double x = r.X + (step / 2); x < right; x += step)
                {
                    Line(x, r.Y, x, bottom);
                }

                break;
            case "Pattern_Dots":
                for (double y = r.Y + (step / 2); y < bottom; y += step)
                {
                    for (double x = r.X + (step / 2); x < right; x += step)
                    {
                        group.Children.Add(new EllipseGeometry { Center = new Point(x, y), RadiusX = 1, RadiusY = 1 });
                    }
                }

                break;
            case "Pattern_Checker":
                for (double y = r.Y; y < bottom; y += step)
                {
                    for (double x = r.X + ((int)((y - r.Y) / step) % 2 * step); x < right; x += step * 2)
                    {
                        group.Children.Add(new RectangleGeometry { Rect = new Rect(x, y, Math.Min(step, right - x), Math.Min(step, bottom - y)) });
                    }
                }

                break;
        }

        return group;
    }

    /// <summary>ツールチップの文字列を設定する (同じなら何もしない)。</summary>
    public static void SetToolTip(FrameworkElement element, string text)
    {
        if (ToolTipService.GetToolTip(element) is ToolTip tip)
        {
            if (!Equals(tip.Content, text))
            {
                tip.Content = text;
            }

            tip.IsOpen = text.Length > 0;
            return;
        }

        ToolTipService.SetToolTip(element, new ToolTip { Content = text });
    }
}
