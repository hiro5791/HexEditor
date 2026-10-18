using System.Globalization;
using System.Text;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Disassembly;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Disassembly.Msil;
using HexEditor.Core.Engine;
using HexEditor.Core.Panels;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 逆アセンブル (06 の ANA-23〜ANA-30) の組み込み: 逆アセンブルパネル (既定は右)、コマンド、Hex ビューとの連動 (選択・カーソルに追従・
/// 現在の命令の枠線)、分岐先へ移動 (F12・Enter・ジャンプ履歴)、相互参照と命令パターン検索 (共通の結果一覧)、インプレースアセンブル、
/// 仮想アドレスへ移動、デマングル。状態 (<see cref="DisassemblyViewModel"/>) はウィンドウごとに 1 つ。
/// </summary>
public sealed partial class MainWindow
{
    public const string DisassemblyPanelId = "disassembly";

    private DisassemblyViewModel? _disasmVm;
    private DisassemblyPanelActions? _disasmActions;
    private DisassemblyPanel? _disasmPanel;
    private EditorState? _disasmEditor;
    private bool _disasmSelecting;

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度)。</summary>
    public static void RegisterDisassemblyPanel()
    {
        if (PanelRegistry.Find(DisassemblyPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(DisassemblyPanelId, "Panel_Disassembly_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateDisassemblyPanel()));
        }

        // インスペクタの 1 命令の逆アセンブル (INSP-16) は、逆アセンブルの表示設定 (ANA-26) の構文・数値の書式を使う。
        InspectorViewModel.DisasmFormatSource = CurrentDisasmFormat;
    }

    /// <summary>設定の表示設定 (ANA-26。アプリ全体で共有する)。</summary>
    internal static DisasmFormat CurrentDisasmFormat() => DisasmSettings.Read(App.Settings.GetString, App.Settings.GetBool, App.Settings.GetInt);

    internal DisassemblyViewModel DisasmVm => _disasmVm ??= CreateDisasmVm();

    public bool IsDisassemblyPanelOpen => IsPanelShown(DisassemblyPanelId);

    private DisassemblyViewModel CreateDisasmVm()
    {
        var vm = new DisassemblyViewModel(CurrentDisasmFormat());
        vm.RowActivated += (_, row) => SelectRowInEditor(row, recordJump: false);
        vm.RowsChanged += (_, _) => SelectedView()?.RefreshHighlights();
        vm.Attach(Vm.Selected);
        AttachDisasmEditor(Vm.Selected?.Editor);
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected) && !_closingConfirmed)
            {
                vm.Attach(Vm.Selected);
                AttachDisasmEditor(Vm.Selected?.Editor);
            }
        };
        Action<IReadOnlyCollection<string>> settingsChanged = keys =>
        {
            if (keys.Any(k => k.StartsWith("disassembly.", StringComparison.Ordinal)))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closingConfirmed)
                    {
                        vm.SetFormat(CurrentDisasmFormat());
                        _disasmPanel?.FormatChanged();
                    }
                });
            }
        };
        App.Settings.Changed += settingsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                App.Settings.Changed -= settingsChanged;
                AttachDisasmEditor(null);
                vm.Detach();
            }
        };
        return vm;
    }

    private DisassemblyPanelActions DisasmActions => _disasmActions ??= new DisassemblyPanelActions
    {
        GoToTarget = row => GoToDisasmTarget(row),
        FindReferences = row => _ = FindDisasmReferencesAsync(row),
        Assemble = row => ShowAssembleFlyout(row),
        Copy = CopyDisasmRows,
        Export = () => _ = ExportDisassemblyAsync(),
        StartFrom = StartDisassemblyFrom,
        FindPattern = FindDisasmPatternAsync,
        ToolTip = DisasmToolTip,
        SetFormat = SaveDisasmFormat,
    };

    private DisassemblyPanel CreateDisassemblyPanel()
    {
        var panel = new DisassemblyPanel(DisasmVm, DisasmActions);
        AutomationProperties.SetAutomationId(panel, "DisassemblyPanel");
        _disasmPanel = panel;
        return panel;
    }

    /// <summary>カーソルに追従する (ANA-23 の仕様 6): Hex ビューのカーソル (選択範囲があれば先頭) を含む命令の行を選ぶ。</summary>
    private void AttachDisasmEditor(EditorState? editor)
    {
        if (_disasmEditor is not null)
        {
            _disasmEditor.Changed -= DisasmEditor_Changed;
        }

        _disasmEditor = editor;
        if (editor is not null)
        {
            editor.Changed += DisasmEditor_Changed;
        }
    }

    private void DisasmEditor_Changed(object? sender, EventArgs e)
    {
        if (_disasmSelecting || _disasmVm is not { FollowCursor: true } vm || !IsDisassemblyPanelOpen || sender is not EditorState editor)
        {
            return;
        }

        long at = editor.HasSelection ? editor.SelectionStart : editor.Cursor;
        if (vm.SelectedRow is { } row && row.Offset <= at && at < row.Offset + Math.Max(1, row.Length))
        {
            return;
        }

        vm.GoToOffset(at);
    }

    /// <summary>リスティングの行の命令のバイト列を Hex ビューで選ぶ (ANA-23 の仕様 6)。</summary>
    private void SelectRowInEditor(DisasmRow row, bool recordJump)
    {
        if (Editor is not { } editor || row.Length <= 0)
        {
            return;
        }

        _disasmSelecting = true;
        try
        {
            if (recordJump)
            {
                editor.RecordJump();
            }

            editor.Select(row.Offset, row.Length, cursorAtStart: true);
        }
        finally
        {
            _disasmSelecting = false;
        }

        SelectedView()?.RefreshHighlights();
    }

    // ---- Hex ビューの強調 (現在の命令の範囲。VIEW-17 の層 3、ANA-23 の仕様 6) ----

    private static readonly double[] DisasmDash = [2, 2];

    private void AttachDisassemblyHighlights(HexView view) =>
        view.SetHighlightSource("disassembly", (start, end) => DisassemblyHighlights(view, start, end));

    private IEnumerable<HexHighlight> DisassemblyHighlights(HexView view, long start, long end)
    {
        if (_disasmVm is not { } vm || !IsDisassemblyPanelOpen || view.Editor != Vm.Selected?.Editor || vm.SelectedRow is not { } row
            || row.Length <= 0 || row.Offset >= end || row.Offset + row.Length <= start)
        {
            yield break;
        }

        yield return new HexHighlight(row.Offset, row.Length, CellLayer.Focus, null,
            AnnotationBrushes.Get("InspectorTargetBorderBrush", view, view.IsHighContrast), DisasmDash, "disassembly", Thickness: 2);
    }

    /// <summary>Hex ビューの右クリックメニュー: 選択範囲の「逆アセンブル」(ANA-23) と「デマングル」(ANA-30)。</summary>
    private void ExtendHexViewDisassemblyMenu(MenuFlyout menu)
    {
        const string DisasmId = "HexViewMenu_Disassemble";
        const string DemangleId = "HexViewMenu_Demangle";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == DisasmId))
        {
            var disasm = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_Disassemble") };
            AutomationProperties.SetAutomationId(disasm, DisasmId);
            disasm.Click += (_, _) => StartDisassemblyFrom("selection");
            var demangle = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_Demangle") };
            AutomationProperties.SetAutomationId(demangle, DemangleId);
            demangle.Click += (_, _) => OpenDemangle(fromSelection: true);
            menu.Items.Add(disasm);
            menu.Items.Add(demangle);
        }

        bool selection = Editor?.HasSelection == true;
        foreach (MenuFlyoutItem item in menu.Items.OfType<MenuFlyoutItem>())
        {
            string id = AutomationProperties.GetAutomationId(item);
            if (id is DisasmId or DemangleId)
            {
                item.Visibility = selection ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ---- コマンド ----

    private void RegisterDisassemblyCommands()
    {
        _inspectorVm.GoToBranchTarget = GoToInspectorBranch;
        Commands.Register("view.panel.disassembly", () => TogglePanel(DisassemblyPanelId), () => Toggle(IsDisassemblyPanelOpen));
        Commands.Register("analysis.disassemble", () => StartDisassemblyFrom(Editor?.HasSelection == true ? "selection" : "cursor"), NeedsDocument);
        Commands.Register("analysis.disassembleAtCursor", () => StartDisassemblyFrom("cursor"), NeedsDocument);
        Commands.Register("analysis.goToVirtualAddress", GoToVirtualAddressAsync,
            () => NeedsDocument(_ => DisasmVm.Image.IsExecutable ? null : Loc.Get("Disasm_NotExecutable")));
        Commands.Register("analysis.goToEntryPoint", GoToEntryPointAsync,
            () => NeedsDocument(_ => DisasmVm.Image.EntryPoint is null ? Loc.Get("Disasm_NoEntryPoint") : null));
        Commands.Register("analysis.disassembleDotNet", DisassembleDotNetAsync,
            () => NeedsDocument(_ => DisasmVm.Image.IsDotNet ? null : Loc.Get("Disasm_NotDotNet")));
        Commands.Register("analysis.demangle", () => OpenDemangle(fromSelection: Editor?.HasSelection == true));
        Commands.Register("disassembly.goToTarget", () => GoToDisasmTargetFromCommand(), NeedsDocument);
        Commands.Register("disassembly.findReferences", () => DisasmTargetRow() is { } row ? FindDisasmReferencesAsync(row) : Task.CompletedTask,
            () => NeedsDocument(_ => DisasmVm.IsMsil ? Loc.Get("Disasm_NotForMsil") : null));
        Commands.Register("disassembly.findPattern", () =>
        {
            ShowPanel(DisassemblyPanelId);
            _disasmPanel?.ShowPatternSearch();
        }, () => NeedsDocument(_ => DisasmVm.IsMsil ? Loc.Get("Disasm_NotForMsil") : null));

        // インプレースアセンブルは x86 / x64 だけ (ANA-29 の仕様 6)。読み取り専用では使えない (「エラー」)。
        Commands.Register("disassembly.assemble", () =>
        {
            if (DisasmTargetRow() is { } row)
            {
                ShowAssembleFlyout(row);
            }
        }, () => NeedsEditable(_ => DisasmVm.Architecture.CanAssemble && !DisasmVm.IsMsil ? null : Loc.Get("Disasm_AssembleX86Only")));
        Commands.Register("disassembly.copy", () =>
        {
            if (DisasmVm.SelectedRow is { } row)
            {
                CopyDisasmRows([row]);
            }
        }, NeedsDocument);
        Commands.Register("disassembly.export", ExportDisassemblyAsync, NeedsDocument);
    }

    /// <summary>逆アセンブルの開始位置 (ANA-23 の仕様 2): カーソル位置・選択範囲の先頭・指定したオフセット・仮想アドレス・エントリポイント。</summary>
    private void StartDisassemblyFrom(string where)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        ShowPanel(DisassemblyPanelId, focus: false);
        switch (where)
        {
            case "selection":
                DisasmVm.GoToOffset(editor.HasSelection ? editor.SelectionStart : editor.Cursor, activate: true);
                break;
            case "offset":
                _ = AskDisassemblyOffsetAsync();
                return;
            case "address":
                _ = GoToVirtualAddressAsync();
                return;
            case "entry":
                _ = GoToEntryPointAsync();
                return;
            default:
                DisasmVm.GoToOffset(editor.Cursor);
                break;
        }

        _disasmPanel?.FocusContent();
    }

    /// <summary>「指定したオフセット」: 移動のダイアログ (Ctrl+G) と同じ入力式でオフセットを入れる。</summary>
    private async Task AskDisassemblyOffsetAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        TextBox box = DialogParts.Field("Disasm_OffsetBox", Loc.Get("Disasm_StartOffset"));
        TextBlock info = DialogParts.Caption("Disasm_OffsetInfo");
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Disasm_StartOffset"),
            Content = new StackPanel { Spacing = 8, Children = { box, info } },
            PrimaryButtonText = Loc.Get("Disasm_Go"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        var context = new EditorExpressionContext(editor);
        void Validate()
        {
            bool ok = DialogParts.TryEvaluate(box.Text, context, out long value, out Core.Expressions.ExpressionException? error) && value >= 0 && value < editor.Document.Length;
            info.Text = ok ? DialogParts.Interpretation(value) : error is null ? Loc.Get("Disasm_OutOfDocument") : error.Message;
            dialog.IsPrimaryButtonEnabled = ok;
        }

        box.TextChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary
            && DialogParts.TryEvaluate(box.Text, context, out long offset, out _))
        {
            DisasmVm.GoToOffset(offset, activate: true);
            _disasmPanel?.FocusContent();
        }
    }

    /// <summary>
    /// 「仮想アドレスへ移動」(ANA-24 の仕様 5): 入力式で仮想アドレスを入れ、入力欄の横に対応するオフセットとセクション名を出す。
    /// どのセクションにも含まれないアドレスは確定できない (「エラー」)。
    /// </summary>
    private async Task GoToVirtualAddressAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        await DisasmVm.WhenImageReadyAsync();
        ShowPanel(DisassemblyPanelId, focus: false);
        TextBox box = DialogParts.Field("Disasm_AddressBox", Loc.Get("Disasm_VirtualAddress"));
        TextBlock info = DialogParts.Caption("Disasm_AddressInfo", monospace: true);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Cmd_analysis_goToVirtualAddress"),
            Content = new StackPanel { Spacing = 8, Children = { box, info } },
            PrimaryButtonText = Loc.Get("Disasm_Go"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        long? target = null;
        box.TextChanged += (_, _) =>
        {
            (target, info.Text) = ResolveVirtualAddress(box.Text, editor);
            dialog.IsPrimaryButtonEnabled = target is not null;
        };
        dialog.IsPrimaryButtonEnabled = false;
        _pendingAddressDialog = (dialog, box, info);
        try
        {
            if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && target is { } offset)
            {
                _disasmSelecting = true;
                editor.GoTo(offset);
                _disasmSelecting = false;
                DisasmVm.GoToOffset(offset);
                FocusEditor();
            }
        }
        finally
        {
            _pendingAddressDialog = null;
        }
    }

    /// <summary>開いている「仮想アドレスへ移動」のダイアログ (テスト用の命令)。</summary>
    private (ContentDialog Dialog, TextBox Box, TextBlock Info)? _pendingAddressDialog;

    /// <summary>仮想アドレスの入力を解釈する。オフセット (なければ null) と、入力欄の横に出す文。</summary>
    internal (long? Offset, string Info) ResolveVirtualAddress(string text, EditorState editor)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, string.Empty);
        }

        if (!DialogParts.TryEvaluateWide(text, new EditorExpressionContext(editor), out Int128 value, out Core.Expressions.ExpressionException? error)
            || value < 0 || value > ulong.MaxValue)
        {
            return (null, error?.Message ?? Loc.Get("Disasm_NoSection"));
        }

        ulong address = (ulong)value;
        DisassemblyListing? listing = DisasmVm.Listing;
        ImageSection? section = DisasmVm.Image.SectionOfAddress(address);
        if (listing?.OffsetOf(address) is { } offset && section is not null)
        {
            return (offset, Loc.Format("Disasm_AddressResolved", "0x" + offset.ToString("X", CultureInfo.InvariantCulture), section.Name));
        }

        return (null, Loc.Get("Disasm_NoSection"));
    }

    /// <summary>「エントリポイントへ移動」(ANA-24)。</summary>
    private async Task GoToEntryPointAsync()
    {
        await DisasmVm.WhenImageReadyAsync();
        ShowPanel(DisassemblyPanelId, focus: false);
        if (DisasmVm.Image.EntryPoint is { } entry && DisasmVm.Image.ToOffset(entry) is { } offset)
        {
            DisasmVm.GoToOffset(offset, activate: true);
            _disasmPanel?.FocusContent();
        }
        else
        {
            ShowStatusMessage(Loc.Get("Disasm_NoEntryPoint"));
        }
    }

    /// <summary>「.NET のメソッドを逆アセンブル」(ANA-27): アーキテクチャを MSIL にして型とメソッドの木を出す。</summary>
    private async Task DisassembleDotNetAsync()
    {
        await DisasmVm.WhenImageReadyAsync();
        ShowPanel(DisassemblyPanelId, focus: false);
        if (_disasmPanel is { } panel)
        {
            await panel.SwitchToMsilAsync();
        }
        else
        {
            DisasmVm.SetArchitecture(DisasmArchitectures.Msil);
        }
    }

    // ---- 分岐先へ移動 (ANA-25 の仕様 1〜4、INSP-16 の仕様 7) ----

    /// <summary>コマンドの対象の行 (パネルで選んだ行。なければカーソル位置を含む命令)。</summary>
    private DisasmRow? DisasmTargetRow()
    {
        if (DisasmVm.SelectedRow is { } row)
        {
            return row;
        }

        if (Editor is { } editor && IsDisassemblyPanelOpen)
        {
            DisasmVm.GoToOffset(editor.Cursor);
            return DisasmVm.SelectedRow;
        }

        return null;
    }

    private void GoToDisasmTargetFromCommand()
    {
        if (DisasmTargetRow() is { } row)
        {
            GoToDisasmTarget(row);
        }
    }

    /// <summary>
    /// F12 (Hex ビューにフォーカスがあり、テンプレートの適用がないとき。00-overview 8.7): 逆アセンブルパネルが開いていれば、カーソル位置を
    /// 含む命令の分岐先へ移動する。移動したら true。
    /// </summary>
    internal bool DisassemblyF12()
    {
        if (!IsDisassemblyPanelOpen || Editor is not { } editor)
        {
            return false;
        }

        DisasmVm.GoToOffset(editor.Cursor);
        if (DisasmVm.SelectedRow is { } row)
        {
            GoToDisasmTarget(row);
            return true;
        }

        return false;
    }

    /// <summary>F12 のときにフォーカスが逆アセンブルパネルにあるか。</summary>
    internal bool IsDisassemblyFocused(DependencyObject? focused)
    {
        for (DependencyObject? n = focused; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
        {
            if (n is DisassemblyPanel)
            {
                return true;
            }
        }

        return false;
    }

    private async void GoToDisasmTarget(DisasmRow row)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (row.Msil is { } il)
        {
            await GoToMsilTargetAsync(il, editor);
            return;
        }

        DisasmInstruction? instruction = row.Native?.Instruction;
        if (instruction?.Target is not { } target)
        {
            ShowStatusMessage(Loc.Get("Disasm_NoTarget"));
            return;
        }

        DisassemblyListing? listing = DisasmVm.Listing;
        if (listing is null)
        {
            return;
        }

        // インポートの項目 (IAT・PLT) は移動せずに名前を出す (仕様 2)。
        ulong imageAddress = listing.Options.BaseAddress is { } manual && listing.Image.IsExecutable ? target - manual + listing.Image.ImageBase : target;
        if (listing.Image.IsImport(imageAddress) && listing.Image.ImportAt(imageAddress) is { } name)
        {
            ShowStatusMessage(Loc.Format("Disasm_ExternalFunction", name));
            LastDisasmStatus = Loc.Format("Disasm_ExternalFunction", name);
            return;
        }

        if (row.TargetOffset is not { } offset)
        {
            string text = Loc.Format("Disasm_TargetOutside", listing.Format.Hex(target));
            ShowStatusMessage(text);
            LastDisasmStatus = text;
            return;
        }

        _disasmSelecting = true;
        try
        {
            // 移動はジャンプ履歴に記録する (仕様 3。Alt+← で戻る)。
            editor.RecordJump();
            long start = listing.InstructionStart(offset);
            DisasmVm.GoToOffset(start);
            if (DisasmVm.SelectedRow is { } selected)
            {
                editor.Select(selected.Offset, selected.Length, cursorAtStart: true);
            }
        }
        finally
        {
            _disasmSelecting = false;
        }

        SelectedView()?.RefreshHighlights();
    }

    /// <summary>最後にステータスバーに出した逆アセンブルの文 (テスト用)。</summary>
    internal string? LastDisasmStatus { get; private set; }

    /// <summary>MSIL の分岐先 (同じメソッドの行) と call の呼び出し先 (同じアセンブリのメソッド。ANA-27 の仕様 5)。</summary>
    private async Task GoToMsilTargetAsync(MsilLine line, EditorState editor)
    {
        if (line.BranchTarget is { } il && DisasmVm.MsilLineAt(il) is { } targetLine)
        {
            _disasmSelecting = true;
            editor.RecordJump();
            DisasmVm.GoToOffset(targetLine.Offset);
            editor.Select(targetLine.Offset, targetLine.Length, cursorAtStart: true);
            _disasmSelecting = false;
            return;
        }

        if (line.MethodToken is { } token && DisasmVm.Msil?.MethodByToken(token) is { } method)
        {
            editor.RecordJump();
            await DisasmVm.SelectMethodAsync(method);
            if (DisasmVm.SelectedRow is { } first)
            {
                _disasmSelecting = true;
                editor.Select(first.Offset, first.Length, cursorAtStart: true);
                _disasmSelecting = false;
            }

            return;
        }

        ShowStatusMessage(Loc.Get("Disasm_NoTarget"));
    }

    /// <summary>インスペクタの 1 命令の行の「分岐先へ移動」(INSP-16 の仕様 7。ジャンプ履歴に記録する)。</summary>
    internal void GoToInspectorBranch(long target)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (target < 0 || target >= editor.Document.Length)
        {
            ShowStatusMessage(Loc.Format("Disasm_TargetOutside", "0x" + target.ToString("X", CultureInfo.InvariantCulture)));
            return;
        }

        editor.GoTo(target);
        FocusEditor();
    }

    // ---- 相互参照 (ANA-25 の仕様 7・8) と命令パターン検索 (ANA-28) ----

    /// <summary>相互参照の結果 (同じドキュメントを編集するまで記憶する。仕様 8)。</summary>
    private readonly Dictionary<(DocumentSnapshot Snapshot, ulong Address), List<CrossReference>> _xrefCache = [];

    private async Task FindDisasmReferencesAsync(DisasmRow row)
    {
        if (Vm.Selected is not { } doc || DisasmVm.Listing is not { } listing || row.Native is not { } line)
        {
            return;
        }

        ulong address = line.Address;
        IReadOnlyList<(long Offset, long Length)> ranges = DisassemblySearch.DefaultRanges(listing) ?? SearchRangesForData(doc.Editor);
        DocumentSnapshot snapshot = doc.Document.Current;
        string header = Loc.Format("Disasm_ReferencesTo", listing.Format.Hex(address));
        var found = new List<CrossReference>();
        SearchResults results = DisassemblySearchResults.References(listing, snapshot, address, ranges, found,
            x => $"{listing.Format.Hex(x.Address)}  {x.Text}  ({x.Kind.ToString().ToLowerInvariant()})",
            () =>
            {
                lock (_xrefCache)
                {
                    return _xrefCache.TryGetValue((snapshot, address), out List<CrossReference>? cached) ? cached : null;
                }
            },
            refs =>
            {
                lock (_xrefCache)
                {
                    _xrefCache[(snapshot, address)] = [.. refs];
                }
            });
        LastDisasmReferences = found;
        ShowPanel(SearchResultsPanelId, focus: false);
        await SearchResults.RunAsync(doc.Editor, results, Loc.Get("Disasm_ReferencesKind"), header, Encoding.ASCII, new CancellationTokenSource());
    }

    /// <summary>直前の相互参照の結果 (テスト用)。</summary>
    internal IReadOnlyList<CrossReference> LastDisasmReferences { get; private set; } = [];

    /// <summary>実行形式でないドキュメントの対象範囲 (06 の 0.1: 選択範囲があれば選択範囲、なければ全体)。</summary>
    private static IReadOnlyList<(long Offset, long Length)> SearchRangesForData(EditorState editor) =>
        editor.HasSelection ? [(editor.SelectionStart, editor.SelectionLength)] : [(0, editor.Document.Length)];

    /// <summary>直前の命令パターン検索の結果 (テスト用)。</summary>
    internal IReadOnlyList<PatternMatch> LastDisasmPatternMatches { get; private set; } = [];

    /// <summary>
    /// 命令パターン検索 (ANA-28)。<paramref name="direction"/> は 1 (次)、-1 (前)、0 (すべて: 共通の結果一覧)。構文エラーなら理由を返す。
    /// 対象が 100 MB を超える場合は所要時間の目安を出す (「巨大ファイル」)。
    /// </summary>
    private async Task<string?> FindDisasmPatternAsync(string text, bool regex, int direction)
    {
        if (Vm.Selected is not { } doc || DisasmVm.Listing is not { } listing)
        {
            return null;
        }

        InstructionPattern pattern;
        try
        {
            pattern = InstructionPattern.Parse(text, regex);
        }
        catch (InstructionPatternException ex)
        {
            return Loc.Format("Disasm_PatternError", ex.Position + 1, Loc.Get("Disasm_PatternError_" + ex.Reason));
        }

        IReadOnlyList<(long Offset, long Length)> ranges = DisassemblySearch.DefaultRanges(listing) ?? SearchRangesForData(doc.Editor);
        long total = ranges.Sum(r => r.Length);
        if (total > DisassemblySearch.EstimateThreshold)
        {
            ShowStatusMessage(Loc.Format("Disasm_PatternEstimate", Math.Ceiling(DisassemblySearch.EstimatedTime(total).TotalSeconds)));
        }

        DocumentSnapshot snapshot = doc.Document.Current;
        if (direction != 0)
        {
            long from = DisasmVm.SelectedOffset ?? doc.Editor.Cursor;
            PatternMatch? hit = await Task.Run(() =>
            {
                PatternMatch? last = null;
                foreach (PatternMatch m in DisassemblySearch.FindPattern(listing, snapshot, pattern, regex, ranges, CancellationToken.None))
                {
                    if (direction > 0 && m.Offset > from)
                    {
                        return m;
                    }

                    if (direction < 0)
                    {
                        if (m.Offset >= from)
                        {
                            break;
                        }

                        last = m;
                    }
                }

                return last;
            });
            if (hit is null)
            {
                ShowStatusMessage(Loc.Get("Disasm_PatternNotFound"));
                return null;
            }

            DisasmVm.GoToOffset(hit.Offset, activate: true);
            return null;
        }

        var found = new List<PatternMatch>();
        SearchResults results = DisassemblySearchResults.Pattern(listing, snapshot, pattern, regex, ranges, found,
            m => $"{listing.Format.Hex(m.Address)}  {m.Text}", m => DispatcherQueue.TryEnqueue(() => DisasmVm.GoToOffset(m.Offset)));
        LastDisasmPatternMatches = found;
        ShowPanel(SearchResultsPanelId, focus: false);
        await SearchResults.RunAsync(doc.Editor, results, Loc.Get("Disasm_PatternKind"), text, Encoding.ASCII, new CancellationTokenSource());
        return null;
    }

    // ---- コピー・エクスポート (ANA-23 の仕様 8) ----

    private void CopyDisasmRows(IReadOnlyList<DisasmRow> rows)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(DisassemblyListingView.CopyText(rows));
        SystemClipboard.SetContent(package);
    }

    /// <summary>
    /// 「アセンブリとしてエクスポート...」: 範囲 (選択範囲、なければ選んだ行のセクション、なければ表示範囲) のリスティングをテキストファイルに保存する。
    /// </summary>
    private async Task ExportDisassemblyAsync()
    {
        if (Vm.Selected is not { } doc || DisasmVm.Listing is not { } listing)
        {
            return;
        }

        (long start, long end) = ExportRange(doc.Editor, listing);
        var picker = new FileSavePicker(WindowId) { SuggestedFileName = Path.GetFileNameWithoutExtension(doc.DisplayName) + ".asm", SettingsIdentifier = "HexEditor.DisassemblyExport" };
        picker.FileTypeChoices.Add(Loc.Get("Disasm_AssemblyFile"), [".asm", ".txt"]);
        string? path = TestExportPath ?? (await picker.PickSaveFileAsync())?.Path;
        if (path is null)
        {
            return;
        }

        await Task.Run(() => WriteListing(listing, start, end, path));
        ShowStatusMessage(Loc.Format("Disasm_Exported", Path.GetFileName(path)));
    }

    /// <summary>テスト用の保存先 (ファイルの選択ダイアログの代わり)。</summary>
    internal string? TestExportPath { get; set; }

    private (long Start, long End) ExportRange(EditorState editor, DisassemblyListing listing)
    {
        if (editor.HasSelection)
        {
            return (editor.SelectionStart, editor.SelectionStart + editor.SelectionLength);
        }

        if (DisasmVm.SelectedOffset is { } at && listing.Image.SectionOfOffset(at) is { } section)
        {
            return (section.FileOffset, section.FileOffset + section.FileSize);
        }

        IReadOnlyList<DisasmRow> rows = DisasmVm.Rows;
        return rows.Count == 0 ? (0, 0) : (rows[0].Offset, rows[^1].Offset + rows[^1].Length);
    }

    private static void WriteListing(DisassemblyListing listing, long start, long end, string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        long at = start;
        int skip = 0;
        while (at < end)
        {
            IReadOnlyList<ListingLine> lines = listing.Lines(at, 256, skip);
            if (lines.Count == 0)
            {
                break;
            }

            foreach (ListingLine l in lines)
            {
                if (l.Offset >= end)
                {
                    return;
                }

                writer.WriteLine(l.Kind == ListingLineKind.Heading
                    ? l.Label + ":"
                    : $"    {listing.AddressText(l)}  {l.Bytes,-24}  {l.Instruction!.Text}{(l.Comment.Length > 0 ? "  ; " + l.Comment : string.Empty)}");
            }

            ListingLine last = lines[^1];
            at = last.Offset + Math.Max(1, last.Length);
            skip = 0;
        }
    }

    // ---- 表示設定 (ANA-26) ----

    private static void SaveDisasmFormat(DisasmFormat f)
    {
        App.Settings.SetString(DisasmSettings.SyntaxKey, DisasmSettings.SyntaxValue(f.Syntax), "intel");
        App.Settings.SetString(DisasmSettings.HexStyleKey, f.HexStyle == HexStyle.Suffix ? "suffix" : "prefix", "prefix");
        App.Settings.SetBool(DisasmSettings.UppercaseHexKey, f.UppercaseHex, false);
        App.Settings.SetBool(DisasmSettings.SignedImmediatesKey, f.SignedImmediates, false);
        App.Settings.SetBool(DisasmSettings.UppercaseMnemonicsKey, f.UppercaseMnemonic, false);
        App.Settings.SetBool(DisasmSettings.ArmRegisterAliasesKey, f.ArmRegisterAlias, true);
        App.Settings.SetBool(DisasmSettings.ShowBytesKey, f.ShowBytes, true);
        App.Settings.SetInt(DisasmSettings.MaxBytesKey, f.MaxBytes, 8);
        App.Settings.SetString(DisasmSettings.AddressColumnKey, DisasmSettings.AddressColumnValue(f.AddressColumn), "virtual");
        App.Settings.SetBool(DisasmSettings.BranchLinesKey, f.ShowBranchLines, true);
        App.Settings.SetBool(DisasmSettings.CommentsKey, f.ShowComments, true);

        // 変更はその場で反映する (仕様 9)。設定の変更の通知を待たずに、このウィンドウにも反映する。
        foreach (MainWindow w in WindowManager.Windows)
        {
            w._disasmVm?.SetFormat(f);
            w._disasmPanel?.FormatChanged();
        }
    }

    // ---- ツールチップ (ANA-23 の仕様 7) ----

    private string? DisasmToolTip(DisasmRow row)
    {
        if (row.Native?.Instruction is not { } i || DisasmVm.Listing is not { } listing || Vm.Selected is not { } doc)
        {
            return row.Msil is { } m ? $"IL_{m.IlOffset:x4} ({m.Length} B)" : null;
        }

        try
        {
            using IInstructionDecoder decoder = Disassembler.Create(listing.Architecture, listing.Format, listing.Options.BigEndian
                ?? (listing.Architecture.HasEndian && listing.Image.IsExecutable ? listing.Image.BigEndian : null));
            byte[] bytes = new byte[i.Length];
            doc.Document.Current.Read(row.Offset, bytes);
            DisasmInstruction? detail = decoder.Decode(bytes, i.Address, detail: true);
            var sb = new StringBuilder();
            sb.AppendLine(Loc.Format("Disasm_TipLength", i.Length));
            if (detail?.Detail is { } d)
            {
                if (d.Operands.Count > 0)
                {
                    sb.AppendLine(Loc.Format("Disasm_TipOperands", string.Join(", ", d.Operands)));
                }

                sb.AppendLine(Loc.Format("Disasm_TipRead", d.RegistersRead.Count == 0 ? "-" : string.Join(", ", d.RegistersRead)));
                sb.AppendLine(Loc.Format("Disasm_TipWritten", d.RegistersWritten.Count == 0 ? "-" : string.Join(", ", d.RegistersWritten)));
                sb.Append(Loc.Format("Disasm_TipGroups", d.Groups.Count == 0 ? "-" : string.Join(", ", d.Groups)));
            }

            return sb.ToString().TrimEnd();
        }
        catch (DisassemblerUnavailableException)
        {
            return null;
        }
    }
}
