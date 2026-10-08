using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Inspector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>行の設定の一覧の項目 (グループの見出しか型の行)。</summary>
public sealed partial class RowSettingItem : ObservableObject
{
    public RowSettingItem(InspectorGroup group, string? typeId, string name, bool isChecked)
    {
        Group = group;
        TypeId = typeId;
        Name = name;
        IsChecked = isChecked;
    }

    public InspectorGroup Group { get; }

    public string? TypeId { get; }

    public bool IsGroup => TypeId is null;

    public string Name { get; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public string AutomationId => IsGroup ? "Inspector_SettingGroup_" + Group : "Inspector_Setting_" + TypeId;

    public override string ToString() => Name;
}

/// <summary>行の設定の一覧の項目の見た目を選ぶ。</summary>
public sealed partial class RowSettingTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Group { get; set; }

    public DataTemplate? Row { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is RowSettingItem { IsGroup: true } ? Group : Row;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// インスペクタの行の設定 (INSP-19)。表示する行・順序・反対のエンディアンの行・日時の書式・プリセットを決める。変更はすぐ反映し、
/// アプリ全体の設定として保存する。
/// </summary>
public sealed partial class InspectorRowSettings : UserControl
{
    /// <summary>名前を付けたプリセット (settings.json に JSON の文字列で保存する)。</summary>
    public const string PresetsKey = "inspector.rowPresets";

    private readonly System.Collections.ObjectModel.ObservableCollection<RowSettingItem> _items = [];
    private RowSettingItem? _dragged;
    private InspectorViewModel? _vm;
    private bool _updating;

    public InspectorRowSettings()
    {
        InitializeComponent();
        AutomationProperties.SetName(SettingsList, Loc.Get("Inspector_SettingsList_Name"));
        AutomationProperties.SetName(PresetChoice, Loc.Get("Inspector_PresetLabel/Text"));
        AutomationProperties.SetName(DateFormatChoice, Loc.Get("Inspector_DateFormatLabel/Text"));
    }

    public InspectorViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
            {
                _vm.LayoutChanged -= Vm_LayoutChanged;
            }

            _vm = value;
            if (_vm is not null)
            {
                _vm.LayoutChanged += Vm_LayoutChanged;
            }

            Rebuild();
        }
    }

    private void Vm_LayoutChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>一覧を作り直す (選んでいた項目は選んだまま)。</summary>
    public void Rebuild()
    {
        if (_vm is null)
        {
            return;
        }

        _updating = true;
        string? selectedKey = (SettingsList.SelectedItem as RowSettingItem)?.AutomationId;
        InspectorLayout layout = _vm.Layout;
        _items.Clear();
        foreach (InspectorGroup group in layout.Groups)
        {
            _items.Add(new RowSettingItem(group, null, Loc.Get("Inspector_Group_" + group), true));
            foreach (InspectorRowConfig row in layout.RowsIn(group))
            {
                _items.Add(new RowSettingItem(group, row.TypeId, InspectorViewModel.RowName(row.TypeId, false, Endianness.Little), row.Visible));
            }
        }

        SettingsList.ItemsSource = null;
        SettingsList.ItemsSource = _items;
        if (_items.FirstOrDefault(i => i.AutomationId == selectedKey) is { } again)
        {
            SettingsList.SelectedItem = again;
        }

        DateFormatChoice.SelectedIndex = _vm.DateTimeStyle == DateTimeStyle.Iso8601 ? 1 : 0;
        UpdatePresets();
        UpdateButtons();
        _updating = false;
    }

    private void UpdatePresets()
    {
        while (PresetChoice.Items.Count > 4)
        {
            PresetChoice.Items.RemoveAt(PresetChoice.Items.Count - 1);
        }

        foreach (string name in SavedPresets().Select(p => p.Key))
        {
            PresetChoice.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }

        InspectorLayout layout = _vm!.Layout;
        int index = Enum.GetValues<InspectorPreset>().ToList().FindIndex(p => InspectorLayout.FromPreset(p).Equals(layout));
        PresetChoice.SelectedIndex = index;
    }

