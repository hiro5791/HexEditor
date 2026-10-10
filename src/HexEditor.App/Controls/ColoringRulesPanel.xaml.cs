using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Coloring;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>「色付けルール」パネル (INSP-33、INSP-34)。表示と入力だけを持ち、ルールの変更は <see cref="ColoringRulesViewModel"/> が行う。</summary>
public sealed partial class ColoringRulesPanel : UserControl, Panels.IPanelContent
{
    private static readonly int[] CodePages = [65001, 20127, 28591, 1252, 1200, 1201, 932, 51932];
    private static readonly string[] NumberTypes = ["int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "float", "double"];
    private bool _loading;

    public ColoringRulesPanel(ColoringRulesViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Coloring_PanelName"));
        AutomationProperties.SetName(List, Loc.Get("Coloring_PanelName"));
        foreach ((Button button, string key) in new[]
        {
            (AddButton, "Coloring_AddName"), (DeleteButton, "Coloring_DeleteName"), (UpButton, "Coloring_MoveUpName"), (DownButton, "Coloring_MoveDownName"),
        })
        {
            AutomationProperties.SetName(button, Loc.Get(key));
            ToolTipService.SetToolTip(button, Loc.Get(key));
        }

        foreach (ColoringConditionKind kind in Enum.GetValues<ColoringConditionKind>())
        {
            KindChoice.Items.Add(new ComboBoxItem { Content = Loc.Get("Coloring_Kind_" + kind), Tag = kind });
        }

        foreach (int cp in CodePages)
        {
            string name = cp switch { 1200 => "UTF-16 LE", 1201 => "UTF-16 BE", _ => CompiledColoringRule.EncodingOf(cp).WebName };
            CodePageChoice.Items.Add(new ComboBoxItem { Content = name, Tag = cp });
        }

        foreach (string type in NumberTypes)
        {
            NumberTypeChoice.Items.Add(new ComboBoxItem { Content = type, Tag = type });
        }

        foreach (ColoringBorder border in Enum.GetValues<ColoringBorder>())
        {
            BorderChoice.Items.Add(new ComboBoxItem { Content = Loc.Get("Coloring_Border_" + border), Tag = border });
        }

        foreach (ColoringTarget target in Enum.GetValues<ColoringTarget>())
        {
            TargetChoice.Items.Add(new ComboBoxItem { Content = Loc.Get("Coloring_Target_" + target), Tag = target });
        }

        AutomationProperties.SetName(PatternBox, Loc.Get("Coloring_PatternName"));
        AutomationProperties.SetName(ForegroundText, Loc.Get("Coloring_UseForeground/Content"));
        AutomationProperties.SetName(BackgroundText, Loc.Get("Coloring_UseBackground/Content"));
        Vm.SelectedRuleChanged += Vm_SelectedRuleChanged;
        Loaded += (_, _) =>
        {
            Vm.SelectedRuleChanged -= Vm_SelectedRuleChanged;
            Vm.SelectedRuleChanged += Vm_SelectedRuleChanged;
            SyncScope();
            LoadEditor();
        };
        Unloaded += (_, _) => Vm.SelectedRuleChanged -= Vm_SelectedRuleChanged;
        SyncScope();
        LoadEditor();
    }

    public ColoringRulesViewModel Vm { get; }

    bool Panels.IPanelContent.FocusContent()
    {
        if (Vm.Items.Count == 0)
        {
            return AddButton.Focus(FocusState.Keyboard);
        }

        if (List.SelectedIndex < 0)
        {
            List.SelectedIndex = 0;
        }

        List.UpdateLayout();
        return List.ContainerFromIndex(List.SelectedIndex) is ListViewItem item ? item.Focus(FocusState.Keyboard) : List.Focus(FocusState.Keyboard);
    }

    private void SyncScope()
    {
        _loading = true;
        DocumentScope.IsChecked = Vm.Scope == ColoringScope.Document;
        GlobalScope.IsChecked = Vm.Scope == ColoringScope.Global;
        _loading = false;
    }

    private void Vm_SelectedRuleChanged(object? sender, EventArgs e) => LoadEditor();

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            Vm.Scope = GlobalScope.IsChecked == true ? ColoringScope.Global : ColoringScope.Document;
            LoadEditor();
        }
    }

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ColoringRuleItem item && args.ItemContainer is ListViewItem container)
        {
            AutomationProperties.SetName(container, item.AutomationName);
            AutomationProperties.SetAutomationId(container, "Coloring_Rule_" + item.Name);
        }
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e) => e.Handled = HandleKey(e.Key);

    /// <summary>一覧のキー: Enter で編集欄へ、Delete で削除、Space で有効 / 無効 (INSP-33 の仕様 7)。</summary>
    internal bool HandleKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Enter when Vm.Selected is not null:
                NameBox.Focus(FocusState.Keyboard);
                return true;
            case VirtualKey.Delete when Vm.Selected is not null:
                Vm.Delete();
                return true;
            case VirtualKey.Space when Vm.Selected is { } item:
                Vm.SetEnabled(item, !item.Enabled);
                return true;
            default:
                return false;
        }
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ColoringRuleItem item } box)
        {
            Vm.SetEnabled(item, box.IsChecked == true);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Vm.Add();

    private void Delete_Click(object sender, RoutedEventArgs e) => Vm.Delete();

    private void Up_Click(object sender, RoutedEventArgs e) => Vm.Move(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => Vm.Move(1);

    // ---- 編集欄 ----

    /// <summary>選んだルールを編集欄に入れる。</summary>
    private void LoadEditor()
    {
        _loading = true;
        ColoringRule? rule = Vm.Selected?.Rule;
        Editor.Visibility = rule is null ? Visibility.Collapsed : Visibility.Visible;
        if (rule is not null)
        {
            Set(NameBox, rule.Name);
            KindChoice.SelectedIndex = (int)rule.Kind;
            Set(PatternBox, rule.Pattern);
            Set(EndBox, rule.EndExpression);
            Set(ModulusBox, rule.Modulus.ToString(CultureInfo.InvariantCulture));
            Set(RemainderBox, rule.Remainder.ToString(CultureInfo.InvariantCulture));
            Set(PeriodLengthBox, rule.PeriodLength.ToString(CultureInfo.InvariantCulture));
            CodePageChoice.SelectedIndex = Math.Max(0, Array.IndexOf(CodePages, rule.CodePage));
            CaseBox.IsChecked = rule.CaseSensitive;
            RegexTextBox.IsChecked = rule.RegexOnText;
            NumberTypeChoice.SelectedIndex = Math.Max(0, Array.IndexOf(NumberTypes, rule.NumberType));
            BigEndianBox.IsChecked = rule.BigEndian;
            ForegroundBox.IsChecked = rule.Foreground is not null;
            Set(ForegroundText, rule.Foreground is { } f ? ColoringRule.Hex(f) : ForegroundText.Text.Length > 0 ? ForegroundText.Text : "#C00000");
            BackgroundBox.IsChecked = rule.Background is not null;
            Set(BackgroundText, rule.Background is { } b ? ColoringRule.Hex(b) : BackgroundText.Text.Length > 0 ? BackgroundText.Text : "#FFE680");
            BorderChoice.SelectedIndex = (int)rule.Border;
            TargetChoice.SelectedIndex = (int)rule.Target;
            Set(RangeStartBox, rule.RangeStart);
            Set(RangeEndBox, rule.RangeEnd);
            UpdateFieldVisibility(rule.Kind);
            ShowError(Vm.Selected!.Error);
        }

        _loading = false;
    }

    private static void Set(TextBox box, string text)
    {
        if (box.Text != text)
        {
            box.Text = text;
        }
    }

    /// <summary>条件の種類に合う欄だけを出す。</summary>
    private void UpdateFieldVisibility(ColoringConditionKind kind)
    {
        PatternBox.Header = Loc.Get("Coloring_PatternHeader_" + kind);
        PatternBox.PlaceholderText = kind switch
        {
            ColoringConditionKind.ByteValues => "20-7E, 7F",
            ColoringConditionKind.OffsetRange => "0x0",
            ColoringConditionKind.HexPattern => "50 4B 03 04",
            ColoringConditionKind.Text => "PNG",
            ColoringConditionKind.Regex => @"[\x00-\x1F]{8,}",
            ColoringConditionKind.Number => "0..0xFF",
            _ => string.Empty,
        };
        PatternBox.Visibility = kind == ColoringConditionKind.Period ? Visibility.Collapsed : Visibility.Visible;
        EndBox.Visibility = kind == ColoringConditionKind.OffsetRange ? Visibility.Visible : Visibility.Collapsed;
        PeriodFields.Visibility = kind is ColoringConditionKind.Period or ColoringConditionKind.Number ? Visibility.Visible : Visibility.Collapsed;
        PeriodLengthBox.Visibility = kind == ColoringConditionKind.Period ? Visibility.Visible : Visibility.Collapsed;
        TextFields.Visibility = kind is ColoringConditionKind.Text or ColoringConditionKind.Regex ? Visibility.Visible : Visibility.Collapsed;
        RegexTextBox.Visibility = kind == ColoringConditionKind.Regex ? Visibility.Visible : Visibility.Collapsed;
        NumberFields.Visibility = kind == ColoringConditionKind.Number ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string error)
    {
        ErrorText.Text = error;
        ErrorText.Visibility = error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Brush? critical = error.Length > 0 ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : null;
        if (critical is null)
        {
            PatternBox.ClearValue(Control.BorderBrushProperty);
        }
        else
        {
            PatternBox.BorderBrush = critical;
        }
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => Apply();

    private void Field_SelectionChanged(object sender, SelectionChangedEventArgs e) => Apply();

    private void Field_Click(object sender, RoutedEventArgs e) => Apply();

    /// <summary>編集欄の値でルールを書き換える (入力と同時に反映する)。</summary>
    private void Apply()
    {
        if (_loading || Vm.Selected is not { } item)
        {
            return;
        }

        static long Number(TextBox box, long fallback) =>
            long.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;
        var kind = (ColoringConditionKind)Math.Max(0, KindChoice.SelectedIndex);
        Vm.Update(item, r => r with
        {
            Name = NameBox.Text,
            Kind = kind,
            Pattern = PatternBox.Text,
            EndExpression = EndBox.Text,
            Modulus = Math.Max(1, Number(ModulusBox, r.Modulus)),
            Remainder = Number(RemainderBox, r.Remainder),
            PeriodLength = Math.Max(1, Number(PeriodLengthBox, r.PeriodLength)),
            CodePage = CodePageChoice.SelectedItem is ComboBoxItem { Tag: int cp } ? cp : r.CodePage,
            CaseSensitive = CaseBox.IsChecked == true,
            RegexOnText = RegexTextBox.IsChecked == true,
            NumberType = NumberTypeChoice.SelectedItem is ComboBoxItem { Tag: string t } ? t : r.NumberType,
            BigEndian = BigEndianBox.IsChecked == true,
            Foreground = ForegroundBox.IsChecked == true ? ColoringRule.ParseHex(ForegroundText.Text) ?? r.Foreground : null,
            Background = BackgroundBox.IsChecked == true ? ColoringRule.ParseHex(BackgroundText.Text) ?? r.Background : null,
            Border = BorderChoice.SelectedItem is ComboBoxItem { Tag: ColoringBorder b } ? b : r.Border,
            Target = TargetChoice.SelectedItem is ComboBoxItem { Tag: ColoringTarget g } ? g : r.Target,
            RangeStart = RangeStartBox.Text,
            RangeEnd = RangeEndBox.Text,
        });
        UpdateFieldVisibility(kind);
        if (Vm.Items.FirstOrDefault(i => i.Id == item.Id) is { } updated)
        {
            ShowError(updated.Error);
        }
    }

    /// <summary>テスト用の命令の通り道: ルールを選ぶ。</summary>
    internal bool Select(string name)
    {
        Vm.Selected = Vm.Items.FirstOrDefault(i => i.Name == name);
        return Vm.Selected is not null;
    }
}
