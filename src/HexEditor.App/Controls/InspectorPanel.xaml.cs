using System.Collections.Specialized;
using System.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Inspector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>インスペクタの一覧の項目の見た目を選ぶ。</summary>
public sealed partial class InspectorTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Group { get; set; }

    public DataTemplate? Row { get; set; }

    public DataTemplate? Component { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        InspectorItemViewModel { Kind: InspectorItemKind.Group } => Group,
        InspectorItemViewModel { Kind: InspectorItemKind.Component } => Component,
        _ => Row,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// データインスペクタのパネル (INSP-01)。表示とキー操作だけを持ち、解釈と書き換えは <see cref="InspectorViewModel"/> が行う。
/// パネルのキー操作 (00-overview 8.6): ↑ / ↓ で行を移動、Enter で値を編集、Space で 2 進の行のビットへ (ビットの上では反転)・
/// グループの折りたたみ・GUID の展開、Shift+F10 / アプリケーションキーで右クリックメニュー。
/// </summary>
public sealed partial class InspectorPanel : UserControl
{
    private InspectorItemViewModel? _menuTarget;

    public InspectorPanel(InspectorViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Inspector_PanelName"));
        AutomationProperties.SetName(RowsList, Loc.Get("Inspector_PanelName"));
        AutomationProperties.SetName(EndianButton, Loc.Get("Inspector_Endian_Name"));
        AutomationProperties.SetName(BaseChoice, Loc.Get("Inspector_Base_Name"));
        AutomationProperties.SetName(FloatChoice, Loc.Get("Inspector_FloatFormat_Name"));
        AutomationProperties.SetName(RowsButton, Loc.Get("Inspector_RowsText/Text"));
        ToolTipService.SetToolTip(EndianButton, Loc.Get("Inspector_Endian_Name"));
        ToolTipService.SetToolTip(UtcToggle, Loc.Get("Inspector_Utc_Name"));
        BaseChoice.SelectedIndex = (int)vm.IntegerBase;
        FloatChoice.SelectedIndex = (int)vm.FloatFormat;
        UpdateUtcToggle();
        RowSettings.ViewModel = vm;
        vm.Items.CollectionChanged += Items_CollectionChanged;
        EndianMenu.Opening += (_, _) => UpdateEndianMenu();
    }

    public InspectorViewModel Vm { get; }

    /// <summary>設定が外部で変わった (表示形式の選択を合わせる)。</summary>
    public void SyncOptions()
    {
        BaseChoice.SelectedIndex = (int)Vm.IntegerBase;
        FloatChoice.SelectedIndex = (int)Vm.FloatFormat;
        UpdateUtcToggle();
    }

    // ---- フォーカス ----

    /// <summary>一覧にフォーカスを置く (F6 で領域に入ったとき)。選んでいる行がなければ最初の行を選ぶ。</summary>
    public bool FocusList()
    {
        if (Vm.Selected is null && Vm.Items.FirstOrDefault(i => i.Kind == InspectorItemKind.Row) is { } first)
        {
            Vm.Selected = first;
        }

        if (Vm.Selected is not { } selected)
        {
            return RowsList.Focus(FocusState.Keyboard);
        }

        RowsList.ScrollIntoView(selected);
        RowsList.UpdateLayout();
        return RowsList.ContainerFromItem(selected) is ListViewItem item ? item.Focus(FocusState.Keyboard) : RowsList.Focus(FocusState.Keyboard);
    }

    /// <summary>フォーカスがこのパネルの中にあるか。</summary>
    public bool ContainsFocus()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
        {
            return false;
        }

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == this)
            {
                return true;
            }
        }

        // 行の設定のフライアウトはポップアップの中にある。
        return RowsFlyout.IsOpen && IsInside(focused, RowSettings);
    }

    private static bool IsInside(DependencyObject node, DependencyObject root)
    {
        for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (n == root)
            {
                return true;
            }
        }

        return false;
    }

    // ---- 読み上げの名前 (INSP-01 の受け入れ基準 5) ----

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (InspectorItemViewModel item in e.NewItems?.OfType<InspectorItemViewModel>() ?? [])
        {
            item.PropertyChanged -= Item_PropertyChanged;
            item.PropertyChanged += Item_PropertyChanged;
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorItemViewModel.AutomationName) && sender is InspectorItemViewModel item
            && RowsList.ContainerFromItem(item) is ListViewItem container)
        {
            AutomationProperties.SetName(container, item.AutomationName);
        }
    }

    private void RowsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is InspectorItemViewModel item && args.ItemContainer is ListViewItem container)
        {
            AutomationProperties.SetName(container, item.AutomationName);
            AutomationProperties.SetAutomationId(container, item.AutomationId);
        }
    }

    // ---- ツールバー (INSP-02、INSP-15) ----

    private void UpdateEndianMenu()
    {
        EndianDocument.IsChecked = Vm.EndianMode == InspectorEndianMode.Document;
        EndianLittle.IsChecked = Vm.EndianMode == InspectorEndianMode.Little;
        EndianBig.IsChecked = Vm.EndianMode == InspectorEndianMode.Big;
    }

    private void Endian_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            Vm.EndianMode = tag switch { "little" => InspectorEndianMode.Little, "big" => InspectorEndianMode.Big, _ => InspectorEndianMode.Document };
            UpdateEndianMenu();
        }
    }

    private void BaseChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BaseChoice.SelectedIndex >= 0 && (IntegerBase)BaseChoice.SelectedIndex != Vm.IntegerBase)
        {
            Vm.IntegerBase = (IntegerBase)BaseChoice.SelectedIndex;
        }
    }

    private void FloatChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FloatChoice.SelectedIndex >= 0 && (FloatFormat)FloatChoice.SelectedIndex != Vm.FloatFormat)
        {
            Vm.FloatFormat = (FloatFormat)FloatChoice.SelectedIndex;
        }
    }

    private bool _syncingToggle;

    private void UtcToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle)
        {
            return;
        }

        Vm.TimeZoneMode = UtcToggle.IsChecked == true ? DateTimeZoneMode.Utc : DateTimeZoneMode.Local;
        UpdateUtcToggle();
    }

    private void UpdateUtcToggle()
    {
        bool utc = Vm.TimeZoneMode == DateTimeZoneMode.Utc;
        _syncingToggle = true;
        UtcToggle.IsChecked = utc;
        _syncingToggle = false;
        UtcToggle.Content = Loc.Get(utc ? "Inspector_ZoneUtc" : "Inspector_ZoneLocal");
        AutomationProperties.SetName(UtcToggle, Loc.Get("Inspector_Utc_Name"));
    }

    private void RowsFlyout_Opened(object sender, object e)
    {
        RowSettings.Rebuild();
        RowSettings.FocusList();
    }

    // ---- 一覧のキー操作 ----

    private void RowsList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or Button)
        {
            return;
        }

        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Tab && !shift && Vm.Selected is { IsBinary: true } row)
        {
            e.Handled = FocusBit(row, row.BitCount - 1);
        }
    }

    private void RowsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or Button)
        {
            return;
        }

        e.Handled = e.Key switch
        {
            VirtualKey.Enter => Activate(Vm.Selected, edit: true),
            VirtualKey.Space => Activate(Vm.Selected, edit: false),
            _ => false,
        };
    }

    /// <summary>Enter / Space: 見出しは折りたたみ、行は値の編集 (Enter) か、2 進の行のビットへ・GUID の展開 (Space)。</summary>
    private bool Activate(InspectorItemViewModel? item, bool edit)
    {
        switch (item)
        {
            case null:
                return false;
            case { IsGroup: true }:
                Vm.ToggleGroup(item);
                FocusItem(item);
                return true;
            case { Kind: InspectorItemKind.Row } when edit:
                return Vm.BeginEdit(item);
            case { IsBinary: true }:
                return FocusBit(item, item.BitCount - 1);
            case { CanExpand: true }:
                Vm.ToggleExpand(item);
                FocusItem(item);
                return true;
            default:
                return false;
        }
    }

    private void FocusItem(InspectorItemViewModel item)
    {
        Vm.Selected = item;
        RowsList.UpdateLayout();
        (RowsList.ContainerFromItem(item) as ListViewItem)?.Focus(FocusState.Keyboard);
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InspectorItemViewModel item } && item.Kind == InspectorItemKind.Row)
        {
            Vm.Selected = item;
            Vm.BeginEdit(item);
            e.Handled = true;
        }
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InspectorItemViewModel item })
        {
            Vm.Hovered = item;
        }
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InspectorItemViewModel item } && Vm.Hovered == item)
        {
            Vm.Hovered = null;
        }
    }

    // ---- 値の書き換え (INSP-17) ----

    private void Edit_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            AutomationProperties.SetName(box, Loc.Get("Inspector_Edit_Name"));
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        }
    }

    private void Edit_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: InspectorItemViewModel item } box)
        {
            Vm.UpdateEdit(item, box.Text);
            ShowError(box, item.HasError);
        }
    }

    private static void ShowError(TextBox box, bool error)
    {
        if (error)
        {
            box.BorderBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
        else
        {
            box.ClearValue(Control.BorderBrushProperty);
        }
    }

    private void Edit_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: InspectorItemViewModel item } box)
        {
            e.Handled = HandleEditKey(item, box, e.Key);
        }
    }

    /// <summary>入力欄のキー: Enter で確定 (不正なら入力欄のまま)、Esc で取り消す。</summary>
    internal bool HandleEditKey(InspectorItemViewModel item, TextBox box, VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Enter:
                if (Vm.CommitEdit(item))
                {
                    FocusItem(item);
                }
                else
                {
                    ShowError(box, true);
                }

                return true;
            case VirtualKey.Escape:
                Vm.CancelEdit();
                FocusItem(item);
                return true;
            default:
                return false;
        }
    }

    private void Edit_LostFocus(object sender, RoutedEventArgs e)
    {
        // フォーカスが外れたら取り消す (INSP-17 の仕様 1)。確定した後は入力中ではないので何もしない。
        if (sender is TextBox { DataContext: InspectorItemViewModel { IsEditing: true } })
        {
            Vm.CancelEdit();
        }
    }

    // ---- 2 進のビット (INSP-08) ----

    private void Bit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InspectorBitViewModel bit })
        {
            Vm.Selected = bit.Row;
            Vm.FlipBit(bit.Row, bit.Index);
        }
    }

    private void Bit_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InspectorBitViewModel bit })
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            e.Handled = HandleBitKey(bit, e.Key, shift);
        }
    }

    /// <summary>ビットのボタンのキー: ← / → でビットを移動、Shift+Tab / Esc で行に戻る (Space は押したのと同じ)。</summary>
    internal bool HandleBitKey(InspectorBitViewModel bit, VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.Left:
                return FocusBit(bit.Row, Math.Min(bit.Row.BitCount - 1, bit.Index + 1));
            case VirtualKey.Right:
                return FocusBit(bit.Row, Math.Max(0, bit.Index - 1));
            case VirtualKey.Tab when shift:
            case VirtualKey.Escape:
                FocusItem(bit.Row);
                return true;
            default:
                return false;
        }
    }

    /// <summary>2 進の行のビット (<paramref name="index"/> は 0 が最下位) のボタンにフォーカスを置く。</summary>
    private bool FocusBit(InspectorItemViewModel row, int index)
    {
        Vm.Selected = row;
        RowsList.UpdateLayout();
        return BitButton(row, index)?.Focus(FocusState.Keyboard) ?? false;
    }

    internal Button? BitButton(InspectorItemViewModel row, int index)
    {
        if (RowsList.ContainerFromItem(row) is not ListViewItem container)
        {
            return null;
        }

        string id = $"Inspector_Bit_{row.TypeId}_{index}";
        return Find(container, d => d is Button b && AutomationProperties.GetAutomationId(b) == id) as Button;
    }

    private static DependencyObject? Find(DependencyObject root, Func<DependencyObject, bool> match)
    {
        if (match(root))
        {
            return root;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ---- 右クリックメニュー (INSP-01 の仕様 7) ----

    private void RowsList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        _menuTarget = (args.OriginalSource as FrameworkElement)?.DataContext as InspectorItemViewModel ?? Vm.Selected;
    }

    private void RowMenu_Opening(object? sender, object e)
    {
        _menuTarget ??= Vm.Selected;
        bool row = _menuTarget is { Kind: InspectorItemKind.Row };
        MenuEdit.IsEnabled = row && !Vm.ReadOnly;
        MenuHide.IsEnabled = row;
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item)
        {
            CopyToClipboard(InspectorViewModel.CopyText(item));
        }
    }

    private void MenuCopyAll_Click(object sender, RoutedEventArgs e) => CopyToClipboard(Vm.AllRowsText());

    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item)
        {
            Vm.Selected = item;
            Vm.BeginEdit(item);
        }
    }

    private void MenuHide_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is { } item)
        {
            Vm.HideRow(item);
        }
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        SystemClipboard.SetContent(package);
    }

    // ---- テスト用の命令の通り道から使う入口 ----

    /// <summary>
    /// キー 1 つを、フォーカスのある要素のキー処理に渡す (テスト用のビルドの命令の通り道。実際のキー入力と同じ動作をする)。
    /// 処理したら true。
    /// </summary>
    internal bool InjectKey(VirtualKey key, bool shift)
    {
        object? focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        switch (focused)
        {
            case TextBox { DataContext: InspectorItemViewModel item } box when IsInside(box, RowsList):
                return HandleEditKey(item, box, key);
            case Button { Tag: InspectorBitViewModel bit } button:
                if (key is VirtualKey.Space or VirtualKey.Enter)
                {
                    Invoke(button);
                    return true;
                }

                return HandleBitKey(bit, key, shift);
            case DependencyObject d when IsInside(d, RowSettings):
                return InjectSettingsKey(d, key, shift);
            case ListViewItem when key is VirtualKey.Down or VirtualKey.Up:
            {
                int index = Vm.Selected is { } s ? Vm.Items.IndexOf(s) : -1;
                int target = Math.Clamp(index + (key == VirtualKey.Down ? 1 : -1), 0, Vm.Items.Count - 1);
                if (target >= 0 && target < Vm.Items.Count)
                {
                    FocusItem(Vm.Items[target]);
                }

                return true;
            }

            case ListViewItem when key == VirtualKey.Tab && !shift && Vm.Selected is { IsBinary: true } row:
                return FocusBit(row, row.BitCount - 1);
            case ListViewItem when key is VirtualKey.Enter or VirtualKey.Space:
                return Activate(Vm.Selected, edit: key == VirtualKey.Enter);
            case ListViewItem when key is VirtualKey.Application || key == VirtualKey.F10 && shift:
                _menuTarget = Vm.Selected;
                RowMenu.ShowAt(RowsList.ContainerFromItem(Vm.Selected) as FrameworkElement ?? RowsList);
                return true;
            case not null when key == VirtualKey.Tab:
                return FocusManager.TryMoveFocus(shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next,
                    new FindNextElementOptions { SearchRoot = XamlRoot!.Content });
            case Button button when key is VirtualKey.Enter or VirtualKey.Space:
                Invoke(button);
                return true;
            default:
                return false;
        }
    }

    private bool InjectSettingsKey(DependencyObject focused, VirtualKey key, bool shift)
    {
        switch (focused)
        {
            case ListViewItem or ListView:
                if (RowSettings.HandleListKey(key))
                {
                    return true;
                }

                break;
            case Button button when key is VirtualKey.Enter or VirtualKey.Space:
                Invoke(button);
                return true;
        }

        if (key == VirtualKey.Escape)
        {
            RowsFlyout.Hide();
            RowsButton.Focus(FocusState.Keyboard);
            return true;
        }

        if (key == VirtualKey.Tab)
        {
            return RowSettings.MoveFocus(focused, back: shift);
        }

        return false;
    }

    private static void Invoke(Button button)
    {
        if (FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
        {
            invoke.Invoke();
        }
    }

    /// <summary>行の設定のフライアウトが開いているか。</summary>
    internal bool RowSettingsOpen => RowsFlyout.IsOpen;
}