    private static JsonObject SavedPresets()
    {
        try
        {
            return JsonNode.Parse(App.Settings.GetString(PresetsKey, "{}")) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void UpdateButtons()
    {
        var item = SettingsList.SelectedItem as RowSettingItem;
        UpButton.IsEnabled = item is not null;
        DownButton.IsEnabled = item is not null;
        bool hasEndian = item?.TypeId is { } id && InspectorTypes.Get(id).HasEndian;
        OppositeBox.IsEnabled = hasEndian;
        OppositeBox.IsChecked = hasEndian && _vm!.Layout.Row(item!.TypeId!)!.Opposite;
    }

    /// <summary>開いたら一覧の先頭の行にフォーカスを置く (キーボードだけで操作する。INSP-19 の受け入れ基準 4)。</summary>
    public void FocusList()
    {
        if (SettingsList.SelectedItem is null && _items.FirstOrDefault(i => !i.IsGroup) is { } first)
        {
            SettingsList.SelectedItem = first;
        }

        if (SettingsList.SelectedItem is { } selected)
        {
            SettingsList.UpdateLayout();
            SettingsList.ScrollIntoView(selected);
            SettingsList.UpdateLayout();
            (SettingsList.ContainerFromItem(selected) as Control)?.Focus(FocusState.Keyboard);
        }
    }

    // 作り直しの途中で選択が外れたときにボタンを無効にしない (押したボタンからフォーカスが外れないように)。
    private void SettingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            UpdateButtons();
        }
    }

