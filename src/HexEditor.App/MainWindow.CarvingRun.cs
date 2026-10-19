using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Forensics;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>カービングの実行の依頼 (ダイアログの入力、またはテスト用の命令)。</summary>
internal sealed record CarveRequest(CarveScopeKind Scope, IReadOnlyList<CarvingSignature> Signatures, bool SkipInside, bool Export, string? OutputFolder);

/// <summary>
/// カービングの実行 (12 の FOR-19): ダイアログ (範囲、定義、オプション、書き出し先)、出力先の検査、長時間処理としての実行 (進捗・速度・残り時間・種類ごとの件数)、
/// 結果のパネル、結果の行からのジャンプ。書き出し先が調査対象と同じディスクなら開始しない。
/// </summary>
public sealed partial class MainWindow
{
    private bool _carvingRunning;

    /// <param name="fromSelection">Hex ビューの右クリックメニュー (選択範囲があるとき) から呼んだ: 対象範囲の既定を「選択範囲」にする。</param>
    private async Task ShowCarvingDialogAsync(bool fromSelection = false)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        IAllocationMap? allocation = ForensicsService.AllocationMapProvider?.Invoke(doc);
        bool preferSelection = fromSelection && doc.Editor.HasSelection;
        RadioButton whole = DialogParts.Radio("Carve_Scope_Whole", Loc.Get("Carve_Scope_Whole"), "CarveScope", !preferSelection);
        RadioButton selection = DialogParts.Radio("Carve_Scope_Selection", Loc.Get("Carve_Scope_Selection"), "CarveScope", preferSelection);
        RadioButton unallocated = DialogParts.Radio("Carve_Scope_Unallocated", Loc.Get("Carve_Scope_Unallocated"), "CarveScope", false);
        RadioButton slack = DialogParts.Radio("Carve_Scope_Slack", Loc.Get("Carve_Scope_Slack"), "CarveScope", false);
        selection.IsEnabled = doc.Editor.HasSelection;
        unallocated.IsEnabled = slack.IsEnabled = allocation is not null;
        var checks = new List<(CarvingSignature Signature, CheckBox Box)>();
        var sigPanel = new StackPanel { Spacing = 2 };
        foreach (CarvingSignature s in ForensicsService.Signatures.All)
        {
            CheckBox box = DialogParts.Check("Carve_Sig_" + s.Name, s.Name + (s.BuiltIn ? string.Empty : " *"), s.Enabled);
            checks.Add((s, box));
            sigPanel.Children.Add(box);
        }

        CheckBox skip = DialogParts.Check("Carve_SkipInside", Loc.Get("Carve_SkipInside"), false);
        CheckBox export = DialogParts.Check("Carve_Export", Loc.Get("Carve_Export"), true);
        string defaultFolder = ForensicsService.Cases.CurrentCase?.CarvedFolder ?? AppState.GetString("forensics.carving.lastFolder", string.Empty);
        TextBox output = DialogParts.Field("Carve_Output", Loc.Get("Carve_Output"), defaultFolder);
        var browse = new Button { Content = Loc.Get("Case_Browse") };
        AutomationProperties.SetAutomationId(browse, "Carve_Browse");
        browse.Click += async (_, _) =>
        {
            if (await PickFolderToolAsync("HexEditor.CarveOutput") is { } picked)
            {
                output.Text = picked;
            }
        };
        var error = DialogParts.Caption("Carve_Error");
        error.TextWrapping = TextWrapping.Wrap;
        error.Foreground = Controls.Tools.ToolParts.ThemeBrush("SystemFillColorCriticalBrush");

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Carve_Scope"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        foreach (UIElement e in new UIElement[] { whole, selection, unallocated, slack })
        {
            body.Children.Add(e);
        }

