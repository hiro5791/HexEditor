using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Inspector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// インスペクタの行からの移動 (INSP-20 の「ポインタとして移動」、INSP-21 の配列モード)。右クリックメニューの項目と、ツールバーの
/// 配列モードの操作部 (前の要素・要素番号・間隔・次の要素・終了)。
/// </summary>
public sealed partial class InspectorPanel
{
    private static readonly (PointerMode Mode, string Id)[] PointerModes =
    [
        (PointerMode.AbsoluteOffset, "AbsoluteOffset"),
        (PointerMode.AbsoluteAddress, "AbsoluteAddress"),
        (PointerMode.RelativeToCursor, "RelativeToCursor"),
        (PointerMode.RelativeToNext, "RelativeToNext"),
        (PointerMode.RelativeToBase, "RelativeToBase"),
    ];

    private MenuFlyoutSubItem _pointerMenu = null!;
    private MenuFlyoutItem _arrayMenu = null!;
    private MenuFlyoutItem _branchMenu = null!;
    private readonly List<MenuFlyoutItem> _pointerItems = [];
    private Flyout? _baseFlyout;

    /// <summary>「基準を指定して相対」の最後の基準 (コマンドパレットからの移動にも使う)。</summary>
    internal static string LastPointerBase { get; private set; } = "0";

