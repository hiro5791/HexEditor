using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Disassembly;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>リスティングの 1 行の表示 (行の内容は <see cref="DisasmRow"/>。選んだ行の印だけが変わる)。</summary>
public sealed class DisasmRowItem(DisasmRow row, int index, double height, GridLength address, GridLength bytes, GridLength mnemonic,
    Func<DisasmRow, string?> toolTip) : INotifyPropertyChanged
{
    private bool _selected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DisasmRow Row { get; } = row;

    public int Index { get; } = index;

    public double RowHeight { get; } = height;

    public GridLength AddressWidth { get; } = address;

    public GridLength BytesWidth { get; } = bytes;

    public GridLength MnemonicWidth { get; } = mnemonic;

    public string AutomationId => "Disasm_Row_" + Index;

    public bool IsHeading => Row.Kind == ListingLineKind.Heading;

    public Visibility InstructionVisibility => IsHeading ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HeadingVisibility => IsHeading ? Visibility.Visible : Visibility.Collapsed;

    public string HeadingText => Row.Msil is not null ? Row.Label ?? string.Empty : Row.Label + ":";

    public string CommentText => Row.Comment.Length == 0 ? string.Empty : "; " + Row.Comment;

    public string AccessibleName => IsHeading ? HeadingText : $"{Row.Address} {Row.Instruction} {CommentText}".Trim();

    public string? ToolTip => IsHeading ? null : toolTip(Row);

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVisibility)));
            }
        }
    }

    public Visibility SelectedVisibility => _selected ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 逆アセンブルのリスティング (ANA-23)。表示範囲の行を <see cref="DisassemblyViewModel"/> から受け取って並べ、キー (↑↓・PageUp/Down・
/// Ctrl+Home/End・Enter・F12・Ctrl+C) とマウス (クリック・ダブルクリック・ホイール) を受ける。分岐の矢印と、表示範囲内の分岐先への線を描く
/// (ANA-25 の仕様 5)。
/// </summary>
public sealed partial class DisassemblyListingView : UserControl
{
    private const double RowHeight = 20;
    private DisassemblyViewModel? _vm;
    private bool _updatingBar;
    private double _charWidth = 7.5;

    public DisassemblyListingView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateRowCount();
        VerticalBar.ValueChanged += VerticalBar_ValueChanged;
        PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;
        RowsRepeater.ElementPrepared += (_, e) =>
        {
            if (e.Element is FrameworkElement fe)
            {
                fe.Tapped -= Row_Tapped;
                fe.Tapped += Row_Tapped;
                fe.DoubleTapped -= Row_DoubleTapped;
                fe.DoubleTapped += Row_DoubleTapped;
                fe.RightTapped -= Row_RightTapped;
                fe.RightTapped += Row_RightTapped;
            }
        };
        ActualThemeChanged += (_, _) => DrawArrows();
        AutomationProperties_SetName();
    }

    private void AutomationProperties_SetName() =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, Loc.Get("Disasm_ListingName"));

    public ObservableCollection<DisasmRowItem> Items { get; } = [];

    /// <summary>Enter・F12・ダブルクリック (分岐先へ移動。ANA-25 の仕様 4)。</summary>
    public event EventHandler<DisasmRow>? GoToTargetRequested;

    /// <summary>右クリック・アプリケーションキー (行の右クリックメニュー)。位置は null ならキーボード。</summary>
    public event EventHandler<(DisasmRow Row, Windows.Foundation.Point? Position)>? ContextMenuRequested;

    /// <summary>Ctrl+C (選んだ行をコピー。ANA-23 の仕様 8)。</summary>
    public event EventHandler<DisasmRow>? CopyRequested;

    /// <summary>行のツールチップ (命令の詳細。ANA-23 の仕様 7)。</summary>
    public Func<DisasmRow, string?> ToolTipProvider { get; set; } = _ => null;

    public DisassemblyViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
            {
                _vm.RowsChanged -= Vm_RowsChanged;
            }

            _vm = value;
            if (_vm is not null)
            {
                _vm.RowsChanged += Vm_RowsChanged;
                UpdateRowCount();
                Rebuild();
            }
        }
    }

    /// <summary>パネルを閉じたときに外す。</summary>
    public void Detach() => ViewModel = null;

    private void Vm_RowsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void UpdateRowCount()
    {
        if (_vm is not null && ActualHeight > 0)
        {
            _vm.VisibleRowCount = Math.Max(1, (int)(ActualHeight / RowHeight));
        }
    }

    private GridLength Chars(int count) => new(Math.Ceiling(count * _charWidth) + 2);

    /// <summary>行を作り直す (表示範囲の行だけ)。</summary>
    private void Rebuild()
    {
        if (_vm is null)
        {
            Items.Clear();
            Arrows.Children.Clear();
            return;
        }

        DisasmFormat f = _vm.Format;
        int addressChars = f.AddressColumn == AddressColumn.Both ? (_vm.Listing?.WideAddress == true ? 25 : 17) : _vm.Listing?.WideAddress == true ? 16 : 8;
        if (_vm.IsMsil)
        {
            addressChars = 7;
        }

        GridLength address = Chars(addressChars);
        GridLength bytes = f.ShowBytes ? Chars(f.MaxBytes * 3 + 1) : new GridLength(0);
        GridLength mnemonic = Chars(_vm.IsMsil ? 12 : 9);
        DisasmRow? selected = _vm.SelectedRow;
        IReadOnlyList<DisasmRow> rows = _vm.Rows;
        bool same = Items.Count == rows.Count && Items.Select(i => i.Row).SequenceEqual(rows);
        if (!same)
        {
            Items.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                Items.Add(new DisasmRowItem(rows[i], i, RowHeight, address, bytes, mnemonic, ToolTipProvider));
            }
        }

        foreach (DisasmRowItem item in Items)
        {
            item.IsSelected = selected is not null && item.Row == selected;
        }

        _updatingBar = true;
        VerticalBar.Maximum = Math.Max(1, _vm.ScrollMaximum);
        VerticalBar.ViewportSize = Math.Max(1, _vm.ScrollMaximum / 50);
        VerticalBar.LargeChange = VerticalBar.ViewportSize;
        VerticalBar.SmallChange = 1;
        VerticalBar.Value = _vm.ScrollValue;
        _updatingBar = false;
        DrawArrows();
    }

    private void VerticalBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingBar || _vm is null)
        {
            return;
        }

        // 矢印のボタンは 1 行、つまみのドラッグは位置 (オフセットに比例)。
        if (Math.Abs(e.NewValue - e.OldValue) <= 1)
        {
            _vm.ScrollLines(e.NewValue > e.OldValue ? 1 : -1);
        }
        else
        {
            _vm.ScrollTo(e.NewValue);
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _vm?.ScrollLines(delta > 0 ? -3 : 3);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Down:
                _vm.MoveSelection(1);
                break;
            case VirtualKey.Up:
                _vm.MoveSelection(-1);
                break;
            case VirtualKey.PageDown:
                _vm.MoveSelection(Math.Max(1, _vm.VisibleRowCount - 1));
                break;
            case VirtualKey.PageUp:
                _vm.MoveSelection(-Math.Max(1, _vm.VisibleRowCount - 1));
                break;
            case VirtualKey.Home when ctrl:
                _vm.GoToOffset(0, activate: true);
                break;
            case VirtualKey.End when ctrl:
                _vm.GoToOffset(Math.Max(0, (long)_vm.ScrollMaximum), activate: true);
                break;
            case VirtualKey.Enter:
            case VirtualKey.F12:
                if (_vm.SelectedRow is { } row)
                {
                    GoToTargetRequested?.Invoke(this, row);
                }

                break;
            case VirtualKey.C when ctrl:
                if (_vm.SelectedRow is { } copy)
                {
                    CopyRequested?.Invoke(this, copy);
                }

                break;
            case VirtualKey.Application:
                if (_vm.SelectedRow is { } menu)
                {
                    ContextMenuRequested?.Invoke(this, (menu, null));
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>キー (テスト用の命令からも呼ぶ)。Enter と F12 は分岐先へ移動。</summary>
    internal void HandleKey(VirtualKey key)
    {
        if (_vm?.SelectedRow is { } row && key is VirtualKey.Enter or VirtualKey.F12)
        {
            GoToTargetRequested?.Invoke(this, row);
        }
    }

    private static DisasmRowItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as DisasmRowItem;

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            Focus(FocusState.Pointer);
            _vm?.Select(item.Row);
        }
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            GoToTargetRequested?.Invoke(this, item.Row);
        }
    }

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            _vm?.Select(item.Row);
            ContextMenuRequested?.Invoke(this, (item.Row, e.GetPosition(this)));
            e.Handled = true;
        }
    }

    // ---- 分岐の矢印と線 (ANA-25 の仕様 5) ----

    private void DrawArrows()
    {
        Arrows.Children.Clear();
        if (_vm is null || !_vm.Format.ShowBranchLines)
        {
            return;
        }

        int lane = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            DisasmRow row = Items[i].Row;
            if (row.Direction == BranchDirection.None)
            {
                continue;
            }

            double y = i * RowHeight + RowHeight / 2;
            int target = row.TargetOffset is { } t ? IndexOfInstruction(t) : -1;
            if (target >= 0 && target != i)
            {
                // 表示範囲内の分岐先: 分岐元から分岐先へ、左に回り込む線と矢じり。
                double x = 24 - 4 * (lane % 5);
                lane++;
                double ty = target * RowHeight + RowHeight / 2;
                var line = new Polyline { StrokeThickness = 1 };
                line.Points.Add(new Windows.Foundation.Point(26, y));
                line.Points.Add(new Windows.Foundation.Point(x - 2, y));
                line.Points.Add(new Windows.Foundation.Point(x - 2, ty));
                line.Points.Add(new Windows.Foundation.Point(26, ty));
                Bind(line, LineBrushHost);
                Arrows.Children.Add(line);
                var head = new Polygon();
                head.Points.Add(new Windows.Foundation.Point(27, ty));
                head.Points.Add(new Windows.Foundation.Point(23, ty - 3));
                head.Points.Add(new Windows.Foundation.Point(23, ty + 3));
                BindFill(head, LineBrushHost);
                Arrows.Children.Add(head);
            }
            else
            {
                // 範囲外: 方向だけを示す矢印 (前方は下向き、後方は上向き)。
                var arrow = new Polygon();
                double dir = row.Direction == BranchDirection.Forward ? 1 : -1;
                arrow.Points.Add(new Windows.Foundation.Point(20, y + 4 * dir));
                arrow.Points.Add(new Windows.Foundation.Point(16, y - 2 * dir));
                arrow.Points.Add(new Windows.Foundation.Point(24, y - 2 * dir));
                BindFill(arrow, AccentBrushHost);
                Arrows.Children.Add(arrow);
            }
        }
    }

    private int IndexOfInstruction(long offset)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i].Row.Offset == offset && !Items[i].IsHeading)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Bind(Shape shape, TextBlock host) =>
        shape.SetBinding(Shape.StrokeProperty, new Binding { Source = host, Path = new PropertyPath("Foreground") });

    private static void BindFill(Shape shape, TextBlock host) =>
        shape.SetBinding(Shape.FillProperty, new Binding { Source = host, Path = new PropertyPath("Foreground") });

    /// <summary>描いた矢印と線の数 (テスト用)。</summary>
    internal int ArrowCount => Arrows.Children.Count;

    /// <summary>表示している行のテキスト (テスト用)。</summary>
    internal IReadOnlyList<string> RowTexts => [.. Items.Select(i => i.IsHeading ? i.HeadingText : i.Row.Address + " " + i.Row.Instruction + " " + i.CommentText)];

    /// <summary>行の要素 (テスト用の画面上の位置)。</summary>
    internal FrameworkElement? RowElement(int index) => RowsRepeater.TryGetElement(index) as FrameworkElement;

    internal static string CopyText(IEnumerable<DisasmRow> rows)
    {
        var sb = new StringBuilder();
        foreach (DisasmRow r in rows)
        {
            sb.Append(r.TabSeparated).Append("\r\n");
        }

        return sb.ToString();
    }
}
