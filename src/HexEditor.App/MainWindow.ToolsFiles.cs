using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Hashing;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Panels;
using HexEditor.Core.Tools;
using HexEditor.Core.Tools.Checksums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// ファイルツールのうち、チェックサムファイルの作成・検証 (TOOL-18・TOOL-19) とフォルダの比較 (TOOL-20) のつなぎ込み。
/// 結果は共通の結果一覧のパネル (下) に、確定したものから出す。長時間処理は処理センターに進捗を出し、キャンセルできる。
/// </summary>
public sealed partial class MainWindow
{
    public const string ChecksumResultsPanelId = "checksumresults";
    public const string FolderComparePanelId = "foldercompare";

    private const string ChecksumFolderKey = "checksum.create.";
    private const string FolderCompareKey = "foldercompare.";

    // 結果の行は、パネルの中身を作り直しても残すようウィンドウで持つ。
    private readonly List<object> _checksumRows = [];
    private readonly List<object> _folderRows = [];
    private ToolResultTable? _checksumTable;
    private ToolResultTable? _folderTable;
    private string _checksumSummaryText = string.Empty;
    private string _folderSummaryText = string.Empty;
    private ChecksumDocument? _checksumDocument;
    private string? _checksumFile;
    private bool _checksumRunning;
    private bool _folderRunning;