    /// <summary>行の要素に名前を付ける (読み上げ: 「int32、表示する」)。</summary>
    private void SettingsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is RowSettingItem item && args.ItemContainer is ListViewItem container)
        {
            AutomationProperties.SetAutomationId(container, item.IsGroup ? "Inspector_SettingGroupItem_" + item.Group : "Inspector_SettingItem_" + item.TypeId);
            AutomationProperties.SetName(container, item.Name);
        }
    }

    private void SettingsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space && SettingsList.SelectedItem is RowSettingItem { IsGroup: false } item)
        {
            Toggle(item);
            e.Handled = true;
        }
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: RowSettingItem item } box)
        {
            item.IsChecked = box.IsChecked == true;
            Apply(_vm!.Layout.WithVisible(item.TypeId!, item.IsChecked));
        }
    }

    /// <summary>行の表示を切り替える (Space、チェックボックス)。</summary>
    public void Toggle(RowSettingItem item)
    {
        if (item.IsGroup || _vm is null)
        {
            return;
        }

        item.IsChecked = !item.IsChecked;
        Apply(_vm.Layout.WithVisible(item.TypeId!, item.IsChecked));
    }

    // ---- ドラッグでの並べ替え (INSP-19 の仕様 2) ----

    private void SettingsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        _dragged = e.Items.Count == 1 ? e.Items[0] as RowSettingItem : null;

    /// <summary>一覧の中でドラッグして落とした: 落とした位置に合わせて行 (グループ) を動かす。</summary>
    private void SettingsList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        RowSettingItem? dragged = _dragged;
        _dragged = null;
        if (dragged is null || args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
        {
            Rebuild();
            return;
        }

        int index = _items.IndexOf(dragged);
        if (index >= 0)
        {
            ReorderTo(dragged, index);
        }
    }

    /// <summary>
    /// 一覧の並びで <paramref name="item"/> を <paramref name="newIndex"/> に置いたときの順序にする。行は同じグループの中での位置、
    /// グループの見出しはグループの順序で決める (ドラッグの結果。テスト用の命令からも呼ぶ)。
    /// </summary>
    internal void ReorderTo(RowSettingItem item, int newIndex)
    {
        if (_vm is null)
        {
            return;
        }

        // 置いた後の並び (一覧がまだ元の並びのとき、ここで動かした並びを作る)。
        List<RowSettingItem> order = [.. _items];
        order.Remove(item);
        order.Insert(Math.Clamp(newIndex, 0, order.Count), item);
        InspectorLayout layout = _vm.Layout;
        if (item.IsGroup)
        {
            List<InspectorGroup> before = [.. layout.Groups];
            List<InspectorGroup> after = [.. order.Where(i => i.IsGroup).Select(i => i.Group)];
            Apply(layout.MoveGroup(item.Group, after.IndexOf(item.Group) - before.IndexOf(item.Group)));
        }
        else
        {
            List<string> before = [.. layout.RowsIn(item.Group).Select(r => r.TypeId)];
            List<string> after = [.. order.Where(i => !i.IsGroup && i.Group == item.Group).Select(i => i.TypeId!)];
            Apply(layout.MoveRow(item.TypeId!, after.IndexOf(item.TypeId!) - before.IndexOf(item.TypeId!)));
        }
    }

    /// <summary>行の設定の項目 (テスト用)。</summary>
    internal IReadOnlyList<RowSettingItem> Items => _items;

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    /// <summary>選んでいる行 (またはグループ) を上下に動かす (INSP-19 の仕様 2)。</summary>
    public void Move(int delta)
    {
        if (_vm is null || SettingsList.SelectedItem is not RowSettingItem item)
        {
            return;
        }

        Apply(item.IsGroup ? _vm.Layout.MoveGroup(item.Group, delta) : _vm.Layout.MoveRow(item.TypeId!, delta));
    }

    private void Opposite_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && SettingsList.SelectedItem is RowSettingItem { TypeId: { } id })
        {
            Apply(_vm.Layout.WithOpposite(id, OppositeBox.IsChecked == true));
        }
    }

    private void DateFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && _vm is not null && DateFormatChoice.SelectedIndex >= 0)
        {
            _vm.DateTimeStyle = DateFormatChoice.SelectedIndex == 1 ? DateTimeStyle.Iso8601 : DateTimeStyle.Regional;
        }
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _vm is null || PresetChoice.SelectedIndex < 0)
        {
            return;
        }

        if (PresetChoice.SelectedIndex < 4)
        {
            Apply(InspectorLayout.FromPreset((InspectorPreset)PresetChoice.SelectedIndex));
        }
        else if (PresetChoice.SelectedItem is ComboBoxItem { Tag: string name } && SavedPresets()[name] is JsonValue v && v.TryGetValue(out string? text))
        {
            Apply(InspectorLayout.Parse(text));
        }
    }

    /// <summary>「既定に戻す」: プリセット「基本」に戻す (INSP-19 の仕様 5)。</summary>
    public void Reset() => Apply(InspectorLayout.Default);

    private void Reset_Click(object sender, RoutedEventArgs e) => Reset();

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        string name = PresetName.Text.Trim();
        if (name.Length == 0 || _vm is null)
        {
            return;
        }

        JsonObject presets = SavedPresets();
        presets[name] = _vm.Layout.Serialize();
        App.Settings.SetString(PresetsKey, presets.ToJsonString(), "{}");
        PresetName.Text = string.Empty;
        _updating = true;
        UpdatePresets();
        _updating = false;
    }

    private void Apply(InspectorLayout layout)
    {
        // 一覧を作り直すと選んでいる項目のフォーカスが外れるので、フォーカスのある場所を覚えて戻す。
        bool listFocused = SettingsList.FocusState != FocusState.Unfocused
            || FocusManager.GetFocusedElement(XamlRoot) is ListViewItem;
        _vm!.SetLayout(layout);
        if (listFocused)
        {
            FocusList();
        }
    }

    /// <summary>Tab / Shift+Tab と同じ順 (TabIndex の順) に、次 / 前の要素へフォーカスを移す。</summary>
    internal bool MoveFocus(DependencyObject focused, bool back)
    {
        Control[] order = [SettingsList, UpButton, DownButton, OppositeBox, DateFormatChoice, PresetChoice, ResetButton, PresetName, SavePresetButton];
        int index = Array.FindIndex(order, c => c == focused || IsInside(focused, c));
        for (int step = 1; step <= order.Length; step++)
        {
            Control next = order[((index < 0 ? -1 : index) + (back ? -step : step) + order.Length * 2) % order.Length];
            if (next.IsEnabled && next.Visibility == Visibility.Visible)
            {
                if (next == SettingsList)
                {
                    FocusList();
                    return true;
                }

                return next.Focus(FocusState.Keyboard);
            }
        }

        return false;
    }

    private static bool IsInside(DependencyObject node, DependencyObject root)
    {
        for (DependencyObject? n = node; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
        {
            if (n == root)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>キー 1 つ (テスト用の命令の通り道と同じ処理)。処理したら true。</summary>
    internal bool HandleListKey(VirtualKey key)
    {
        int index = SettingsList.SelectedIndex;
        switch (key)
        {
            case VirtualKey.Down when index + 1 < _items.Count:
                SettingsList.SelectedIndex = index + 1;
                FocusList();
                return true;
            case VirtualKey.Up when index > 0:
                SettingsList.SelectedIndex = index - 1;
                FocusList();
                return true;
            case VirtualKey.Space when SettingsList.SelectedItem is RowSettingItem item:
                Toggle(item);
                return true;
            default:
                return false;
        }
    }
}
