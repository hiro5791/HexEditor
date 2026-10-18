using System.Globalization;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Disassembly;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Disassembly.Msil;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>逆アセンブルパネルからウィンドウに頼む処理 (MainWindow.Disassembly.cs が設定する)。</summary>
public sealed class DisassemblyPanelActions
{
    public Action<DisasmRow> GoToTarget { get; set; } = _ => { };

    public Action<DisasmRow> FindReferences { get; set; } = _ => { };

    public Action<DisasmRow> Assemble { get; set; } = _ => { };

    public Action<IReadOnlyList<DisasmRow>> Copy { get; set; } = _ => { };

    public Action Export { get; set; } = () => { };

    public Action<string> StartFrom { get; set; } = _ => { };

    /// <summary>命令パターン検索 (ANA-28)。<c>direction</c> は 1 (次)、-1 (前)、0 (すべて)。構文エラーなら理由を返す。</summary>
    public Func<string, bool, int, Task<string?>> FindPattern { get; set; } = (_, _, _) => Task.FromResult<string?>(null);

    public Func<DisasmRow, string?> ToolTip { get; set; } = _ => null;

    /// <summary>表示設定 (ANA-26) を変える (設定に保存し、全ウィンドウに反映する)。</summary>
    public Action<DisasmFormat> SetFormat { get; set; } = _ => { };
}

/// <summary>
/// 逆アセンブルパネル (ANA-23〜ANA-28) の表示。状態は <see cref="DisassemblyViewModel"/> (ウィンドウごとに 1 つ)、ウィンドウの処理は
/// <see cref="DisassemblyPanelActions"/>。
/// </summary>
public sealed partial class DisassemblyPanel : UserControl, IPanelContent
{
    private readonly DisassemblyViewModel _vm;
    private readonly DisassemblyPanelActions _actions;
    private bool _updating;
    private readonly List<string> _patternHistory = [];
    private int _historyIndex = -1;
    private MenuFlyout? _rowMenu;

    public DisassemblyPanel(DisassemblyViewModel vm, DisassemblyPanelActions actions)
    {
        _vm = vm;
        _actions = actions;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Panel_Disassembly_Title"));
        InitializeTexts();
        Listing.ToolTipProvider = actions.ToolTip;
        Listing.GoToTargetRequested += (_, row) => _actions.GoToTarget(row);
        Listing.CopyRequested += (_, row) => _actions.Copy([row]);
        Listing.ContextMenuRequested += (_, e) => ShowRowMenu(e.Row, e.Position);
        SizeChanged += (_, e) => ApplyNarrowLayout(e.NewSize.Width);
        Attach();

        // 浮動パネルとの間を移るときは木から外れて付き直す。外れている間は状態の変化を受けない。
        Loaded += (_, _) =>
        {
            Attach();
            UpdateToolbar();
            UpdateLists();
            UpdateBars();
        };
        Unloaded += (_, _) =>
        {
            vm.RowsChanged -= Vm_RowsChanged;
            vm.ImageChanged -= Vm_ImageChanged;
            Listing.Detach();
            _attached = false;
        };
        UpdateToolbar();
        UpdateLists();
        UpdateBars();
    }

    private bool _attached;