        body.Children.Add(new TextBlock { Text = Loc.Get("Carve_Signatures"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        body.Children.Add(new ScrollViewer { Content = sigPanel, MaxHeight = 150 });
        foreach (UIElement e in new UIElement[] { skip, export, output, browse, error })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "CarveDialog", Loc.Get("Carve_Title"), new ScrollViewer { Content = body, MaxHeight = 620 }, Loc.Get("Carve_Start"));
        void Update()
        {
            output.IsEnabled = browse.IsEnabled = export.IsChecked == true;
            dialog.IsPrimaryButtonEnabled = checks.Any(c => c.Box.IsChecked == true) && (export.IsChecked != true || output.Text.Trim().Length > 0);
        }

        foreach ((_, CheckBox box) in checks)
        {
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
        }

        export.Checked += (_, _) => Update();
        export.Unchecked += (_, _) => Update();
        output.TextChanged += (_, _) => Update();
        Update();

        CarveRequest? request = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            CarveScopeKind scope = selection.IsChecked == true ? CarveScopeKind.Selection : unallocated.IsChecked == true ? CarveScopeKind.Unallocated
                : slack.IsChecked == true ? CarveScopeKind.Slack : CarveScopeKind.WholeDocument;
            var chosen = new CarveRequest(scope, [.. checks.Where(c => c.Box.IsChecked == true).Select(c => c.Signature with { Enabled = true })], skip.IsChecked == true,
                export.IsChecked == true, export.IsChecked == true ? output.Text.Trim() : null);
            if (PreflightCarving(doc, chosen) is { } problem)
            {
                // 開始しない: 理由を示してダイアログを開いたままにする。
                args.Cancel = true;
                error.Text = problem;
                return;
            }

            request = chosen;
        };
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || request is null)
        {
            return;
        }

        if (request.OutputFolder is not null)
        {
            AppState.SetString("forensics.carving.lastFolder", request.OutputFolder);
        }

