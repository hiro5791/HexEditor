using System.Collections.Specialized;
using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Operations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// フォレンジックのケース (12 の FOR-03)・監査ログ (FOR-04) の組み込み: コマンド、パネル、タイトルとステータスバーのケース名、ドキュメントの
/// 開く・閉じるの記録、監査ログに書けないときの閉じられない InfoBar。ケースと監査ログはアプリ全体で 1 つ (<see cref="ForensicsService"/>)。
/// </summary>
public sealed partial class MainWindow
{
    public const string EvidencePanelId = "evidence";
    public const string AuditLogPanelId = "auditLog";
    public const string CarvingPanelId = "carving";

    private EvidencePanel? _evidencePanel;
    private AuditLogPanel? _auditPanel;
    private CarvingPanel? _carvingPanel;

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterForensicsPanels()
    {
        if (PanelRegistry.Find(EvidencePanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(EvidencePanelId, "Panel_Evidence_Title", Core.Panels.PanelDock.Left,
                ctx => ((MainWindow)ctx.Window)._evidencePanel = new EvidencePanel((MainWindow)ctx.Window)) { RequiresDocument = false, CanFloat = false });
        }

        if (PanelRegistry.Find(AuditLogPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(AuditLogPanelId, "Panel_AuditLog_Title", Core.Panels.PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window)._auditPanel = new AuditLogPanel((MainWindow)ctx.Window)) { RequiresDocument = false, CanFloat = false });
        }