    private void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _vm.RowsChanged += Vm_RowsChanged;
        _vm.ImageChanged += Vm_ImageChanged;
        Listing.ViewModel = _vm;
    }

    public DisassemblyListingView ListingView => Listing;

    public bool FocusContent() => Listing.Focus(FocusState.Keyboard);

    private void Vm_RowsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateBars);

    private void Vm_ImageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateToolbar();
        UpdateLists();
        UpdateBars();
    });

    private void InitializeTexts()
    {
        SetTip(ArchBox, "Disasm_Architecture");
        SetTip(ModeBox, "Disasm_Mode");
        SetTip(EndianBox, "Disasm_Endian");
        SetTip(SliceBox, "Disasm_Slice");
        StartButton.Content = Loc.Get("Disasm_Start");
        SetTip(StartButton, "Disasm_StartTip");
        BaseBox.PlaceholderText = Loc.Get("Disasm_BaseAuto");
        SetTip(BaseBox, "Disasm_BaseAddress");
        SetTip(SyntaxBox, "Disasm_Syntax");
        OptionsButton.Content = Loc.Get("Disasm_Options");
        FollowToggle.Content = Loc.Get("Disasm_Follow");
        FindButton.Content = Loc.Get("Disasm_FindPattern");
        PatternBox.PlaceholderText = Loc.Get("Disasm_PatternPlaceholder");
        AutomationProperties.SetName(PatternBox, Loc.Get("Disasm_FindPattern"));
        RegexCheck.Content = Loc.Get("Disasm_PatternRegex");
        PatternFindAll.Content = Loc.Get("Disasm_PatternFindAll");
        SetTip(PatternClose, "Disasm_PatternClose");
        SectionsTab.Text = Loc.Get("Disasm_Sections");
        SymbolsTab.Text = Loc.Get("Disasm_Symbols");
        SymbolFilter.PlaceholderText = Loc.Get("Disasm_SymbolFilter");
        AutomationProperties.SetName(SymbolFilter, Loc.Get("Disasm_SymbolFilter"));
        AutomationProperties.SetName(SectionsList, Loc.Get("Disasm_Sections"));
        AutomationProperties.SetName(SymbolsList, Loc.Get("Disasm_Symbols"));
        AutomationProperties.SetName(MethodTree, Loc.Get("Disasm_Methods"));
        DotNetBar.Message = Loc.Get("Disasm_DotNetMessage");
        DotNetAction.Content = Loc.Get("Disasm_DotNetAction");
        DataBar.Message = Loc.Get("Disasm_MostlyData");
        foreach ((string id, string key) in new[]
        {
            ("cursor", "Disasm_StartCursor"), ("selection", "Disasm_StartSelection"), ("offset", "Disasm_StartOffset"),
            ("address", "Disasm_StartAddress"), ("entry", "Disasm_StartEntry"),
        })
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), Tag = id };
            AutomationProperties.SetAutomationId(item, "Disasm_Start_" + id);
            item.Click += (_, _) => _actions.StartFrom(id);
            StartMenu.Items.Add(item);
        }

        foreach (DisasmSyntax syntax in Enum.GetValues<DisasmSyntax>())
        {
            SyntaxBox.Items.Add(new ComboBoxItem { Content = Loc.Get("SetOpt_disassembly_syntax_" + DisasmSettings.SyntaxValue(syntax)), Tag = syntax });
        }

        EndianBox.Items.Add(new ComboBoxItem { Content = "LE", Tag = false });
        EndianBox.Items.Add(new ComboBoxItem { Content = "BE", Tag = true });
        BuildOptions();
    }

    private static void SetTip(FrameworkElement element, string key)
    {
        string text = Loc.Get(key);
        ToolTipService.SetToolTip(element, text);
        AutomationProperties.SetName(element, text);
    }

    // ---- ツールバー ----

    private const string MsilFamily = "MSIL (.NET)";

    private void UpdateToolbar()
    {
        _updating = true;
        try
        {
            DisasmArchitecture arch = _vm.Architecture;
            ArchBox.Items.Clear();
            foreach (string family in DisasmArchitectures.Families())
            {
                ArchBox.Items.Add(family);
            }

            if (_vm.Image.IsDotNet || _vm.IsMsil)
            {
                ArchBox.Items.Add(MsilFamily);
            }

            ArchBox.SelectedItem = _vm.IsMsil ? MsilFamily : arch.Family;
            ModeBox.Items.Clear();
            if (!_vm.IsMsil)
            {
                foreach (DisasmArchitecture mode in DisasmArchitectures.ModesOf(arch.Family))
                {
                    ModeBox.Items.Add(new ComboBoxItem
                    {
                        Content = mode.Mode.Length == 0 ? mode.Family : mode.Mode,
                        Tag = mode.Id,
                        // プラグインの逆アセンブラはツールチップにプラグイン名を出す (08 の AUTO-31 の仕様 2)。
                    });
                    if (mode.PluginName is { } plugin && ModeBox.Items[^1] is ComboBoxItem pi)
                    {
                        ToolTipService.SetToolTip(pi, Loc.Format("Disasm_PluginArchitecture", plugin));
                    }
                }

                ModeBox.SelectedItem = ModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == arch.Id);
            }

            ModeBox.Visibility = _vm.IsMsil ? Visibility.Collapsed : Visibility.Visible;
            EndianBox.IsEnabled = arch.HasEndian && !_vm.IsMsil;
            bool big = _vm.BigEndian ?? (_vm.Image.IsExecutable && arch.HasEndian ? _vm.Image.BigEndian : arch.BigEndianDefault);
            EndianBox.SelectedIndex = big ? 1 : 0;
            SliceBox.Items.Clear();
            foreach (ImageSlice slice in _vm.Image.Slices)
            {
                SliceBox.Items.Add(new ComboBoxItem { Content = slice.Name, Tag = slice });
            }

            SliceBox.Visibility = _vm.Image.Slices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SliceBox.SelectedIndex = _vm.Slice is { } chosen ? _vm.Image.Slices.ToList().FindIndex(s => s.Offset == chosen.Offset) : _vm.Image.Slices.Count > 0 ? 0 : -1;
            BaseBox.Text = _vm.BaseAddress is { } b ? "0x" + b.ToString("X", CultureInfo.InvariantCulture) : string.Empty;
            SyntaxBox.SelectedIndex = (int)_vm.Format.Syntax;
            SyntaxBox.IsEnabled = arch.IsX86 && !_vm.IsMsil;
            FollowToggle.IsChecked = _vm.FollowCursor;
            UpdateOptions();
        }
        finally
        {
            _updating = false;
        }
    }

    private void ArchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ArchBox.SelectedItem is not string family)
        {
            return;
        }

        if (family == MsilFamily)
        {
            _vm.SetArchitecture(DisasmArchitectures.Msil);
            _ = ShowMethodsAsync();
        }
        else
        {
            _vm.SetArchitecture(DisasmArchitectures.ModesOf(family)[0].Id);
        }

        UpdateToolbar();
        UpdateLists();
    }

    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && ModeBox.SelectedItem is ComboBoxItem { Tag: string id })
        {
            _vm.SetArchitecture(id);
            UpdateToolbar();
        }
    }

    private void EndianBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && EndianBox.SelectedItem is ComboBoxItem { Tag: bool big })
        {
            _vm.SetEndian(big);
        }
    }

    private void SliceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && SliceBox.SelectedItem is ComboBoxItem { Tag: ImageSlice slice })
        {
            _ = _vm.SetSliceAsync(slice);
        }
    }

    private void SyntaxBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating && SyntaxBox.SelectedItem is ComboBoxItem { Tag: DisasmSyntax syntax })
        {
            _actions.SetFormat(_vm.Format with { Syntax = syntax });
        }
    }

    private void FollowToggle_Click(object sender, RoutedEventArgs e) => _vm.FollowCursor = FollowToggle.IsChecked == true;

    /// <summary>ベースアドレスの入力 (空欄は自動。ANA-23 の仕様 3)。</summary>
    private void ApplyBase()
    {
        string text = BaseBox.Text.Trim();
        if (text.Length == 0)
        {
            _vm.SetBaseAddress(null);
            return;
        }

        string digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text.TrimEnd('h', 'H');
        if (ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
        {
            _vm.SetBaseAddress(value);
        }
    }

    private void BaseBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ApplyBase();
            e.Handled = true;
        }
    }

    private void BaseBox_LostFocus(object sender, RoutedEventArgs e) => ApplyBase();

    // ---- 表示オプション (ANA-26) ----

    private ComboBox? _hexStyle;
    private CheckBox? _upperHex;
    private CheckBox? _signed;
    private CheckBox? _upperMnemonic;
    private CheckBox? _armAlias;
    private CheckBox? _showBytes;
    private NumberBox? _maxBytes;
    private ComboBox? _addressColumn;
    private CheckBox? _branchLines;
    private CheckBox? _comments;

    private void BuildOptions()
    {
        _hexStyle = Combo("Disasm_Opt_HexStyle", "Set_disassembly_hexStyle", ["prefix", "suffix"], "SetOpt_disassembly_hexStyle_");
        _upperHex = Check("Disasm_Opt_UppercaseHex", "Set_disassembly_uppercaseHex");
        _signed = Check("Disasm_Opt_SignedImmediates", "Set_disassembly_signedImmediates");
        _upperMnemonic = Check("Disasm_Opt_UppercaseMnemonics", "Set_disassembly_uppercaseMnemonics");
        _armAlias = Check("Disasm_Opt_ArmAliases", "Set_disassembly_armRegisterAliases");
        _showBytes = Check("Disasm_Opt_ShowBytes", "Set_disassembly_showBytes");
        _maxBytes = new NumberBox
        {
            Header = Loc.Get("Set_disassembly_maxBytes"),
            Minimum = DisasmFormat.MinBytes,
            Maximum = DisasmFormat.MaxBytesLimit,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        AutomationProperties.SetAutomationId(_maxBytes, "Disasm_Opt_MaxBytes");
        _maxBytes.ValueChanged += (_, _) => OptionChanged();
        OptionsPanel.Children.Add(_maxBytes);
        _addressColumn = Combo("Disasm_Opt_AddressColumn", "Set_disassembly_addressColumn", ["virtual", "offset", "both"], "SetOpt_disassembly_addressColumn_");
        _branchLines = Check("Disasm_Opt_BranchLines", "Set_disassembly_branchLines");
        _comments = Check("Disasm_Opt_Comments", "Set_disassembly_comments");
    }

    private CheckBox Check(string id, string key)
    {
        var box = new CheckBox { Content = Loc.Get(key) };
        AutomationProperties.SetAutomationId(box, id);
        box.Click += (_, _) => OptionChanged();
        OptionsPanel.Children.Add(box);
        return box;
    }

    private ComboBox Combo(string id, string key, string[] values, string optionPrefix)
    {
        var box = new ComboBox { Header = Loc.Get(key), MinWidth = 200 };
        foreach (string v in values)
        {
            box.Items.Add(new ComboBoxItem { Content = Loc.Get(optionPrefix + v), Tag = v });
        }

        AutomationProperties.SetAutomationId(box, id);
        box.SelectionChanged += (_, _) => OptionChanged();
        OptionsPanel.Children.Add(box);
        return box;
    }

    private void UpdateOptions()
    {
        DisasmFormat f = _vm.Format;
        _hexStyle!.SelectedIndex = (int)f.HexStyle;
        _upperHex!.IsChecked = f.UppercaseHex;
        _signed!.IsChecked = f.SignedImmediates;
        _upperMnemonic!.IsChecked = f.UppercaseMnemonic;
        _armAlias!.IsChecked = f.ArmRegisterAlias;
        _showBytes!.IsChecked = f.ShowBytes;
        _maxBytes!.Value = f.MaxBytes;
        _addressColumn!.SelectedIndex = (int)f.AddressColumn;
        _branchLines!.IsChecked = f.ShowBranchLines;
        _comments!.IsChecked = f.ShowComments;
    }

    private void OptionChanged()
    {
        if (_updating || _hexStyle is null || _comments is null)
        {
            return;
        }

        _actions.SetFormat(_vm.Format with
        {
            HexStyle = _hexStyle.SelectedIndex == 1 ? HexStyle.Suffix : HexStyle.Prefix,
            UppercaseHex = _upperHex!.IsChecked == true,
            SignedImmediates = _signed!.IsChecked == true,
            UppercaseMnemonic = _upperMnemonic!.IsChecked == true,
            ArmRegisterAlias = _armAlias!.IsChecked == true,
            ShowBytes = _showBytes!.IsChecked == true,
            MaxBytes = double.IsNaN(_maxBytes!.Value) ? 8 : (int)Math.Clamp(_maxBytes.Value, DisasmFormat.MinBytes, DisasmFormat.MaxBytesLimit),
            AddressColumn = (AddressColumn)Math.Max(0, _addressColumn!.SelectedIndex),
            ShowBranchLines = _branchLines!.IsChecked == true,
            ShowComments = _comments.IsChecked == true,
        });
    }

    /// <summary>設定が変わった (設定画面・別のウィンドウ)。</summary>
    public void FormatChanged() => UpdateToolbar();

    // ---- 通知 ----

    private void UpdateBars()
    {
        EngineBar.IsOpen = _vm.EngineError is not null && !_vm.IsMsil;
        EngineBar.Message = _vm.EngineError is { } error ? Loc.Format("Disasm_EngineError", error) : string.Empty;
        DataBar.IsOpen = _vm.MostlyData && !_vm.IsMsil;
        IReadOnlyList<ImageIssue> issues = _vm.Image.Issues;
        HeaderBar.IsOpen = issues.Count > 0;
        HeaderBar.Message = issues.Count > 0
            ? Loc.Format("Disasm_HeaderIssue", string.Join(", ", issues.Take(3).Select(i => Loc.Get("Disasm_HeaderIssue_" + i.Code) + (i.Detail.Length > 0 ? " (" + i.Detail + ")" : string.Empty))))
            : string.Empty;
        DotNetBar.IsOpen = _vm.Image.IsDotNet && !_vm.IsMsil;
    }

    private void DotNetAction_Click(object sender, RoutedEventArgs e) => _ = SwitchToMsilAsync();

    /// <summary>アーキテクチャを「MSIL (.NET)」にして型とメソッドの木を出す (ANA-27)。</summary>
    public async Task SwitchToMsilAsync()
    {
        _vm.SetArchitecture(DisasmArchitectures.Msil);
        UpdateToolbar();
        UpdateLists();
        await ShowMethodsAsync();
    }

    // ---- 一覧 (ANA-24 の仕様 3・7、ANA-27 の仕様 1) ----

    private void UpdateLists()
    {
        bool msil = _vm.IsMsil;
        LeftTabs.Visibility = msil ? Visibility.Collapsed : Visibility.Visible;
        MethodTree.Visibility = msil ? Visibility.Visible : Visibility.Collapsed;
        bool symbols = !msil && LeftTabs.SelectedItem == SymbolsTab;
        SectionsList.Visibility = !msil && !symbols ? Visibility.Visible : Visibility.Collapsed;
        SymbolsList.Visibility = symbols ? Visibility.Visible : Visibility.Collapsed;
        SymbolFilter.Visibility = symbols ? Visibility.Visible : Visibility.Collapsed;
        SectionsList.ItemsSource = _vm.Sections;
        RefreshSymbols();
        LeftPane.Visibility = msil || _vm.Image.IsExecutable ? Visibility.Visible : Visibility.Collapsed;
        ApplyNarrowLayout(ActualWidth);
    }

    private void LeftTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => UpdateLists();

    private CancellationTokenSource? _filter;

    private void SymbolFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshSymbols();

    /// <summary>シンボルの絞り込み (100 万件を超えてもよいよう、バックグラウンドで行う)。</summary>
    private void RefreshSymbols()
    {
        _filter?.Cancel();
        var cts = new CancellationTokenSource();
        _filter = cts;
        string text = SymbolFilter.Text;
        _ = Task.Run(() => _vm.SymbolsMatching(text), cts.Token).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && !cts.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        SymbolsList.ItemsSource = t.Result;
                        SymbolsShown = t.Result;
                    }
                });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>今の絞り込みの結果 (テスト用)。</summary>
    internal IReadOnlyList<DisasmSymbolRow> SymbolsShown { get; private set; } = [];

    /// <summary>シンボルの一覧を絞り込む (テスト用の命令)。</summary>
    internal void FilterSymbols(string text)
    {
        LeftTabs.SelectedItem = SymbolsTab;
        SymbolFilter.Text = text;
        UpdateLists();
    }

    private void SectionsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DisasmSectionRow row)
        {
            _vm.GoToOffset(row.Section.FileOffset, activate: true);
        }
    }

    private void SymbolsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DisasmSymbolRow row)
        {
            ShowSymbol(row);
        }
    }

    internal void ShowSymbol(DisasmSymbolRow row)
    {
        if (_vm.Listing?.OffsetOf(row.Symbol.Address) is { } offset)
        {
            _vm.GoToOffset(offset, activate: true);
        }
    }

    private void ApplyNarrowLayout(double width)
    {
        bool narrow = width > 0 && width < 600;
        if (LeftPane.Visibility == Visibility.Collapsed)
        {
            LeftColumn.Width = new GridLength(0);
            TopRow.Height = new GridLength(0);
            Grid.SetRow(LeftPane, 1);
            Grid.SetColumn(LeftPane, 0);
            return;
        }

        LeftColumn.Width = narrow ? new GridLength(0) : new GridLength(220);
        TopRow.Height = narrow ? new GridLength(180) : new GridLength(0);
        Grid.SetRow(LeftPane, narrow ? 0 : 1);
        Grid.SetColumn(LeftPane, 0);
        Grid.SetColumnSpan(LeftPane, narrow ? 2 : 1);
        Grid.SetRow(Listing, 1);
        Grid.SetColumn(Listing, narrow ? 0 : 1);
        Grid.SetColumnSpan(Listing, narrow ? 2 : 1);
    }

    // ---- 型とメソッドの木 (ANA-27) ----

    private async Task ShowMethodsAsync()
    {
        MsilModule? module = await _vm.OpenMsilAsync();
        MethodTree.RootNodes.Clear();
        if (module is null)
        {
            return;
        }

        // 名前空間 → 型 → メソッド。子は開いたときに作る (10 万メソッドでも木を作るのは見える分だけ)。
        foreach (IGrouping<string, MsilType> ns in module.Types.GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var node = new TreeViewNode { Content = ns.Key.Length == 0 ? "-" : ns.Key, HasUnrealizedChildren = true };
            _nodeTypes[node] = [.. ns];
            MethodTree.RootNodes.Add(node);
        }

        if (module.Issues.Count > 0)
        {
            HeaderBar.IsOpen = true;
            HeaderBar.Message = Loc.Get("Disasm_MetadataIssue");
        }
    }

    private readonly Dictionary<TreeViewNode, IReadOnlyList<MsilType>> _nodeTypes = [];

    private void MethodTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        TreeViewNode node = args.Node;
        if (!node.HasUnrealizedChildren)
        {
            return;
        }

        node.HasUnrealizedChildren = false;
        if (_nodeTypes.TryGetValue(node, out IReadOnlyList<MsilType>? types))
        {
            foreach (MsilType type in types)
            {
                var child = new TreeViewNode { Content = type.Name, HasUnrealizedChildren = type.Methods.Count > 0 };
                _nodeMethods[child] = type.Methods;
                node.Children.Add(child);
            }
        }
        else if (_nodeMethods.TryGetValue(node, out IReadOnlyList<MsilMethod>? methods))
        {
            foreach (MsilMethod m in methods)
            {
                node.Children.Add(new TreeViewNode { Content = new MethodItem(m) });
            }
        }
    }

    private readonly Dictionary<TreeViewNode, IReadOnlyList<MsilMethod>> _nodeMethods = [];

    private sealed record MethodItem(MsilMethod Method)
    {
        public override string ToString() => Method.DisplayName;
    }

    private void MethodTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: MethodItem item })
        {
            _ = _vm.SelectMethodAsync(item.Method);
        }
    }

    // ---- 行の右クリックメニュー (ANA-23 の仕様 8、ANA-25、ANA-29) ----

    private void ShowRowMenu(DisasmRow row, Windows.Foundation.Point? position)
    {
        _rowMenu = new MenuFlyout();
        MenuFlyoutItem Item(string id, string key, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, "Disasm_Menu_" + id);
            item.Click += (_, _) => action();
            _rowMenu.Items.Add(item);
            return item;
        }

        bool hasTarget = row.TargetOffset is not null || row.Native?.Instruction?.Target is not null || row.Msil?.BranchTarget is not null
            || row.Msil?.MethodToken is not null;
        Item("GoToTarget", "Cmd_disassembly_goToTarget", () => _actions.GoToTarget(row), hasTarget);
        if (!_vm.IsMsil)
        {
            Item("FindReferences", "Cmd_disassembly_findReferences", () => _actions.FindReferences(row));
        }

        // インプレースアセンブルは x86 / x64 だけ (ANA-29 の仕様 6。ほかのアーキテクチャでは項目を出さない)。
        if (_vm.Architecture.CanAssemble && !_vm.IsMsil && row.Kind == ListingLineKind.Instruction)
        {
            Item("Assemble", "Disasm_MenuAssemble", () => _actions.Assemble(row), _vm.Document?.Document.IsReadOnly == false);
        }

        _rowMenu.Items.Add(new MenuFlyoutSeparator());
        Item("Copy", "Disasm_MenuCopy", () => _actions.Copy([row]));
        Item("Export", "Cmd_disassembly_export", () => _actions.Export());
        if (position is { } p)
        {
            _rowMenu.ShowAt(Listing, p);
        }
        else
        {
            _rowMenu.ShowAt(Listing);
        }
    }

    /// <summary>行の右クリックメニューの項目 (テスト用)。</summary>
    internal IReadOnlyList<(string Id, bool Enabled)> RowMenuItems(DisasmRow row)
    {
        ShowRowMenu(row, new Windows.Foundation.Point(10, 10));
        var items = _rowMenu!.Items.OfType<MenuFlyoutItem>().Select(i => (AutomationProperties.GetAutomationId(i), i.IsEnabled)).ToList();
        _rowMenu.Hide();
        return items;
    }

    // ---- 命令パターン検索 (ANA-28) ----

    private void FindButton_Click(object sender, RoutedEventArgs e) => ShowPatternSearch();

    public void ShowPatternSearch()
    {
        SearchRow.Visibility = Visibility.Visible;
        PatternBox.Focus(FocusState.Keyboard);
        PatternBox.SelectAll();
    }

    private void PatternClose_Click(object sender, RoutedEventArgs e) => ClosePatternSearch();

    private void ClosePatternSearch()
    {
        SearchRow.Visibility = Visibility.Collapsed;
        Listing.Focus(FocusState.Keyboard);
    }

    private void PatternFindAll_Click(object sender, RoutedEventArgs e) => _ = RunPatternAsync(0);

    private async Task RunPatternAsync(int direction)
    {
        string text = PatternBox.Text;
        if (text.Trim().Length == 0)
        {
            return;
        }

        _patternHistory.Remove(text);
        _patternHistory.Insert(0, text);
        _historyIndex = -1;
        string? error = await _actions.FindPattern(text, RegexCheck.IsChecked == true, direction);
        PatternError.Text = error ?? string.Empty;
        PatternBox.BorderBrush = error is null ? null : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    }

    /// <summary>
    /// 検索欄のキー (検索バーの有効範囲と同じ。00-overview 8.4): Enter / Shift+Enter (次 / 前)、Alt+Enter (すべて)、↑ / ↓ (履歴)、Esc (閉じる)。
    /// </summary>
    private void PatternBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                _ = RunPatternAsync(alt ? 0 : shift ? -1 : 1);
                break;
            case VirtualKey.Escape:
                ClosePatternSearch();
                break;
            case VirtualKey.Up when _patternHistory.Count > 0:
                _historyIndex = Math.Min(_patternHistory.Count - 1, _historyIndex + 1);
                PatternBox.Text = _patternHistory[_historyIndex];
                break;
            case VirtualKey.Down when _historyIndex > 0:
                _historyIndex--;
                PatternBox.Text = _patternHistory[_historyIndex];
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>検索欄に入力して実行する (テスト用の命令)。</summary>
    internal async Task<string?> FindPatternForTestAsync(string text, bool regex, int direction)
    {
        SearchRow.Visibility = Visibility.Visible;
        PatternBox.Text = text;
        RegexCheck.IsChecked = regex;
        await RunPatternAsync(direction);
        return PatternError.Text.Length == 0 ? null : PatternError.Text;
    }

    /// <summary>ツールバーの状態 (テスト用)。</summary>
    internal (string Architecture, string? Mode, int Endian, string Base, int Syntax, bool Follow, bool SliceVisible, IReadOnlyList<string> Families) ToolbarState =>
        (ArchBox.SelectedItem as string ?? string.Empty, (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string, EndianBox.SelectedIndex, BaseBox.Text,
            SyntaxBox.SelectedIndex, FollowToggle.IsChecked == true, SliceBox.Visibility == Visibility.Visible, [.. ArchBox.Items.OfType<string>()]);

    /// <summary>通知の状態 (テスト用)。</summary>
    internal (bool Engine, bool Data, bool Header, bool DotNet) BarsState => (EngineBar.IsOpen, DataBar.IsOpen, HeaderBar.IsOpen, DotNetBar.IsOpen);

    /// <summary>ツールバーの要素 (テスト用の書字方向の確認)。</summary>
    internal FrameworkElement ToolbarElement => Toolbar;

    /// <summary>MSIL のメソッドを選ぶ (テスト用の命令)。</summary>
    internal async Task SelectMethodForTestAsync(string typeName, string name)
    {
        if (await _vm.OpenMsilAsync() is { } module && module.FindMethod(typeName, name) is { } method)
        {
            await _vm.SelectMethodAsync(method);
        }
    }
}