        await RunCarvingAsync(doc, request);
    }

    /// <summary>実行前の検査 (出力先の検査: 調査対象のディスク上のボリュームでない・空き容量・1 ファイルの上限)。問題があれば説明、なければ null。</summary>
    internal string? PreflightCarving(DocumentViewModel doc, CarveRequest request)
    {
        if (request.OutputFolder is not { } folder)
        {
            return null;
        }

        var devices = new List<string>(ForensicsService.Cases.EvidenceDevicePaths);
        if (doc.Device is { } device)
        {
            devices.Add(device.Info.Path);
        }

        long largest = request.Signatures.Count == 0 ? 0 : request.Signatures.Max(s => s.MaxSize);
        OutputCheck check = OutputGuard.Check(folder, devices, 16L * 1024 * 1024, Math.Min(largest, doc.Document.Length), new CatalogDiskLocator(() => DeviceService.EnumerateDevices()));
        return check.Problem switch
        {
            OutputProblem.None => null,
            OutputProblem.SameDisk => Loc.Get("Carve_Error_SameDisk"),
            OutputProblem.NotEnoughSpace => Loc.Format("Carve_Error_Space", (check.RequiredBytes ?? 0).ToString("N0", CultureInfo.CurrentCulture), (check.FreeBytes ?? 0).ToString("N0", CultureInfo.CurrentCulture)),
            OutputProblem.FileTooLarge => Loc.Format("Carve_Error_FileSize", (check.FileLimit ?? 0).ToString("N0", CultureInfo.CurrentCulture)),
            _ => Loc.Get("Carve_Error_Folder"),
        };
    }

    /// <summary>カービングを長時間処理として実行する。キャンセルしてもそれまでの結果とマニフェストを残す。</summary>
    internal async Task<CarvingResult?> RunCarvingAsync(DocumentViewModel doc, CarveRequest request)
    {
        if (_carvingRunning)
        {
            return null;
        }

        IReadOnlyList<SearchRange> selection = doc.Editor.HasSelection ? [new SearchRange(doc.Editor.SelectionStart, doc.Editor.SelectionLength)] : [];
        // 割り当ての状況 (未割り当て・スラックの区間) の計算はボリューム全体の走査になるため、UI のスレッドではなく処理の中で行う (FOR-19 の対象範囲)。
        IAllocationMap? allocation = request.Scope is CarveScopeKind.Unallocated or CarveScopeKind.Slack ? ForensicsService.AllocationMapProvider?.Invoke(doc) : null;
        if (request.Scope is CarveScopeKind.Unallocated or CarveScopeKind.Slack && allocation is null)
        {
            ShowNotice(Loc.Get("Carve_Error_NoAllocation"), InfoBarSeverity.Error);
            return null;
        }

        long documentLength = doc.Document.Length;

        int sector = SectorSizeOf(doc);
        var store = new CarvedFileStore(request.Signatures, sector);
        ForensicsService.CarvingTarget = doc;
        ForensicsService.SetCarving(null, store);
        store.Changed += (_, _) => ForensicsService.NotifyCarvingProgress();
        ShowPanel(CarvingPanelId, focus: false);

        string? source = doc.Device?.Info.Path ?? doc.FilePath;
        EvidenceItem? evidence = source is null ? null : ForensicsService.Cases.CurrentCase?.Evidence.FirstOrDefault(e => ForensicCase.SourceKey(e.Kind, e.Path) == ForensicCase.SourceKey(KindOf(doc), source));
        Core.Engine.DocumentSnapshot snapshot = doc.Document.DataSnapshot;
        bool exporting = request.Export && request.OutputFolder is not null;
        _carvingRunning = true;
        Commands.NotifyStatesChanged();
        try
        {
            CarvingResult result = await Vm.Operations.RunAsync(Loc.Get("Carve_Operation"), exporting ? OperationKind.WritesExternal : OperationKind.ReadOnly, exporting ? null : doc.Document,
                null, op =>
                {
                    IReadOnlyList<SearchRange> ranges = CarveScope.Resolve(request.Scope, documentLength, selection, allocation);
                    op.SetTotal(ranges.Sum(r => r.Length));
                    return CarvingEngine.RunAsync(new SnapshotCarveData(snapshot), new CarvingOptions
                {
                    Signatures = request.Signatures,
                    Ranges = ranges,
                    SkipInside = request.SkipInside,
                    OutputFolder = request.OutputFolder,
                    Export = request.Export,
                    SectorSize = sector,
                    Store = store,
                    Resolver = StructureResolver(),
                    OnOutputProblem = (info, token) => AskOutputProblemAsync(info, token),
                    Audit = ForensicsService.Audit,
                    EvidenceId = evidence?.Id,
                    SourcePath = source,
                    Progress = new Progress<CarvingProgress>(p =>
                    {
                        op.Report(Math.Min(op.TotalBytes ?? p.BytesScanned, p.BytesScanned));
                        op.ReportMatches(p.FilesFound);
                        op.ReportDetail(string.Join(", ", p.CountsByType.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value:N0}")));
                    }),
                },
                op.CancellationToken);
                });
            ForensicsService.SetCarving(result, result.Files);
            ShowCarvingSummary(result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Carve_Failed", ex.Message), InfoBarSeverity.Error);
            return null;
        }
        finally
        {
            _carvingRunning = false;
            Commands.NotifyStatesChanged();
        }
    }

    private void ShowCarvingSummary(CarvingResult result)
    {
        string counts = string.Join(", ", result.CountsByType.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value:N0}"));
        string text = result.AbortedByOutputProblem ? Loc.Format("Carve_Aborted", result.Files.Count)
            : result.Cancelled ? Loc.Format("Carve_Cancelled", result.Files.Count)
            : Loc.Format("Carve_Done", result.Files.Count) + (counts.Length > 0 ? $" ({counts})" : string.Empty);
        var actions = new List<Core.Notifications.NotificationAction>();
        if (result.OutputFolder is not null && Directory.Exists(result.OutputFolder))
        {
            actions.Add(new Core.Notifications.NotificationAction(Loc.Get("Carving_OpenFolder"), OpenCarvingFolder));
        }

        ShowNotice(text, result.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Success, actions: actions.Count == 0 ? null : actions);
    }

    /// <summary>書き出し先の問題 (空き容量の不足など): 走査を止めたまま、「再試行」「書き出し先を変える」「中止」を選ばせる (FOR-19 の「エラー」)。</summary>
    private Task<OutputDecision> AskOutputProblemAsync(OutputProblemInfo info, CancellationToken token)
    {
        var completion = new TaskCompletionSource<OutputDecision>();
        token.Register(() => completion.TrySetResult(new OutputDecision(OutputDecisionKind.Abort)));
        DispatcherQueue.TryEnqueue(async () =>
        {
            string message = info.Kind == OutputProblem.NotEnoughSpace
                ? Loc.Format("Carve_Problem_Space", info.Folder, info.RequiredBytes.ToString("N0", CultureInfo.CurrentCulture), info.FreeBytes.ToString("N0", CultureInfo.CurrentCulture))
                : Loc.Format("Carve_Problem_Write", info.Folder, info.Message);
            ContentDialog dialog = DialogParts.Dialog(Root, "CarveProblemDialog", Loc.Get("Carve_Problem_Title"), new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
                Loc.Get("Carve_Problem_Retry"), Loc.Get("Carve_Problem_Change"));
            dialog.CloseButtonText = Loc.Get("Carve_Problem_Stop");
            dialog.DefaultButton = ContentDialogButton.Close;
            ContentDialogResult result = await ShowEditDialogAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                completion.TrySetResult(new OutputDecision(OutputDecisionKind.Retry));
            }
            else if (result == ContentDialogResult.Secondary && await PickFolderToolAsync("HexEditor.CarveOutput") is { } folder)
            {
                completion.TrySetResult(new OutputDecision(OutputDecisionKind.ChangeFolder, folder));
            }
            else
            {
                completion.TrySetResult(new OutputDecision(OutputDecisionKind.Abort));
            }
        });
        return completion.Task;
    }

    // ---- Hex ビューの右クリックメニュー (FOR-19 の「呼び出し」: 選択範囲があるとき) ----

    private void ExtendHexViewCarvingMenu(MenuFlyout menu)
    {
        const string Id = "HexViewMenu_CarveSelection";
        if (menu.Items.FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == Id) is not MenuFlyoutItem item)
        {
            item = new MenuFlyoutItem { Text = Loc.Get("HexViewMenu_CarveSelection"), Tag = "Command" };
            AutomationProperties.SetAutomationId(item, Id);
            item.Click += (_, _) => _ = ShowCarvingDialogAsync(fromSelection: true);
            menu.Items.Add(item);
        }

        // 選択範囲があるときだけ出す。使えないとき (カービングの実行中、監査ログに書けないときなど) は無効にして理由をツールチップに出す。
        CommandState state = Commands.StateOf("forensics.carving.run");
        item.Visibility = Vm.Selected?.Editor.HasSelection == true ? Visibility.Visible : Visibility.Collapsed;
        item.IsEnabled = state.Enabled;
        ToolTipService.SetToolTip(item, state.Enabled ? null : state.Reason);
        AutomationProperties.SetHelpText(item, state.Enabled ? string.Empty : state.Reason ?? string.Empty);
    }

    /// <summary>結果の行を選んだ: 開始位置へジャンプして範囲を選択する (FOR-19 の仕様 5)。</summary>
    internal void JumpToCarved(CarvedFile file)
    {
        if (ForensicsService.CarvingTarget is DocumentViewModel target && Vm.Documents.Contains(target))
        {
            if (Vm.Selected != target)
            {
                Vm.Selected = target;
            }

            target.Editor.Select(file.Offset, Math.Min(file.Length, Math.Max(0, target.Document.Length - file.Offset)));
        }
    }

    /// <summary>見つけたファイルの内容 (縮小表示用。大きすぎれば null)。読み込みは UI のスレッドの外で行う (ディスクの読み込みで UI を止めない)。</summary>
    internal async Task<byte[]?> ReadCarvedBytesAsync(CarvedFile file)
    {
        if (ForensicsService.CarvingTarget is not DocumentViewModel target || !Vm.Documents.Contains(target) || file.Length > int.MaxValue / 2)
        {
            return null;
        }

        Core.Engine.DocumentSnapshot snapshot = target.Document.DataSnapshot;
        return await Task.Run(() =>
        {
            byte[] bytes = new byte[file.Length];
            Core.Sources.ReadResult r = snapshot.Read(file.Offset, bytes);
            return r.Unreadable.Count == 0 && r.BytesReturned == bytes.Length ? bytes : null;
        });
    }

    internal void OpenCarvingFolder()
    {
        if (ForensicsService.LastCarving?.OutputFolder is { } folder && Directory.Exists(folder))
        {
            _ = Windows.System.Launcher.LaunchFolderPathAsync(folder);
        }
    }
}