        if (PanelRegistry.Find(CarvingPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(CarvingPanelId, "Panel_Carving_Title", Core.Panels.PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window)._carvingPanel = new CarvingPanel((MainWindow)ctx.Window)) { RequiresDocument = false, CanFloat = false });
        }
    }

    private void InitializeForensics()
    {
        Commands.Register("forensics.case.new", () => NewCaseAsync(), () => NoCaseOpen());
        Commands.Register("forensics.case.open", () => OpenCaseAsync(), () => NoCaseOpen());
        Commands.Register("forensics.case.close", () => CloseCase(), NeedsCase);
        Commands.Register("forensics.case.addEvidence", () => AddEvidenceAsync(), AddEvidenceState);
        Commands.Register("forensics.case.properties", () => CasePropertiesAsync(), NeedsCase);
        Commands.Register("forensics.audit.show", () => ShowPanel(AuditLogPanelId));
        Commands.Register("forensics.audit.verify", () => VerifyAuditAsync(), NeedsCase);
        RegisterCarvingCommands();
        RegisterReportCommands();

        EventHandler caseChanged = (_, _) => DispatcherQueue.TryEnqueue(OnCaseStateChanged);
        EventHandler<AuditFaultEventArgs> faultChanged = (_, e) => DispatcherQueue.TryEnqueue(() => OnAuditFault(e));
        ForensicsService.CaseChanged += caseChanged;
        ForensicsService.EvidenceChanged += caseChanged;
        ForensicsService.TimeDisplayChanged += caseChanged;
        ForensicsService.Audit.FaultChanged += faultChanged;
        Vm.Documents.CollectionChanged += ForensicsDocumentsChanged;
        Closed += (_, _) =>
        {
            ForensicsService.CaseChanged -= caseChanged;
            ForensicsService.EvidenceChanged -= caseChanged;
            ForensicsService.TimeDisplayChanged -= caseChanged;
            ForensicsService.Audit.FaultChanged -= faultChanged;
            Vm.Documents.CollectionChanged -= ForensicsDocumentsChanged;
        };
        ForensicsService.RegisterWriteCommands("file.save", "file.saveAs", "file.saveAll", "tools.files.shred", "tools.ads", "forensics.carving.run", "forensics.report.create");
        CommandHost.WriteGate = ForensicsService.WriteGateReason;
        AuditFaultBar.ActionButton = new Button { Content = Loc.Get("Audit_Fault_Retry") };
        AuditFaultBar.ActionButton.Click += (_, _) => RetryAuditLog();
        OnCaseStateChanged();
    }

    private CommandState NoCaseOpen() =>
        ForensicsService.Cases.CurrentCase is null ? CommandState.Available : CommandState.Unavailable(Loc.Get("Forensics_CaseAlreadyOpen"));

    private CommandState NeedsCase() =>
        ForensicsService.Cases.CurrentCase is not null ? CommandState.Available : CommandState.Unavailable(Loc.Get("Forensics_NoCase"));

    private CommandState AddEvidenceState() =>
        NeedsCase() is { Enabled: false } state ? state
        : Vm.Selected is not { } doc ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
        : EvidenceRegistrationOf(doc) is null ? CommandState.Unavailable(Loc.Get("Evidence_NotRegistrable"))
        : CommandState.Available;

    /// <summary>ケースの状態が変わった: タイトル・ステータスバー・コマンドの有効状態を更新する。</summary>
    private void OnCaseStateChanged()
    {
        UpdateTitle();
        UpdateStatusBarLayout();
        Commands.NotifyStatesChanged();
    }

    /// <summary>監査ログに書けなくなった・戻った (FOR-04 の「エラー」): 閉じられない InfoBar を出し続け、書き込みを伴う操作を無効にする。</summary>
    private void OnAuditFault(AuditFaultEventArgs e)
    {
        AuditFaultBar.Message = e.Faulted ? Loc.Format("Audit_Fault_Message", e.Message) : string.Empty;
        AuditFaultBar.IsOpen = e.Faulted;
        Commands.NotifyStatesChanged();
    }

    private void RetryAuditLog()
    {
        if (!ForensicsService.Audit.TryRecover())
        {
            ShowNotice(Loc.Get("Audit_Fault_StillFailing"), InfoBarSeverity.Error);
        }
    }

    // ---- ドキュメントの開く・閉じるの記録 (FOR-04 の仕様 3) ----

    private void ForensicsDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ForensicsService.Cases.CurrentCase is null)
        {
            return;
        }

        foreach (DocumentViewModel d in (e.NewItems ?? Array.Empty<object>()).OfType<DocumentViewModel>())
        {
            RecordDocument(AuditOperations.DocumentOpen, d);
        }

        foreach (DocumentViewModel d in (e.OldItems ?? Array.Empty<object>()).OfType<DocumentViewModel>())
        {
            RecordDocument(AuditOperations.DocumentClose, d);
        }
    }

    private static void RecordDocument(string operation, DocumentViewModel d)
    {
        string? path = d.Device?.Path ?? d.FilePath;
        if (path is null)
        {
            return;
        }

        EvidenceItem? evidence = ForensicsService.Cases.CurrentCase?.Evidence.FirstOrDefault(e => ForensicCase.SourceKey(e.Kind, e.Path) == ForensicCase.SourceKey(KindOf(d), path));
        ForensicsService.Audit.Record(new AuditRecord(operation)
        {
            EvidenceId = evidence?.Id,
            Path = path,
            Parameters = AuditRecord.Args(("readOnly", d.Document.IsReadOnly ? "yes" : "no")),
        });
    }

    // ---- ケースの作成・オープン・クローズ (FOR-03 の仕様 1〜4) ----

    private async Task NewCaseAsync()
    {
        var number = DialogParts.Field("Case_Number", Loc.Get("Case_Number"), string.Empty, monospace: false);
        var name = DialogParts.Field("Case_Name", Loc.Get("Case_Name"), string.Empty, monospace: false);
        var investigator = DialogParts.Field("Case_Investigator", Loc.Get("Case_Investigator"), Environment.UserName, monospace: false);
        var organization = DialogParts.Field("Case_Organization", Loc.Get("Case_Organization"), string.Empty, monospace: false);
        var description = DialogParts.Field("Case_Description", Loc.Get("Case_Description"), string.Empty, monospace: false);
        description.AcceptsReturn = true;
        description.TextWrapping = TextWrapping.Wrap;
        description.MinHeight = 60;
        var folder = DialogParts.Field("Case_Folder", Loc.Get("Case_Folder"), AppState.GetString("forensics.case.lastFolder", string.Empty));
        var browse = new Button { Content = Loc.Get("Case_Browse") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(browse, "Case_Browse");
        browse.Click += async (_, _) =>
        {
            if (await PickFolderToolAsync("HexEditor.CaseFolder") is { } picked)
            {
                folder.Text = picked;
            }
        };
        TimeZoneInfo[] zones = [.. TimeZoneInfo.GetSystemTimeZones()];
        var zone = DialogParts.Combo("Case_TimeZone", Loc.Get("Case_TimeZone"), zones.Select(z => z.DisplayName), Math.Max(0, Array.FindIndex(zones, z => z.Id == TimeZoneInfo.Local.Id)));
        var error = DialogParts.Caption("Case_Error");
        error.Foreground = Controls.Tools.ToolParts.ThemeBrush("SystemFillColorCriticalBrush");
        error.TextWrapping = TextWrapping.Wrap;
        var note = DialogParts.Caption("Case_ZoneNote");
        note.Text = Loc.Get("Case_TimeZoneNote");
        note.TextWrapping = TextWrapping.Wrap;

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement element in new UIElement[] { number, name, investigator, organization, description, folder, browse, zone, note, error })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "CaseNewDialog", Loc.Get("Case_New_Title"), new ScrollViewer { Content = body, MaxHeight = 620 }, Loc.Get("Case_Create"));
        void Update() => dialog.IsPrimaryButtonEnabled = number.Text.Trim().Length > 0 && name.Text.Trim().Length > 0 && folder.Text.Trim().Length > 0;
        foreach (TextBox box in new[] { number, name, folder })
        {
            box.TextChanged += (_, _) => Update();
        }

        Update();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            // 作れなければダイアログを閉じず、理由を示す (ケースフォルダに書き込めない: ケースを作らない。FOR-03 の「エラー」)。
            try
            {
                var info = new CaseInfo
                {
                    Number = number.Text.Trim(),
                    Name = name.Text.Trim(),
                    Investigator = investigator.Text.Trim(),
                    Organization = organization.Text.Trim(),
                    Description = description.Text.Trim(),
                    TimeZoneId = zones[Math.Max(0, zone.SelectedIndex)].Id,
                };
                ForensicsService.Cases.Create(folder.Text.Trim(), info);
                AppState.SetString("forensics.case.lastFolder", folder.Text.Trim());
            }
            catch (Exception ex) when (ex is CaseFolderException or CaseValidationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                args.Cancel = true;
                error.Text = Loc.Format("Case_Error_Create", ex is CaseValidationException ? Loc.Get("Case_Error_Required") : ex.Message);
            }
        };

        if (await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary && ForensicsService.Cases.CurrentCase is not null)
        {
            OnCaseOpened(created: true);
        }
    }

    private async Task OpenCaseAsync()
    {
        string? folder = await PickFolderToolAsync("HexEditor.CaseOpen");
        if (folder is null)
        {
            return;
        }

        OpenCaseFolder(folder);
    }

    /// <summary>フォルダのケースを開く (壊れた <c>case.json</c> は行番号付きで示して開かない)。</summary>
    internal bool OpenCaseFolder(string folder)
    {
        try
        {
            ForensicsService.Cases.Open(folder);
        }
        catch (CaseFileException ex)
        {
            ShowNotice(Loc.Format("Case_Error_File", ex.Line, ex.Detail), InfoBarSeverity.Error);
            return false;
        }
        catch (Exception ex) when (ex is CaseFolderException or IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Case_Error_Open", ex.Message), InfoBarSeverity.Error);
            return false;
        }

        OnCaseOpened(created: false);
        return true;
    }

    /// <summary>ケースを開いた後: 証拠パネルを出し、書き込み保護モードを有効にする (設定) か提案する (FOR-03 の仕様 4)。</summary>
    private void OnCaseOpened(bool created)
    {
        ShowPanel(EvidencePanelId, focus: false);
        ShowNotice(Loc.Format(created ? "Case_Created" : "Case_Opened", ForensicsService.Cases.CurrentCase!.Info.Name), InfoBarSeverity.Success);
        if (ForensicsService.IsWriteBlockEnabled())
        {
            return;
        }

        if (App.Settings.GetBool("forensics.case.autoWriteBlock", true))
        {
            ForensicsService.EnableWriteBlock();
            ShowNotice(Loc.Get("Case_WriteBlockEnabled"), InfoBarSeverity.Informational);
        }
        else
        {
            ShowNotice(Loc.Get("Case_WriteBlockSuggest"), InfoBarSeverity.Warning, actions:
            [
                new Core.Notifications.NotificationAction(Loc.Get("Case_WriteBlockEnable"), () =>
                {
                    ForensicsService.EnableWriteBlock();
                    Commands.NotifyStatesChanged();
                }),
            ]);
        }
    }

    private void CloseCase()
    {
        ForensicsService.Cases.Close();
        ForensicsService.SetCarving(null, null);
    }

    private async Task CasePropertiesAsync()
    {
        if (ForensicsService.Cases.CurrentCase is not { } current)
        {
            return;
        }

        CaseInfo info = current.Info;
        var number = DialogParts.Field("Case_Number", Loc.Get("Case_Number"), info.Number, monospace: false);
        var name = DialogParts.Field("Case_Name", Loc.Get("Case_Name"), info.Name, monospace: false);
        var investigator = DialogParts.Field("Case_Investigator", Loc.Get("Case_Investigator"), info.Investigator, monospace: false);
        var organization = DialogParts.Field("Case_Organization", Loc.Get("Case_Organization"), info.Organization, monospace: false);
        var description = DialogParts.Field("Case_Description", Loc.Get("Case_Description"), info.Description, monospace: false);
        description.AcceptsReturn = true;
        description.TextWrapping = TextWrapping.Wrap;
        description.MinHeight = 60;
        TimeZoneInfo[] zones = [.. TimeZoneInfo.GetSystemTimeZones()];
        var zone = DialogParts.Combo("Case_TimeZone", Loc.Get("Case_TimeZone"), zones.Select(z => z.DisplayName),
            Math.Max(0, Array.FindIndex(zones, z => z.Id == current.TimeZone.Id)));
        var note = DialogParts.Caption("Case_ZoneNote");
        note.Text = Loc.Get("Case_TimeZoneNote");
        note.TextWrapping = TextWrapping.Wrap;
        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement element in new UIElement[] { number, name, investigator, organization, description, zone, note })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "CasePropertiesDialog", Loc.Get("Case_Properties_Title"), new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Common_Save"));
        void Update() => dialog.IsPrimaryButtonEnabled = number.Text.Trim().Length > 0 && name.Text.Trim().Length > 0;
        number.TextChanged += (_, _) => Update();
        name.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        SetCaseInfo(info with
        {
            Number = number.Text.Trim(),
            Name = name.Text.Trim(),
            Investigator = investigator.Text.Trim(),
            Organization = organization.Text.Trim(),
            Description = description.Text.Trim(),
            TimeZoneId = zones[Math.Max(0, zone.SelectedIndex)].Id,
        });
    }

    /// <summary>ケース情報を変える (タイムゾーンを変えると、FAT などの日時の UTC と表示が変わる)。変更は監査ログに記録する。</summary>
    internal void SetCaseInfo(CaseInfo updated)
    {
        if (ForensicsService.Cases.CurrentCase is not { } current)
        {
            return;
        }

        CaseInfo before = current.Info;
        try
        {
            current.UpdateInfo(updated);
        }
        catch (Exception ex) when (ex is IOException or CaseValidationException)
        {
            ShowNotice(Loc.Format("Case_Error_Save", ex.Message), InfoBarSeverity.Error);
            return;
        }

        ForensicsService.Audit.Record(new AuditRecord(AuditOperations.SettingChange)
        {
            Path = current.Folder,
            Message = "Case properties changed.",
            Parameters = AuditRecord.Args(("timezone", $"{before.TimeZoneId} -> {updated.TimeZoneId}"), ("name", updated.Name)),
        });
        ForensicsService.NotifyTimeZoneChanged();
    }

    // ---- 証拠の追加・除外・オープン (FOR-03 の仕様 5〜7) ----

    private static EvidenceKind KindOf(DocumentViewModel d)
    {
        if (d.Device is { } device)
        {
            return device.Info.Disk is not null ? EvidenceKind.PhysicalDisk : EvidenceKind.Volume;
        }

        return IsImageExtension(d.FilePath) ? EvidenceKind.ImageFile : EvidenceKind.File;
    }

    private static bool IsImageExtension(string? path) =>
        Path.GetExtension(path ?? string.Empty).ToLowerInvariant() is ".dd" or ".img" or ".raw" or ".bin" or ".001" or ".e01" or ".vhd" or ".vhdx" or ".vmdk" or ".iso" or ".dmg" or ".aff";

    /// <summary>開いているドキュメントを証拠として登録する内容にする。ディスク・ボリューム・ファイル・イメージだけ登録できる。</summary>
    internal EvidenceRegistration? EvidenceRegistrationOf(DocumentViewModel d)
    {
        if (d.Device is { } device)
        {
            Core.Devices.DeviceOpenInfo info = device.Info;
            long capacity = info.Disk?.Size ?? info.Volume?.Size ?? device.Length;
            int sector = device.LogicalSectorSize;
            return new EvidenceRegistration
            {
                Kind = KindOf(d),
                Path = info.Path,
                Description = info.DisplayName,
                Model = info.Disk?.Model,
                Serial = info.SerialNumber ?? info.Disk?.SerialNumber,
                CapacityBytes = capacity,
                SectorSize = sector,
                SectorCount = sector > 0 ? capacity / sector : null,
            };
        }

        if (d.FilePath is { } path && File.Exists(path) && !d.IsDerived)
        {
            return new EvidenceRegistration
            {
                Kind = KindOf(d),
                Path = path,
                Description = d.DisplayName,
                CapacityBytes = d.Document.Length,
            };
        }

        return null;
    }

    private async Task AddEvidenceAsync()
    {
        if (Vm.Selected is not { } doc || EvidenceRegistrationOf(doc) is not { } registration || ForensicsService.Cases.CurrentCase is not { } current)
        {
            return;
        }

        if (current.FindBySource(registration.Kind, registration.Path) is { } existing)
        {
            ShowNotice(Loc.Format("Evidence_Duplicate", existing.Id), InfoBarSeverity.Error);
            return;
        }

        var description = DialogParts.Field("Evidence_Description", Loc.Get("Evidence_Description"), registration.Description, monospace: false);
        var compute = DialogParts.Check("Evidence_Compute", Loc.Get("Evidence_Compute"), true);
        var checks = EvidenceHasher.Supported.ToDictionary(a => a, a => DialogParts.Check("Evidence_Hash_" + a, a, EvidenceHasher.DefaultAlgorithms.Contains(a)));
        var info = DialogParts.Caption("Evidence_Info");
        info.Text = Loc.Format("Evidence_AddInfo", registration.Path);
        info.TextWrapping = TextWrapping.Wrap;
        var body = new StackPanel { Spacing = 8, MinWidth = 440 };
        body.Children.Add(info);
        body.Children.Add(description);
        body.Children.Add(compute);
        foreach (CheckBox c in checks.Values)
        {
            c.Margin = new Thickness(24, 0, 0, 0);
            body.Children.Add(c);
        }

        compute.Checked += (_, _) => Array.ForEach([.. checks.Values], c => c.IsEnabled = true);
        compute.Unchecked += (_, _) => Array.ForEach([.. checks.Values], c => c.IsEnabled = false);
        ContentDialog dialog = DialogParts.Dialog(Root, "EvidenceAddDialog", Loc.Get("Evidence_Add_Title"), new ScrollViewer { Content = body, MaxHeight = 500 }, Loc.Get("Evidence_Add"));
        void Update() => dialog.IsPrimaryButtonEnabled = compute.IsChecked != true || checks.Values.Any(c => c.IsChecked == true);
        compute.Checked += (_, _) => Update();
        compute.Unchecked += (_, _) => Update();
        foreach (CheckBox c in checks.Values)
        {
            c.Checked += (_, _) => Update();
            c.Unchecked += (_, _) => Update();
        }

        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string[] algorithms = compute.IsChecked == true ? [.. checks.Where(c => c.Value.IsChecked == true).Select(c => c.Key)] : [];
        await AddEvidenceAsync(doc, registration with { Description = description.Text.Trim() }, algorithms);
    }

    /// <summary>証拠を登録し、指定されたアルゴリズムでハッシュを計算する (長時間処理。中止したら「ハッシュ未計算」のまま登録しておく)。</summary>
    internal async Task<EvidenceItem?> AddEvidenceAsync(DocumentViewModel doc, EvidenceRegistration registration, IReadOnlyList<string> algorithms)
    {
        EvidenceItem? item;
        try
        {
            item = ForensicsService.Cases.Register(registration);
        }
        catch (DuplicateEvidenceException ex)
        {
            ShowNotice(Loc.Format("Evidence_Duplicate", ex.ExistingId), InfoBarSeverity.Error);
            return null;
        }
        catch (AuditWriteException)
        {
            ShowNotice(Loc.Get("Audit_Fault_Blocked"), InfoBarSeverity.Error);
            return null;
        }
        catch (IOException ex)
        {
            ShowNotice(Loc.Format("Case_Error_Save", ex.Message), InfoBarSeverity.Error);
            return null;
        }

        if (item is null)
        {
            return null;
        }

        ShowPanel(EvidencePanelId, focus: false);
        if (algorithms.Count > 0)
        {
            await ComputeEvidenceHashAsync(item, doc, algorithms);
        }

        return ForensicsService.Cases.CurrentCase?.Find(item.Id);
    }

    private async Task ComputeEvidenceHashAsync(EvidenceItem item, DocumentViewModel doc, IReadOnlyList<string> algorithms)
    {
        Core.Engine.DocumentSnapshot snapshot = doc.Document.DataSnapshot;
        try
        {
            IReadOnlyList<EvidenceHash> hashes = await Vm.Operations.RunAsync(Loc.Format("Evidence_HashOperation", item.Id), OperationKind.ReadOnly, doc.Document, snapshot.Raw.Length,
                op => Task.Run(() => EvidenceHasher.Compute(snapshot, algorithms, new OperationProgress(op), op.CancellationToken)));
            ForensicsService.Cases.RecordHashes(item.Id, hashes);
            ShowNotice(Loc.Format("Evidence_HashDone", item.Id), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ForensicsService.Cases.RecordHashCancelled(item.Id);
            ShowNotice(Loc.Format("Evidence_HashCancelled", item.Id), InfoBarSeverity.Warning);
        }
        catch (Core.Hashing.HashReadException ex)
        {
            ForensicsService.Cases.RecordHashCancelled(item.Id);
            ShowNotice(Loc.Format("Evidence_HashUnreadable", item.Id, ex.Range.Offset.ToString("X", CultureInfo.InvariantCulture)), InfoBarSeverity.Error);
        }
    }

    private sealed class OperationProgress(LongRunningOperation op) : IProgress<long>
    {
        public void Report(long value) => op.Report(value);
    }

    /// <summary>証拠を一覧から外す (データソースには何もしない。FOR-03 の仕様 7)。</summary>
    internal void ExcludeEvidence(EvidenceItem item)
    {
        try
        {
            if (ForensicsService.Cases.Exclude(item.Id))
            {
                ShowNotice(Loc.Format("Evidence_Removed", item.Id), InfoBarSeverity.Informational);
            }
        }
        catch (AuditWriteException)
        {
            ShowNotice(Loc.Get("Audit_Fault_Blocked"), InfoBarSeverity.Error);
        }
    }

    /// <summary>証拠のデータソースを読み取り専用で開く (FOR-03 の仕様 6: 書き込み保護モードで開く)。</summary>
    internal async Task OpenEvidenceAsync(EvidenceItem item)
    {
        try
        {
            if (item.IsDevice)
            {
                Core.Devices.DeviceCatalog catalog = DeviceService.EnumerateDevices();
                DeviceListItem? entry = DeviceItems(catalog).FirstOrDefault(i => string.Equals(i.CreateInfo().Path, item.Path, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    ShowNotice(Loc.Format("Evidence_NotPresent", item.Path), InfoBarSeverity.Error);
                    return;
                }

                await OpenDeviceItemAsync(entry, readOnly: true);
                return;
            }

            if (!File.Exists(item.Path))
            {
                ShowNotice(Loc.Format("Evidence_NotPresent", item.Path), InfoBarSeverity.Error);
                return;
            }

            if (Vm.Documents.FirstOrDefault(d => string.Equals(d.FilePath, item.Path, StringComparison.OrdinalIgnoreCase)) is { } open)
            {
                Vm.Selected = open;
                return;
            }

            Vm.Selected = Vm.Open(item.Path, readOnly: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Devices.DeviceException)
        {
            ShowNotice(Loc.Format("Evidence_OpenFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---- 監査ログの検証・エクスポート (FOR-04 の仕様 6・7) ----

    private async Task VerifyAuditAsync()
    {
        if (ForensicsService.Cases.CurrentCase is not { } current)
        {
            return;
        }

        string path = current.AuditPath;
        ShowPanel(AuditLogPanelId, focus: false);
        AuditVerifyResult result;
        try
        {
            result = await Vm.Operations.RunAsync(Loc.Get("Audit_VerifyOperation"), OperationKind.ReadOnly, null, new FileInfo(path).Length,
                op => Task.Run(() => AuditVerifier.Verify(path, new LineProgress(op, new FileInfo(path).Length), op.CancellationToken)));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException ex)
        {
            ShowNotice(Loc.Format("Audit_VerifyFailed", ex.Message), InfoBarSeverity.Error);
            return;
        }

        _auditPanel?.ShowVerifyResult(result);
        ShowNotice(result.IsIntact ? Loc.Format("AuditLog_VerifyOk", result.LinesChecked) : Loc.Format("AuditLog_VerifyBroken", string.Join(", ", result.Breaks.Take(20).Select(b => b.Line))),
            result.IsIntact ? InfoBarSeverity.Success : InfoBarSeverity.Error);

        // 連鎖の切れが見つかった事実自体を新しい行として記録する (仕様の「エラー」)。
        ForensicsService.Audit.Record(new AuditRecord(AuditOperations.AuditVerify)
        {
            Path = path,
            Result = result.IsIntact ? AuditResult.Success : AuditResult.Failure,
            Message = result.IsIntact ? $"Verified {result.LinesChecked} lines." : $"The chain is broken at line {string.Join(", ", result.Breaks.Take(20).Select(b => b.Line))}.",
            Parameters = AuditRecord.Args(("lines", result.LinesChecked.ToString(CultureInfo.InvariantCulture)), ("broken", result.Breaks.Count.ToString(CultureInfo.InvariantCulture))),
        });
        _lastAuditVerify = result;
    }

    private AuditVerifyResult? _lastAuditVerify;

    private sealed class LineProgress(LongRunningOperation op, long bytes) : IProgress<long>
    {
        public void Report(long lines) => op.Report(Math.Min(bytes, lines * 200));
    }

    internal async Task ExportAuditAsync(bool json)
    {
        if (ForensicsService.Cases.CurrentCase is not { } current)
        {
            return;
        }

        string suggested = $"audit-{current.Info.Number}.{(json ? "json" : "csv")}";
        string? target = await PickForensicsSavePathAsync(suggested, "HexEditor.AuditExport");
        if (target is null)
        {
            return;
        }

        Core.Forensics.Time.ForensicTimeContext time = ForensicsService.TimeContext;
        string invalid = Loc.Get("Time_Invalid");
        string source = current.AuditPath;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("Audit_ExportOperation"), OperationKind.WritesExternal, null, null, op => Task.Run(() =>
            {
                using var reader = new AuditLogReader(source);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
                if (json)
                {
                    AuditExporter.WriteJson(output, reader.Enumerate(), time);
                }
                else
                {
                    using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(true));
                    AuditExporter.WriteCsv(writer, reader.Enumerate(), time, invalid);
                }
            }));
            ForensicsService.Audit.Record(new AuditRecord(AuditOperations.FileExport) { Path = target, Message = "Audit log exported." });
            ShowNotice(Loc.Format("Audit_Exported", Path.GetFileName(target)), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Audit_ExportFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>名前を付けて保存の選択 (テストでは代わりの結果を返す)。</summary>
    private async Task<string?> PickForensicsSavePathAsync(string suggestedName, string identifier)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = identifier };
        picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [Path.GetExtension(suggestedName)]);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