    /// <summary>コンストラクターから呼ぶ。</summary>
    private void InitializeNavigation()
    {
        _pointerMenu = new MenuFlyoutSubItem { Text = Loc.Get("Inspector_MenuPointer") };
        AutomationProperties.SetAutomationId(_pointerMenu, "Inspector_MenuPointer");
        foreach ((PointerMode mode, string id) in PointerModes)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get("Inspector_Pointer_" + id), Tag = mode };
            AutomationProperties.SetAutomationId(item, "Inspector_MenuPointer_" + id);
            item.Click += PointerItem_Click;
            _pointerItems.Add(item);
            _pointerMenu.Items.Add(item);
        }

        _arrayMenu = new MenuFlyoutItem { Text = Loc.Get("Inspector_MenuArray") };
        AutomationProperties.SetAutomationId(_arrayMenu, "Inspector_MenuArray");
        _arrayMenu.Click += (_, _) =>
        {
            if (_menuTarget is { } item)
            {
                Vm.EnterArrayMode(item);
            }
        };

        // 1 命令の逆アセンブルの行の「分岐先へ移動」(INSP-16 の仕様 7)。
        _branchMenu = new MenuFlyoutItem { Text = Loc.Get("Inspector_MenuGoToBranchTarget") };
        AutomationProperties.SetAutomationId(_branchMenu, "Inspector_MenuGoToBranchTarget");
        _branchMenu.Click += (_, _) =>
        {
            if ((_menuTarget ?? Vm.Selected) is { BranchTarget: { } target })
            {
                Vm.GoToBranchTarget?.Invoke(target);
            }
        };

        // 「値を編集」の後ろに置く (INSP-01 の仕様 7 の順)。
        int at = RowMenu.Items.IndexOf(MenuEdit) + 1;
        RowMenu.Items.Insert(at, _branchMenu);
        RowMenu.Items.Insert(at, _arrayMenu);
        RowMenu.Items.Insert(at, _pointerMenu);
        RowMenu.Opening += (_, _) =>
        {
            InspectorItemViewModel? target = _menuTarget ?? Vm.Selected;
            _pointerMenu.IsEnabled = InspectorViewModel.CanFollowPointer(target);
            _arrayMenu.IsEnabled = target is { Kind: InspectorItemKind.Row } && InspectorTypes.Find(target.TypeId) is not null;
            bool disasm = target is not null && InspectorTypes.IsDisasm(target.TypeId);
            _branchMenu.Visibility = disasm ? Visibility.Visible : Visibility.Collapsed;
            _branchMenu.IsEnabled = disasm && target!.BranchTarget is not null;
            if (disasm)
            {
                // 1 命令の逆アセンブルの行からは書き換えできない (INSP-16 の仕様 6)。
                MenuEdit.IsEnabled = false;
            }

            if (target is not null && target.TypeId.StartsWith("user:", StringComparison.Ordinal))
            {
                // ユーザー定義型の行は書き換え・非表示にできない (INSP-22 の仕様 5)。
                MenuEdit.IsEnabled = false;
                MenuHide.IsEnabled = false;
            }
            foreach (MenuFlyoutItem item in _pointerItems)
            {
                // 最後に使った方法を既定にする (INSP-20 の仕様 2): 印を付け、コマンドパレットの「ポインタとして移動」もこの方法を使う。
                bool last = (PointerMode)item.Tag == InspectorViewModel.LastPointerMode;
                item.Icon = last ? new FontIcon { Glyph = "\uE73E" } : null;
                AutomationProperties.SetItemStatus(item, last ? Loc.Get("Inspector_PointerDefault") : string.Empty);
            }
        };

        AutomationProperties.SetName(ArrayPrevious, Loc.Get("Inspector_ArrayPrevious"));
        AutomationProperties.SetName(ArrayNext, Loc.Get("Inspector_ArrayNext"));
        AutomationProperties.SetName(ArrayExit, Loc.Get("Inspector_ArrayExit"));
        AutomationProperties.SetName(ArrayIndexBox, Loc.Get("Inspector_ArrayIndex"));
        AutomationProperties.SetName(ArrayStrideBox, Loc.Get("Inspector_ArrayStride"));
        ToolTipService.SetToolTip(ArrayPrevious, Loc.Get("Inspector_ArrayPrevious"));
        ToolTipService.SetToolTip(ArrayNext, Loc.Get("Inspector_ArrayNext"));
        ToolTipService.SetToolTip(ArrayExit, Loc.Get("Inspector_ArrayExit"));
    }

    private void PointerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PointerMode mode } || (_menuTarget ?? Vm.Selected) is not { } item)
        {
            return;
        }

        if (mode == PointerMode.RelativeToBase)
        {
            ShowBaseFlyout(item);
            return;
        }

        Vm.FollowPointer(item, mode);
    }

    /// <summary>「基準を指定して相対」: 基準 (入力式) を入れるフライアウトを開く (INSP-20 の「画面」)。</summary>
    internal void ShowBaseFlyout(InspectorItemViewModel item)
    {
        var box = new TextBox
        {
            Header = Loc.Get("Inspector_PointerBase"),
            Text = LastPointerBase,
            FlowDirection = FlowDirection.LeftToRight,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            MinWidth = 200,
        };
        AutomationProperties.SetAutomationId(box, "Inspector_PointerBase");
        AutomationProperties.SetName(box, Loc.Get("Inspector_PointerBase"));
        var go = new Button { Content = Loc.Get("Inspector_PointerGo"), HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(go, "Inspector_PointerGo");
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { box, go } } };
        void Go()
        {
            LastPointerBase = box.Text;
            flyout.Hide();
            Vm.FollowPointer(item, PointerMode.RelativeToBase, box.Text);
        }

        go.Click += (_, _) => Go();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                Go();
            }
        };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        _baseFlyout = flyout;
        flyout.ShowAt(RowsList.ContainerFromItem(item) as FrameworkElement ?? RowsList);
    }

    /// <summary>基準のフライアウトが開いていれば、入力欄の値で移動する (テスト用の命令の通り道)。</summary>
    internal bool SubmitBaseFlyout()
    {
        if (_baseFlyout is not { IsOpen: true } flyout || flyout.Content is not StackPanel { Children: [TextBox, Button go] })
        {
            return false;
        }

        Invoke(go);
        return true;
    }

    // ---- 配列モードの操作部 (INSP-21 の仕様 1) ----

    private void ArrayPrevious_Click(object sender, RoutedEventArgs e) => Vm.StepElement(-1);

    private void ArrayNext_Click(object sender, RoutedEventArgs e) => Vm.StepElement(1);

    private void ArrayExit_Click(object sender, RoutedEventArgs e)
    {
        Vm.ExitArrayMode();
        FocusList();
    }

    private void ArrayIndexBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Vm.GoToElement(ArrayIndexBox.Text);
        }
    }

    private void ArrayStrideBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (!Vm.SetStride(ArrayStrideBox.Text))
            {
                ArrayStrideBox.Text = Vm.ArrayStrideText;
            }
        }
    }

    /// <summary>Esc (インスペクタにフォーカスがあるとき) で配列モードを終える (INSP-21 の仕様 6)。</summary>
    private bool HandleArrayEscape()
    {
        if (!Vm.IsArrayMode)
        {
            return false;
        }

        Vm.ExitArrayMode();
        return true;
    }

    /// <summary>配列モードの入力欄と基準のフライアウトのキー (テスト用の命令の通り道)。処理したら true。</summary>
    private bool InjectNavigationKey(object? focused, VirtualKey key)
    {
        if (key != VirtualKey.Enter)
        {
            return false;
        }

        if (ReferenceEquals(focused, ArrayIndexBox))
        {
            Vm.GoToElement(ArrayIndexBox.Text);
            return true;
        }

        if (ReferenceEquals(focused, ArrayStrideBox))
        {
            if (!Vm.SetStride(ArrayStrideBox.Text))
            {
                ArrayStrideBox.Text = Vm.ArrayStrideText;
            }

            return true;
        }

        return focused is TextBox box && AutomationProperties.GetAutomationId(box) == "Inspector_PointerBase" && SubmitBaseFlyout();
    }

    /// <summary>配列モードの操作部が表示されているか (テスト用の状態の表示)。</summary>
    internal bool ArrayBarShown => ArrayBar.Visibility == Visibility.Visible && Vm.IsArrayMode;
}