    private static void RegisterToolsFilesPanels()
    {
        RegisterAdsPanel();
        if (PanelRegistry.Find(ChecksumResultsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(ChecksumResultsPanelId, "Panel_ChecksumResults_Title", PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window).CreateChecksumTable())
            {
                ToggleCommand = "tools.checksum.results",
                RequiresDocument = false,
                CanFloat = false,
            });
            PanelRegistry.Register(new PanelRegistration(FolderComparePanelId, "Panel_FolderCompare_Title", PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window).CreateFolderTable())
            {
                ToggleCommand = "tools.folderCompare.results",
                RequiresDocument = false,
                CanFloat = false,
            });
        }
    }

    private void RegisterToolsFilesCommands()
    {
        Commands.Register("tools.checksum.create", () => ShowChecksumCreateDialogAsync());
        Commands.Register("tools.checksum.verify", () => VerifyChecksumCommandAsync());
        Commands.Register("tools.folderCompare", () => ShowFolderCompareDialogAsync());
    }

    // ---- 選択ダイアログ(テストでは差し替える) ----

    private async Task<IReadOnlyList<string>> PickFilesAsync(string identifier, bool multiple)
    {
        if (TestHooks.OpenPickerResult(identifier) is { } paths)
        {
            return paths;
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = identifier };
        picker.FileTypeFilter.Add("*");
        if (multiple)
        {
            return [.. (await picker.PickMultipleFilesAsync()).Select(r => r.Path)];
        }

        return await picker.PickSingleFileAsync() is { } one ? [one.Path] : [];
    }

    private async Task<string?> PickFolderToolAsync(string identifier)
    {
        if (TestHooks.OpenPickerResult(identifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FolderPicker(WindowId) { SettingsIdentifier = identifier };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    // ---- チェックサムファイルの作成 (TOOL-18) ----

    private async Task ShowChecksumCreateDialogAsync()
    {
        var targets = new List<string>();
        if (Vm.Selected?.FilePath is { } active && File.Exists(active))
        {
            targets.Add(active);
        }

        TextBox targetText = DialogParts.Field("Sums_Targets", Loc.Get("Sums_Targets"), string.Join(Environment.NewLine, targets));
        targetText.AcceptsReturn = true;
        targetText.TextWrapping = TextWrapping.NoWrap;
        targetText.MinHeight = 80;
        targetText.MaxHeight = 140;
        Button addFiles = ToolParts.TextButton("Sums_AddFiles", "Sums_AddFiles", () => { });
        Button addFolder = ToolParts.TextButton("Sums_AddFolder", "Sums_AddFolder", () => { });
        addFiles.Click += async (_, _) => AppendTargets(targetText, await PickFilesAsync("HexEditor.ChecksumFiles", multiple: true));
        addFolder.Click += async (_, _) =>
        {
            if (await PickFolderToolAsync("HexEditor.ChecksumFolder") is { } folder)
            {
                AppendTargets(targetText, [folder]);
            }
        };
        CheckBox subfolders = DialogParts.Check("Sums_Subfolders", Loc.Get("Sums_Subfolders"), AppState.GetString(ChecksumFolderKey + "subfolders", "1") == "1");

        HashAlgorithmInfo[] algorithms = [.. HashCatalog.All.Where(a => a.IsAvailable && !a.IsCustom && a.Parameters == HashParameterKinds.None)];
        string savedAlgorithm = AppState.GetString(ChecksumFolderKey + "algorithm", "sha256");
        ComboBox algorithm = DialogParts.Combo("Sums_Algorithm", Loc.Get("Sums_Algorithm"), algorithms.Select(a => a.Name),
            Math.Max(0, Array.FindIndex(algorithms, a => a.Id == savedAlgorithm)));

        string[] formatKeys = ["Gnu", "Bsd", "Sidecar", "Sfv"];
        ComboBox format = DialogParts.Combo("Sums_Format", Loc.Get("Sums_Format"), formatKeys.Select(k => Loc.Get("Sums_Format_" + k)),
            Math.Clamp(int.TryParse(AppState.GetString(ChecksumFolderKey + "format", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out int f) ? f : 0, 0, 3));
        CheckBox binary = DialogParts.Check("Sums_BinaryMark", Loc.Get("Sums_BinaryMark"), false);
        RadioButton slash = DialogParts.Radio("Sums_SeparatorSlash", Loc.Get("Sums_SeparatorSlash"), "SumsSeparator", true);
        RadioButton backslash = DialogParts.Radio("Sums_SeparatorBackslash", Loc.Get("Sums_SeparatorBackslash"), "SumsSeparator", false);
        RadioButton lf = DialogParts.Radio("Sums_LineLf", Loc.Get("Sums_LineLf"), "SumsLine", true);
        RadioButton crlf = DialogParts.Radio("Sums_LineCrLf", Loc.Get("Sums_LineCrLf"), "SumsLine", false);
        TextBox output = DialogParts.Field("Sums_OutputName", Loc.Get("Sums_OutputName"));
        TextBlock info = DialogParts.Caption("Sums_Info");
        TextBlock error = DialogParts.Caption("Sums_Error");
        error.Foreground = ToolParts.ThemeBrush("SystemFillColorCriticalBrush");

        var body = new StackPanel { Spacing = 8, MinWidth = 440 };
        foreach (UIElement element in new UIElement[]
        {
            targetText, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addFiles, addFolder } }, subfolders, algorithm, format,
            binary, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { slash, backslash } },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { lf, crlf } }, output, info, error,
        })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ChecksumCreateDialog", Loc.Get("Sums_CreateTitle"), new ScrollViewer { Content = body, MaxHeight = 560 },
            Loc.Get("Sums_Create"));

        ChecksumCreateOptions Options() => new()
        {
            AlgorithmId = algorithms[Math.Max(0, algorithm.SelectedIndex)].Id,
            Format = (ChecksumFormat)Math.Max(0, format.SelectedIndex),
            IncludeSubfolders = subfolders.IsChecked == true,
            BinaryMark = binary.IsChecked == true,
            Separator = backslash.IsChecked == true ? ChecksumSeparator.Backslash : ChecksumSeparator.Slash,
            LineEnding = crlf.IsChecked == true ? ChecksumLineEnding.CrLf : ChecksumLineEnding.Lf,
            OutputFileName = output.Text.Trim().Length > 0 && format.SelectedIndex != (int)ChecksumFormat.Sidecar ? output.Text.Trim() : null,
        };

        IReadOnlyList<string> Targets() => [.. targetText.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        void Update()
        {
            // SFV は CRC-32 だけ(仕様 3)。SFV を選ぶと CRC-32 にし、CRC-32 以外では SFV を選べない。
            HashAlgorithmInfo selected = algorithms[Math.Max(0, algorithm.SelectedIndex)];
            if (format.SelectedIndex == (int)ChecksumFormat.Sfv && selected.Id != "crc32")
            {
                algorithm.SelectedIndex = Array.FindIndex(algorithms, a => a.Id == "crc32");
                return;
            }

            binary.IsEnabled = format.SelectedIndex is (int)ChecksumFormat.Gnu or (int)ChecksumFormat.Sidecar;
            output.IsEnabled = format.SelectedIndex != (int)ChecksumFormat.Sidecar;
            IReadOnlyList<string> paths = Targets();
            bool valid = paths.Count > 0 && paths.All(p => File.Exists(p) || Directory.Exists(p));
            error.Text = paths.Count == 0 ? Loc.Get("Sums_Error_NoTarget") : valid ? string.Empty : Loc.Get("Sums_Error_Missing");
            dialog.IsPrimaryButtonEnabled = valid;
            if (valid && format.SelectedIndex >= 0)
            {
                try
                {
                    ChecksumPlan plan = ChecksumCreator.Plan(paths, Options());
                    info.Text = Loc.Format("Sums_Info", plan.Files.Count, string.Join("; ", plan.OutputPaths.Take(1).Select(Path.GetFileName)));
                    if (output.Text.Length == 0 && plan.OutputPaths.Count > 0 && format.SelectedIndex != (int)ChecksumFormat.Sidecar)
                    {
                        output.PlaceholderText = Path.GetFileName(plan.OutputPaths[0]);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    info.Text = ex.Message;
                }
            }
            else
            {
                info.Text = string.Empty;
            }
        }

        targetText.TextChanged += (_, _) => Update();
        algorithm.SelectionChanged += (_, _) => Update();
        format.SelectionChanged += (_, _) => Update();
        subfolders.Click += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        ChecksumCreateOptions options = Options();
        AppState.SetString(ChecksumFolderKey + "subfolders", options.IncludeSubfolders ? "1" : "0");
        AppState.SetString(ChecksumFolderKey + "algorithm", options.AlgorithmId);
        AppState.SetString(ChecksumFolderKey + "format", ((int)options.Format).ToString(CultureInfo.InvariantCulture));
        await CreateChecksumsAsync(Targets(), options);
    }

    private static void AppendTargets(TextBox box, IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            box.Text = string.Join(Environment.NewLine, box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Concat(paths));
        }
    }

    /// <summary>チェックサムファイルを作る(上書きの確認、処理センターの進捗、読めないファイルの一覧)。</summary>
    internal async Task<ChecksumCreateResult?> CreateChecksumsAsync(IReadOnlyList<string> targets, ChecksumCreateOptions options, bool confirmOverwrite = true)
    {
        ChecksumPlan plan;
        try
        {
            plan = ChecksumCreator.Plan(targets, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Error);
            return null;
        }

        if (plan.Files.Count == 0)
        {
            ShowNotice(Loc.Get("Sums_Error_NoFiles"), InfoBarSeverity.Warning);
            return null;
        }

        // 出力先に同名のファイルがあれば、上書きの確認を出す(仕様 6)。
        if (confirmOverwrite && plan.ExistingOutputs.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            list.Children.Add(new TextBlock { Text = Loc.Format("Sums_OverwriteBody", plan.ExistingOutputs.Count), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
            list.Children.Add(new TextBlock
            {
                Text = string.Join(Environment.NewLine, plan.ExistingOutputs.Take(8)),
                FontFamily = ToolParts.Mono,
                TextWrapping = TextWrapping.NoWrap,
            });
            if (!await ConfirmAsync(Loc.Get("Sums_OverwriteTitle"), list, Loc.Get("Sums_Overwrite"), "ChecksumOverwriteDialog"))
            {
                return null;
            }
        }

        ChecksumCreateResult result;
        try
        {
            result = await Vm.Operations.RunAsync(Loc.Format("Sums_OpCreate", plan.Files.Count), OperationKind.WritesExternal, null, plan.TotalBytes,
                op => Task.FromResult(ChecksumCreator.Create(plan, options, op, op.CancellationToken)));
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.Get("Sums_Cancelled"), InfoBarSeverity.Informational);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Sums_Error_Write", ex.Message), InfoBarSeverity.Error);
            return null;
        }

        string written = result.Written.Count == 1 ? result.Written[0] : Loc.Format("Sums_WrittenMany", result.Written.Count);
        ShowNotice(Loc.Format("Sums_Done", result.FileCount, written), InfoBarSeverity.Success);
        if (result.Skipped.Count > 0)
        {
            await ShowSkippedChecksumFilesAsync(result.Skipped);
        }

        return result;
    }

    private async Task ShowSkippedChecksumFilesAsync(IReadOnlyList<ChecksumSkipped> skipped)
    {
        var text = new TextBlock
        {
            Text = string.Join(Environment.NewLine, skipped.Select(s => $"{s.Path}{Environment.NewLine}    {s.Reason}")),
            FontFamily = ToolParts.Mono,
            FontSize = 12,
            IsTextSelectionEnabled = true,
        };
        AutomationProperties.SetAutomationId(text, "Sums_SkippedList");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = Loc.Format("Sums_SkippedBody", skipped.Count), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = text, MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        ContentDialog dialog = DialogParts.Dialog(Root, "ChecksumSkippedDialog", Loc.Get("Sums_SkippedTitle"), body, Loc.Get("Common_Close"));
        dialog.CloseButtonText = string.Empty;
        await ShowEditDialogAsync(dialog);
    }

    // ---- チェックサムファイルの検証 (TOOL-19) ----

    private async Task VerifyChecksumCommandAsync()
    {
        IReadOnlyList<string> picked = await PickFilesAsync("HexEditor.ChecksumVerify", multiple: false);
        if (picked.FirstOrDefault() is not { } path)
        {
            return;
        }

        await VerifyChecksumPathAsync(path);
    }

    /// <summary>
    /// 選んだファイルを検証する。チェックサムファイルでなく、同じ名前のサイドカー(<c>ファイル名.sha256</c> など)があれば、それで検証することを
    /// 提案する(仕様 2)。
    /// </summary>
    internal async Task VerifyChecksumPathAsync(string path, bool confirmSidecar = true)
    {
        if (!ChecksumReader.IsChecksumFile(path))
        {
            string? sidecar = ChecksumReader.FindSidecar(path);
            if (sidecar is null)
            {
                ShowNotice(Loc.Format("Sums_Error_NotChecksum", Path.GetFileName(path)), InfoBarSeverity.Error);
                return;
            }

            if (confirmSidecar && !await ConfirmAsync(Loc.Get("Sums_SidecarTitle"), Loc.Format("Sums_SidecarBody", Path.GetFileName(path), Path.GetFileName(sidecar)),
                Loc.Get("Sums_SidecarUse"), "ChecksumSidecarDialog"))
            {
                return;
            }

            path = sidecar;
        }

        await VerifyChecksumFileAsync(path);
    }

    /// <summary>チェックサムファイルを検証し、結果を結果一覧のパネルに確定したものから出す。</summary>
    internal async Task VerifyChecksumFileAsync(string path)
    {
        if (_checksumRunning)
        {
            ShowNotice(Loc.Get("Sums_Error_Running"), InfoBarSeverity.Warning);
            return;
        }

        ChecksumDocument document;
        try
        {
            document = await Task.Run(() => ChecksumReader.ReadFile(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Sums_Error_Read", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
            return;
        }

        string? fallback = null;
        if (document.NeedsAlgorithm)
        {
            // アルゴリズムを判定できない行がある: 利用者に選ばせる(仕様 1)。
            fallback = await AskChecksumAlgorithmAsync();
            if (fallback is null)
            {
                return;
            }
        }

        _checksumDocument = document;
        _checksumFile = path;
        _checksumRows.Clear();
        _checksumSummaryText = Loc.Get("Sums_Verifying");
        ShowPanel(ChecksumResultsPanelId);
        _checksumTable?.Clear();
        _checksumTable?.SetSummary(_checksumSummaryText);
        _checksumRunning = true;
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Sums_OpVerify", Path.GetFileName(path)), OperationKind.ReadOnly, null, null, op => Task.FromResult(
                ChecksumVerifier.Verify(document, baseDirectory, fallback, row => DispatcherQueue.TryEnqueue(() => AddChecksumRow(row)), op, op.CancellationToken)));
            UpdateChecksumSummary(done: true);
        }
        catch (OperationCanceledException)
        {
            _checksumSummaryText = Loc.Format("Sums_VerifyCancelled", ChecksumSummaryText());
            _checksumTable?.SetSummary(_checksumSummaryText);
        }
        finally
        {
            _checksumRunning = false;
        }

        if (document.Errors.Count > 0)
        {
            // 書式の誤りは、行番号を示して飛ばした(仕様の「エラー」)。
            string lines = string.Join(", ", document.Errors.Take(10).Select(e => e.LineNumber.ToString(CultureInfo.CurrentCulture)))
                + (document.Errors.Count > 10 ? ", …" : string.Empty);
            ShowNotice(Loc.Format("Sums_SkippedLines", document.Errors.Count, lines), InfoBarSeverity.Warning);
        }
    }

    private async Task<string?> AskChecksumAlgorithmAsync()
    {
        HashAlgorithmInfo[] algorithms = [.. HashCatalog.All.Where(a => a.IsAvailable && !a.IsCustom && a.Parameters == HashParameterKinds.None)];
        ComboBox combo = DialogParts.Combo("Sums_AskAlgorithm", Loc.Get("Sums_Algorithm"), algorithms.Select(a => a.Name), Math.Max(0, Array.FindIndex(algorithms, a => a.Id == "sha256")));
        var body = new StackPanel { Spacing = 8, MinWidth = 380 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Sums_AskAlgorithmBody"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(combo);
        ContentDialog dialog = DialogParts.Dialog(Root, "ChecksumAlgorithmDialog", Loc.Get("Sums_AskAlgorithmTitle"), body, Loc.Get("Sums_Verify"));
        return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary ? algorithms[Math.Max(0, combo.SelectedIndex)].Id : null;
    }

    private void AddChecksumRow(ChecksumResultRow row)
    {
        _checksumRows.Add(row);
        _checksumTable?.Add(row);
        UpdateChecksumSummary(done: false);
    }

    private string ChecksumSummaryText()
    {
        ChecksumSummary s = ChecksumSummary.Of(_checksumRows.Cast<ChecksumResultRow>());
        var parts = new List<string>();
        if (s.Match > 0)
        {
            parts.Add(Loc.Format("Sums_Summary_Match", s.Match));
        }

        if (s.Mismatch > 0)
        {
            parts.Add(Loc.Format("Sums_Summary_Mismatch", s.Mismatch));
        }

        if (s.Missing > 0)
        {
            parts.Add(Loc.Format("Sums_Summary_Missing", s.Missing));
        }

        if (s.ReadError > 0)
        {
            parts.Add(Loc.Format("Sums_Summary_ReadError", s.ReadError));
        }

        return parts.Count == 0 ? Loc.Get("Sums_Summary_None") : string.Join(Loc.Get("Sums_SummarySeparator"), parts);
    }

    private void UpdateChecksumSummary(bool done)
    {
        _checksumSummaryText = done ? ChecksumSummaryText() : Loc.Format("Sums_Verifying2", ChecksumSummaryText());
        _checksumTable?.SetSummary(_checksumSummaryText);
    }

    private static StatusCell ChecksumStatusCell(object row) => ((ChecksumResultRow)row).Status switch
    {
        ChecksumStatus.Match => new StatusCell("", Loc.Get("Sums_Status_Match"), "SystemFillColorSuccessBrush"),
        ChecksumStatus.Mismatch => new StatusCell("", Loc.Get("Sums_Status_Mismatch"), "SystemFillColorCriticalBrush"),
        ChecksumStatus.Missing => new StatusCell("", Loc.Get("Sums_Status_Missing"), "SystemFillColorCautionBrush"),
        _ => new StatusCell("", Loc.Get("Sums_Status_ReadError"), "SystemFillColorCriticalBrush"),
    };

    private ToolResultTable CreateChecksumTable()
    {
        var table = new ToolResultTable("Sums");
        table.Configure(
        [
            new ToolColumn(Loc.Get("Sums_Col_State"), 150, r => ChecksumStatusCell(r).Text, r => (int)((ChecksumResultRow)r).Status, ChecksumStatusCell),
            new ToolColumn(Loc.Get("Sums_Col_Path"), 0, r => ((ChecksumResultRow)r).Path),
            new ToolColumn(Loc.Get("Sums_Col_Expected"), 260, r => ((ChecksumResultRow)r).Expected),
            new ToolColumn(Loc.Get("Sums_Col_Actual"), 260, r => ((ChecksumResultRow)r).Actual ?? string.Empty),
            new ToolColumn(Loc.Get("Sums_Col_Size"), 110, r => ((ChecksumResultRow)r).Size is { } s ? s.ToString("N0", CultureInfo.CurrentCulture) : string.Empty,
                r => ((ChecksumResultRow)r).Size ?? -1),
        ],
        [
            (Loc.Get("Sums_Filter_All"), null),
            (Loc.Get("Sums_Status_Match"), r => ((ChecksumResultRow)r).Status == ChecksumStatus.Match),
            (Loc.Get("Sums_Status_Mismatch"), r => ((ChecksumResultRow)r).Status == ChecksumStatus.Mismatch),
            (Loc.Get("Sums_Status_Missing"), r => ((ChecksumResultRow)r).Status == ChecksumStatus.Missing),
            (Loc.Get("Sums_Status_ReadError"), r => ((ChecksumResultRow)r).Status == ChecksumStatus.ReadError),
        ]);
        foreach (object row in _checksumRows)
        {
            table.Add(row);
        }

        table.SetSummary(_checksumSummaryText);
        table.ContextItems.Add((Loc.Get("Sums_Open"), r => File.Exists(((ChecksumResultRow)r).FullPath), r => TryOpen(((ChecksumResultRow)r).FullPath)));
        table.RowActivated += (_, r) =>
        {
            if (File.Exists(((ChecksumResultRow)r).FullPath))
            {
                TryOpen(((ChecksumResultRow)r).FullPath);
            }
        };
        table.ExportRequested += async (_, format) => await ExportToolTableAsync(table, "checksums", format);
        _checksumTable = table;
        return table;
    }

    /// <summary>InfoBar「チェックサムファイルです」と「検証する」(TOOL-19 の仕様 7)。</summary>
    private void SuggestChecksumVerify(DocumentViewModel doc)
    {
        if (doc.FilePath is not { } path || !ChecksumReader.IsChecksumFile(path))
        {
            return;
        }

        ShowNotice(Loc.Get("Sums_InfoBar"), InfoBarSeverity.Informational, doc, actions:
        [
            new NotificationAction(Loc.Get("Sums_Verify"), () => _ = VerifyChecksumFileAsync(path)),
        ]);
    }

    // ---- フォルダの比較 (TOOL-20) ----

    private async Task ShowFolderCompareDialogAsync()
    {
        TextBox left = DialogParts.Field("FolderCmp_Left", Loc.Get("FolderCmp_Left"), AppState.GetString(FolderCompareKey + "left", string.Empty));
        TextBox right = DialogParts.Field("FolderCmp_Right", Loc.Get("FolderCmp_Right"), AppState.GetString(FolderCompareKey + "right", string.Empty));
        Button browseLeft = ToolParts.TextButton("FolderCmp_BrowseLeft", "FolderCmp_Browse", () => { });
        Button browseRight = ToolParts.TextButton("FolderCmp_BrowseRight", "FolderCmp_Browse", () => { });
        browseLeft.Click += async (_, _) =>
        {
            if (await PickFolderToolAsync("HexEditor.FolderCompareLeft") is { } folder)
            {
                left.Text = folder;
            }
        };
        browseRight.Click += async (_, _) =>
        {
            if (await PickFolderToolAsync("HexEditor.FolderCompareRight") is { } folder)
            {
                right.Text = folder;
            }
        };
        CheckBox subfolders = DialogParts.Check("FolderCmp_Subfolders", Loc.Get("FolderCmp_Subfolders"), AppState.GetString(FolderCompareKey + "subfolders", "1") == "1");
        TextBox pattern = DialogParts.Field("FolderCmp_Pattern", Loc.Get("FolderCmp_Pattern"), AppState.GetString(FolderCompareKey + "pattern", string.Empty));
        pattern.PlaceholderText = "*.bin";
        CheckBox hash = DialogParts.Check("FolderCmp_Hash", Loc.Get("FolderCmp_Hash"), AppState.GetString(FolderCompareKey + "hash", "0") == "1");
        TextBlock error = DialogParts.Caption("FolderCmp_Error");
        error.Foreground = ToolParts.ThemeBrush("SystemFillColorCriticalBrush");

        var body = new StackPanel { Spacing = 8, MinWidth = 440 };
        foreach (UIElement e in new UIElement[]
        {
            left, browseLeft, right, browseRight, subfolders, pattern, hash, error,
        })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "FolderCompareDialog", Loc.Get("FolderCmp_Title"), body, Loc.Get("FolderCmp_Compare"));
        void Update()
        {
            bool leftOk = Directory.Exists(left.Text.Trim()), rightOk = Directory.Exists(right.Text.Trim());
            DialogParts.MarkInvalid(left, left.Text.Length > 0 && !leftOk);
            DialogParts.MarkInvalid(right, right.Text.Length > 0 && !rightOk);
            error.Text = left.Text.Length > 0 && !leftOk || right.Text.Length > 0 && !rightOk ? Loc.Get("FolderCmp_Error_Missing") : string.Empty;
            dialog.IsPrimaryButtonEnabled = leftOk && rightOk;
        }

        left.TextChanged += (_, _) => Update();
        right.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var options = new FolderCompareOptions { IncludeSubfolders = subfolders.IsChecked == true, Pattern = pattern.Text.Trim(), UseHash = hash.IsChecked == true };
        AppState.SetString(FolderCompareKey + "left", left.Text.Trim());
        AppState.SetString(FolderCompareKey + "right", right.Text.Trim());
        AppState.SetString(FolderCompareKey + "subfolders", options.IncludeSubfolders ? "1" : "0");
        AppState.SetString(FolderCompareKey + "pattern", options.Pattern);
        AppState.SetString(FolderCompareKey + "hash", options.UseHash ? "1" : "0");
        await CompareFoldersAsync(left.Text.Trim(), right.Text.Trim(), options);
    }

    private static StatusCell FolderStatusCell(object row) => ((FolderCompareRow)row).Status switch
    {
        FolderCompareStatus.Same => new StatusCell("", Loc.Get("FolderCmp_Status_Same"), "SystemFillColorSuccessBrush"),
        FolderCompareStatus.Different => new StatusCell("", Loc.Get("FolderCmp_Status_Different"), "SystemFillColorCriticalBrush"),
        FolderCompareStatus.LeftOnly => new StatusCell("", Loc.Get("FolderCmp_Status_LeftOnly"), "SystemFillColorCautionBrush"),
        FolderCompareStatus.RightOnly => new StatusCell("", Loc.Get("FolderCmp_Status_RightOnly"), "SystemFillColorCautionBrush"),
        _ => new StatusCell("", Loc.Get("FolderCmp_Status_ReadError"), "SystemFillColorCriticalBrush"),
    };

    private string? _folderLeft, _folderRight;

    private ToolResultTable CreateFolderTable()
    {
        var table = new ToolResultTable("FolderCmp");
        table.Configure(
        [
            new ToolColumn(Loc.Get("FolderCmp_Col_State"), 150, r => FolderStatusCell(r).Text, r => (int)((FolderCompareRow)r).Status, FolderStatusCell),
            new ToolColumn(Loc.Get("FolderCmp_Col_Path"), 0, r => ((FolderCompareRow)r).RelativePath),
            new ToolColumn(Loc.Get("FolderCmp_Col_LeftSize"), 110, r => Size(((FolderCompareRow)r).LeftSize), r => ((FolderCompareRow)r).LeftSize ?? -1),
            new ToolColumn(Loc.Get("FolderCmp_Col_RightSize"), 110, r => Size(((FolderCompareRow)r).RightSize), r => ((FolderCompareRow)r).RightSize ?? -1),
            new ToolColumn(Loc.Get("FolderCmp_Col_FirstDiff"), 150, r => ((FolderCompareRow)r).FirstDifference is { } d ? "0x" + d.ToString("X", CultureInfo.InvariantCulture) : HashText((FolderCompareRow)r),
                r => ((FolderCompareRow)r).FirstDifference ?? -1),
        ],
        [
            (Loc.Get("FolderCmp_Filter_HideSame"), r => ((FolderCompareRow)r).Status != FolderCompareStatus.Same),
            (Loc.Get("FolderCmp_Filter_All"), null),
            (Loc.Get("FolderCmp_Status_Same"), r => ((FolderCompareRow)r).Status == FolderCompareStatus.Same),
            (Loc.Get("FolderCmp_Status_Different"), r => ((FolderCompareRow)r).Status == FolderCompareStatus.Different),
            (Loc.Get("FolderCmp_Status_LeftOnly"), r => ((FolderCompareRow)r).Status == FolderCompareStatus.LeftOnly),
            (Loc.Get("FolderCmp_Status_RightOnly"), r => ((FolderCompareRow)r).Status == FolderCompareStatus.RightOnly),
            (Loc.Get("FolderCmp_Status_ReadError"), r => ((FolderCompareRow)r).Status == FolderCompareStatus.ReadError),
        ]);
        foreach (object row in _folderRows)
        {
            table.Add(row);
        }

        table.SetSummary(_folderSummaryText);
        table.ContextItems.Add((Loc.Get("FolderCmp_OpenCompare"), r => ((FolderCompareRow)r).Status == FolderCompareStatus.Different, r => _ = OpenFolderRowAsync((FolderCompareRow)r)));
        table.RowActivated += (_, r) =>
        {
            if (((FolderCompareRow)r).Status == FolderCompareStatus.Different)
            {
                _ = OpenFolderRowAsync((FolderCompareRow)r);
            }
        };
        table.ExportRequested += async (_, format) => await ExportToolTableAsync(table, "folder-compare", format);
        _folderTable = table;
        return table;

        static string Size(long? size) => size is { } s ? s.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

        static string HashText(FolderCompareRow row) => row.LeftHash is { } h && row.Status == FolderCompareStatus.Different ? h[..8] + "…" : string.Empty;
    }

    /// <summary>「異なる」の行を開く: 2 つのファイルをファイルの比較の画面で開く(仕様 4)。</summary>
    internal Task OpenFolderRowAsync(FolderCompareRow row)
    {
        if (row.LeftPath is null || row.RightPath is null)
        {
            return Task.CompletedTask;
        }

        return OpenCompareFromCommandLineAsync(row.LeftPath, row.RightPath);
    }

    /// <summary>2 つのフォルダを比べ、結果を結果一覧のパネルに確定したものから出す。</summary>
    internal async Task CompareFoldersAsync(string left, string right, FolderCompareOptions options)
    {
        if (_folderRunning)
        {
            ShowNotice(Loc.Get("FolderCmp_Error_Running"), InfoBarSeverity.Warning);
            return;
        }

        _folderLeft = left;
        _folderRight = right;
        _folderRows.Clear();
        _folderSummaryText = Loc.Get("FolderCmp_Comparing");
        ShowPanel(FolderComparePanelId);
        _folderTable?.Clear();
        _folderTable?.SetSummary(_folderSummaryText);
        _folderRunning = true;
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("FolderCmp_Op", Path.GetFileName(left.TrimEnd('\\', '/')), Path.GetFileName(right.TrimEnd('\\', '/'))),
                OperationKind.ReadOnly, null, null, op => Task.FromResult(
                    FolderComparer.Compare(left, right, options, row => DispatcherQueue.TryEnqueue(() => AddFolderRow(row)), op, op.CancellationToken)));
            UpdateFolderSummary(done: true);
        }
        catch (OperationCanceledException)
        {
            _folderSummaryText = Loc.Format("FolderCmp_Cancelled", FolderSummaryText());
            _folderTable?.SetSummary(_folderSummaryText);
        }
        finally
        {
            _folderRunning = false;
        }
    }

    private void AddFolderRow(FolderCompareRow row)
    {
        _folderRows.Add(row);
        _folderTable?.Add(row);
        UpdateFolderSummary(done: false);
    }

    private string FolderSummaryText()
    {
        int[] counts = new int[5];
        foreach (FolderCompareRow row in _folderRows)
        {
            counts[(int)row.Status]++;
        }

        string[] keys = ["Same", "Different", "LeftOnly", "RightOnly", "ReadError"];
        var parts = new List<string>();
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                parts.Add(Loc.Format("FolderCmp_Summary_" + keys[i], counts[i]));
            }
        }

        return parts.Count == 0 ? Loc.Get("FolderCmp_Summary_None") : string.Join(Loc.Get("Sums_SummarySeparator"), parts);
    }

    private void UpdateFolderSummary(bool done)
    {
        _folderSummaryText = done ? FolderSummaryText() : Loc.Format("FolderCmp_Comparing2", FolderSummaryText());
        _folderTable?.SetSummary(_folderSummaryText);
    }

    // ---- 結果一覧の書き出し (00-overview 9 章: CSV / JSON) ----

    private async Task ExportToolTableAsync(ToolResultTable table, string baseName, string format)
    {
        string name = baseName + "." + format;
        string? path;
        if (TestHooks.TrySavePicker(name, out string? chosen))
        {
            path = chosen;
        }
        else
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = name, SettingsIdentifier = "HexEditor.ToolTableExport" };
            picker.FileTypeChoices.Add(format.ToUpperInvariant(), ["." + format]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, format == "json" ? table.ToJson() : table.ToCsv(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ShowStatusMessage(Loc.Format("ToolTable_Exported", Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Sums_Error_Write", ex.Message), InfoBarSeverity.Error);
        }
    }
}
