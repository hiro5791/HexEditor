using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Hashing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HexEditor.App.Controls;

/// <summary>
/// ハッシュパネル (ANA-18、ANA-21、ANA-22) の表示。状態と処理は <see cref="HashPanelViewModel"/> に置き、ここはフォーカス・フライアウトなど
/// 表示に固有の処理だけを持つ。
/// </summary>
public sealed partial class HashPanel : UserControl
{
    public HashPanel(HashPanelViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Panel_Hash_Title"));
        TargetChoice.SelectedIndex = (int)viewModel.TargetKind;
        CustomEndChoice.SelectedIndex = viewModel.CustomUsesEnd ? 1 : 0;
        UpdateComputeStyle();

        // view model はウィンドウごとに 1 つで、パネルの中身は浮動パネルとの間を移るたびに作り直す。表示している間だけ通知を受ける。
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            TargetChoice.SelectedIndex = (int)ViewModel.TargetKind;
            UpdateComputeStyle();
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HashPanelViewModel.TargetKind))
        {
            TargetChoice.SelectedIndex = (int)ViewModel.TargetKind;
            CustomRange.Visibility = ViewModel.TargetKind == HashTargetKind.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(HashPanelViewModel.ComputeHighlighted))
        {
            UpdateComputeStyle();
        }
    }

    /// <summary>「照合」の期待値の入力欄にフォーカスを移す (「解析: ハッシュ値を照合」)。</summary>
    public void FocusExpected()
    {
        if (!IsLoaded)
        {
            // 開いたばかりのパネルは、表示されてからフォーカスを移す。
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                Loaded -= loaded;
                FocusExpected();
            };
            Loaded += loaded;
            return;
        }

        ExpectedBox.StartBringIntoView();
        ExpectedBox.Focus(FocusState.Programmatic);
        ExpectedBox.SelectAll();
    }

    /// <summary>2 つ目の欄を「長さ」と「終了 (このバイトを含む)」のどちらとして読むか (06 の 0.1)。</summary>
    private void CustomEndChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.CustomUsesEnd = CustomEndChoice.SelectedIndex == 1;
        CustomLengthBox.Header = Loc.Get(ViewModel.CustomUsesEnd ? "Hash_CustomEnd_Header" : "Hash_CustomLength/Header");
    }

    public HashPanelViewModel ViewModel { get; }

    /// <summary>「計算」ボタンを強調表示しているか (テスト用の状態の表示にも使う)。</summary>
    public bool IsComputeHighlighted => ViewModel.ComputeHighlighted;

    // x:Bind で使う AutomationId と名前 (行ごとに変わる)。
    public static string GroupId(string key) => "Hash_Group_" + key;

    public static string AlgorithmId(string id) => "Hash_Alg_" + id;

    public static string RowId(string id) => "Hash_Row_" + id;

    public static string ValueId(string id) => "Hash_Value_" + id;

    public static string CopyId(string id) => "Hash_Copy_" + id;

    public static string SettingsId(string id) => "Hash_Settings_" + id;

    public static string MatchIconId(string id) => "Hash_MatchIcon_" + id;

    public static string MismatchIconId(string id) => "Hash_MismatchIcon_" + id;

    public static string MatchTextId(string id) => "Hash_MatchText_" + id;

    public static string ChangedId(string id) => "Hash_Changed_" + id;

    /// <summary>パラメータが不正な行の赤枠 (ANA-18 の「エラー」)。色は ThemeResource のブラシ。</summary>
    public static Microsoft.UI.Xaml.Media.Brush RowBorder(bool invalid) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[invalid ? "SystemFillColorCriticalBrush" : "CardStrokeColorDefaultBrush"];

    public static Thickness RowBorderThickness(bool invalid) => new(invalid ? 2 : 1);

    /// <summary>除外の方法が「置き換える」か (値の欄を有効にする)。</summary>
    public static bool IsReplace(int modeIndex) => modeIndex == 1;

    public static string CopyName(string name) => Loc.Format("Hash_CopyRow_Name", name);

    public static string SettingsName(string name) => Loc.Format("Hash_Settings_Name", name);

    /// <summary>パネルを表示し、計算ボタンにフォーカスを移す。</summary>
    public void FocusFirst() => ComputeButton.Focus(FocusState.Programmatic);

    private void UpdateComputeStyle() =>
        ComputeButton.Style = ViewModel.ComputeHighlighted ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;

    private void TargetChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetChoice.SelectedIndex >= 0 && (HashTargetKind)TargetChoice.SelectedIndex != ViewModel.TargetKind)
        {
            ViewModel.ChooseTarget((HashTargetKind)TargetChoice.SelectedIndex);
        }

        if (CustomRange is not null)
        {
            CustomRange.Visibility = TargetChoice.SelectedIndex == (int)HashTargetKind.Custom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ByteOrder_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.LittleEndian = ByteOrderChoice.SelectedIndex == 1;

    private void SaveSetConfirm_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveSet(SetName.Text);
        SaveSetFlyout.Hide();
    }

    private static HashRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.Tag as HashRowViewModel;

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.CopyValue(row);
        }
    }

    private void CopyNameValue_Click(object sender, RoutedEventArgs e) => ViewModel.CopyNameValue(RowOf(sender));

    private void CopyChecksum_Click(object sender, RoutedEventArgs e) => ViewModel.CopyChecksumLine(RowOf(sender));

    private void CopyAllNameValue_Click(object sender, RoutedEventArgs e) => ViewModel.CopyNameValue(null);

    private void CopyAllChecksum_Click(object sender, RoutedEventArgs e) => ViewModel.CopyChecksumLine(null);

    private void CopyAllJson_Click(object sender, RoutedEventArgs e) => ViewModel.CopyJson();

    private void CopyAllCsv_Click(object sender, RoutedEventArgs e) => ViewModel.CopyCsv();

    private async void SaveChecksum_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.SaveChecksumFileAsync(row);
        }
    }

    private async void VerifyFile_Click(object sender, RoutedEventArgs e) => await ViewModel.VerifyWithFileAsync();

    private async void VerifyLines_ItemClick(object sender, ItemClickEventArgs e) =>
        await ViewModel.VerifyLineAsync(ViewModel.VerifyLines.IndexOf(e.ClickedItem as string ?? string.Empty));

    /// <summary>結果の行の「設定」: シード・結果の補数を入力するフライアウト (ANA-18 の仕様 8)。</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || sender is not FrameworkElement anchor)
        {
            return;
        }

        HashAlgorithmInfo algorithm = row.Row.Algorithm;
        HashParameters current = ViewModel.ParametersOf(algorithm.Id);
        var panel = new StackPanel { Spacing = 8, Width = 240 };
        TextBox? seed = null;
        ComboBox? complement = null;
        if (algorithm.Parameters.HasFlag(HashParameterKinds.Seed))
        {
            seed = new TextBox { Header = Loc.Get("Hash_Seed"), Text = $"0x{current.Seed:X}", FlowDirection = FlowDirection.LeftToRight };
            AutomationProperties.SetAutomationId(seed, "Hash_Seed");
            panel.Children.Add(seed);
        }

        ComboBox? endian = null, ed2k = null;
        CheckBox? signed = null;
        TextBox? key = null, outputBits = null;
        if (algorithm.Parameters.HasFlag(HashParameterKinds.Endian))
        {
            endian = new ComboBox { Header = Loc.Get("Hash_Endian"), HorizontalAlignment = HorizontalAlignment.Stretch };
            endian.Items.Add(Loc.Get("Hash_Endian_Default"));
            endian.Items.Add(Loc.Get("Hash_Endian_Little"));
            endian.Items.Add(Loc.Get("Hash_Endian_Big"));
            endian.SelectedIndex = current.BigEndian switch { null => 0, false => 1, true => 2 };
            AutomationProperties.SetAutomationId(endian, "Hash_Endian");
            panel.Children.Add(endian);
        }

        if (algorithm.Parameters.HasFlag(HashParameterKinds.Signed))
        {
            signed = new CheckBox { Content = Loc.Get("Hash_Signed"), IsChecked = current.Signed };
            AutomationProperties.SetAutomationId(signed, "Hash_Signed");
            panel.Children.Add(signed);
        }

        if (algorithm.Parameters.HasFlag(HashParameterKinds.Key))
        {
            key = new TextBox { Header = Loc.Get("Hash_Key"), Text = current.KeyHex ?? string.Empty, FlowDirection = FlowDirection.LeftToRight,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), PlaceholderText = Loc.Get("Hash_KeyPlaceholder") };
            AutomationProperties.SetAutomationId(key, "Hash_Key");
            panel.Children.Add(key);
        }

        if (algorithm.Parameters.HasFlag(HashParameterKinds.OutputLength))
        {
            outputBits = new TextBox { Header = Loc.Get("Hash_OutputBits"), Text = algorithm.BitsFor(current).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FlowDirection = FlowDirection.LeftToRight };
            AutomationProperties.SetAutomationId(outputBits, "Hash_OutputBits");
            panel.Children.Add(outputBits);
        }

        if (algorithm.Parameters.HasFlag(HashParameterKinds.Ed2kMode))
        {
            ed2k = new ComboBox { Header = Loc.Get("Hash_Ed2k"), HorizontalAlignment = HorizontalAlignment.Stretch };
            ed2k.Items.Add(Loc.Get("Hash_Ed2k_Blue"));
            ed2k.Items.Add(Loc.Get("Hash_Ed2k_Red"));
            ed2k.SelectedIndex = (int)current.Ed2k;
            AutomationProperties.SetAutomationId(ed2k, "Hash_Ed2kMode");
            panel.Children.Add(ed2k);
        }

        if (algorithm.Parameters.HasFlag(HashParameterKinds.Complement))
        {
            complement = new ComboBox { Header = Loc.Get("Hash_Complement"), HorizontalAlignment = HorizontalAlignment.Stretch };
            complement.Items.Add(Loc.Get("Hash_Complement_None"));
            complement.Items.Add(Loc.Get("Hash_Complement_Ones"));
            complement.Items.Add(Loc.Get("Hash_Complement_Twos"));
            complement.SelectedIndex = (int)current.Complement;
            AutomationProperties.SetAutomationId(complement, "Hash_Complement");
            panel.Children.Add(complement);
        }

        var error = new TextBlock
        {
            Text = Loc.Get("Hash_SeedInvalid"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(error);
        var flyout = new Flyout { Content = panel };
        var apply = new Button { Content = Loc.Get("Hash_ApplySettings"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(apply, "Hash_ApplySettings");
        apply.Click += (_, _) =>
        {
            ulong value = current.Seed;
            if (seed is not null && !HashSelection.TryParseSeed(seed.Text, out value))
            {
                error.Text = Loc.Get("Hash_SeedInvalid");
                error.Visibility = Visibility.Visible;
                return;
            }

            string? keyHex = current.KeyHex;
            if (key is not null)
            {
                string compact = new([.. key.Text.Where(c => !char.IsWhiteSpace(c) && c is not ':' and not '-')]);
                if (compact.Length > 0 && (compact.Length % 2 != 0 || !compact.All(char.IsAsciiHexDigit)))
                {
                    error.Text = Loc.Get("Hash_KeyInvalid");
                    error.Visibility = Visibility.Visible;
                    return;
                }

                keyHex = compact.Length == 0 ? null : compact.ToUpperInvariant();
            }

            int bits = current.OutputBits;
            if (outputBits is not null && !int.TryParse(outputBits.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bits))
            {
                error.Text = Loc.Get("Hash_OutputBitsInvalid");
                error.Visibility = Visibility.Visible;
                return;
            }

            HashParameters updated = current with
            {
                Seed = value,
                Complement = complement is null ? current.Complement : (HashComplement)Math.Max(0, complement.SelectedIndex),
                BigEndian = endian is null ? current.BigEndian : endian.SelectedIndex switch { 1 => false, 2 => true, _ => null },
                Signed = signed is null ? current.Signed : signed.IsChecked == true,
                KeyHex = keyHex,
                OutputBits = bits == algorithm.Bits ? 0 : bits,
                Ed2k = ed2k is null ? current.Ed2k : (Ed2kMode)Math.Max(0, ed2k.SelectedIndex),
            };
            if (algorithm.Validate(updated) is var problem && problem != HashParameterError.None)
            {
                error.Text = Loc.Get("Hash_ParameterError_" + problem);
                error.Visibility = Visibility.Visible;
                return;
            }

            ViewModel.SetParameters(algorithm.Id, updated);
            flyout.Hide();
        };
        panel.Children.Add(apply);
        flyout.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }

    // ---- 除外範囲 (ANA-18 の仕様 2) ----

    private void ExclusionAdd_Click(object sender, RoutedEventArgs e) => ViewModel.AddExclusion("0", "1");

    private void ExclusionAddSelection_Click(object sender, RoutedEventArgs e) => ViewModel.AddSelectionExclusion();

    private void ExclusionRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HashExclusionViewModel item)
        {
            ViewModel.RemoveExclusion(item);
        }
    }

    private void AddUnreadable_Click(object sender, RoutedEventArgs e) => ViewModel.AddUnreadableExclusion();

    // ---- 一致するアルゴリズムを探す、カーソル位置に書き込む、カスタム CRC ----

    private async void FindAlgorithm_Click(object sender, RoutedEventArgs e) => await ViewModel.FindMatchingAlgorithmsAsync();

    private async void WriteAtCursor_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row && ViewModel.WriteAtCursor is { } write)
        {
            await write(row);
        }
    }

    private async void CustomCrcAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AddCustomCrc is { } add)
        {
            await add();
        }
    }

    private async void CustomCrcExport_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ExportCustomCrc is { } export)
        {
            await export();
        }
    }

    private async void CustomCrcImport_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ImportCustomCrc is { } import)
        {
            await import();
        }
    }

    /// <summary>「削除」のサブメニューに、定義したカスタム CRC を並べる。</summary>
    private void CustomCrcMenu_Opening(object? sender, object e)
    {
        CustomCrcDeleteMenu.Items.Clear();
        foreach (HashAlgorithmInfo custom in HashCatalog.Custom)
        {
            var item = new MenuFlyoutItem { Text = custom.Name, Tag = custom.Name };
            AutomationProperties.SetAutomationId(item, "Hash_CustomCrcDelete_" + custom.Name);
            item.Click += (_, _) => ViewModel.RemoveCustomCrc?.Invoke(custom.Name);
            CustomCrcDeleteMenu.Items.Add(item);
        }

        CustomCrcDeleteMenu.IsEnabled = HashCatalog.Custom.Count > 0;
    }
}
