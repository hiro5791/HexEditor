using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Expressions;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>ディスクのクローン (12 の FOR-24) とセクタ範囲の保存と復元 (FOR-25)。書き込みは偽のデバイスを含むデバイスの経路だけを使う。</summary>
public sealed partial class MainWindow
{
    /// <summary>クローンのダイアログの入力。</summary>
    internal sealed record CloneRequest(DocumentViewModel Source, bool SelectionOnly, DiskInfo Target, long StartLba, CloneOptions Options);

    /// <summary>最後のクローンの結果 (テスト用)。</summary>
    internal CloneResult? LastClone { get; private set; }

    /// <summary>テスト用: 強い確認のダイアログの操作 (入力欄に名前を入れて押す・押せるかを見る)。</summary>
    internal Func<ContentDialog, Task>? ImgStrongConfirmInteraction { get; set; }

    /// <summary>強い確認での対象の表示名 (例: <c>ディスク 2</c>)。</summary>
    internal static string ImgDiskName(int number) => Loc.Format("ImgClone_DiskName", number.ToString(CultureInfo.InvariantCulture));

    private async Task CloneDiskAsync()
    {
        if (ImageForensics.Hooks.IsWriteBlocked)
        {
            ShowNotice(Loc.Get("WriteBlock_Enabled"), InfoBarSeverity.Warning);
            return;
        }

        if (Vm.Selected is not { } doc)
        {
            ShowNotice(Loc.Get("Img_NeedsSource"), InfoBarSeverity.Informational);
            return;
        }

        DeviceCatalog catalog;
        try
        {
            catalog = DeviceService.EnumerateDevices();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice(Loc.Format("OpenDisk_EnumError", ex.Message), InfoBarSeverity.Error);
            return;
        }

        if (await ShowCloneDialogAsync(doc, catalog) is { } request)
        {
            await RunCloneAsync(request, catalog);
        }
    }

    /// <summary>クローンのダイアログ (FOR-24 の「画面」): 入力元、出力先のディスク一覧 (番号、モデル、シリアル、容量、セクタサイズ、ボリューム)、オプション。</summary>
    private async Task<CloneRequest?> ShowCloneDialogAsync(DocumentViewModel doc, DeviceCatalog catalog)
    {
        AcquisitionSourceInfo info = ImgSourceInfoOf(doc);
        var disks = catalog.Disks.Where(d => d.Number != info.DiskNumber).ToList();
        var panel = new StackPanel { Spacing = 8, MinWidth = 480 };
        panel.Children.Add(ImgNote("ImgClone_Source", Loc.Format("Img_SourceLabel", doc.DisplayName)));
        CheckBox selectionOnly = DialogParts.Check("ImgClone_SelectionOnly", Loc.Get("Img_SelectionOnly"), false);
        selectionOnly.IsEnabled = doc.Editor.SelectionLength > 0;
        panel.Children.Add(selectionOnly);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 240, MinWidth = 460 };
        AutomationProperties.SetAutomationId(list, "ImgClone_Targets");
        AutomationProperties.SetName(list, Loc.Get("ImgClone_Target"));
        list.ItemsSource = disks.Select(d => ImgDiskLabel(d, catalog)).ToList();
        panel.Children.Add(ImgSection(Loc.Get("ImgClone_Target"), list));
        TextBox startLba = DialogParts.Field("ImgClone_StartLba", Loc.Get("ImgClone_StartLba"), "0");
        TextBox retries = DialogParts.Field("ImgClone_Retries", Loc.Get("Img_Retries"), "3");
        CheckBox zeroFill = DialogParts.Check("ImgClone_ZeroFill", Loc.Get("ImgClone_ZeroFill"), false);
        CheckBox verify = DialogParts.Check("ImgClone_Verify", Loc.Get("Img_VerifyAfter"), true);
        ComboBox hash = DialogParts.Combo("ImgClone_Hash", Loc.Get("ImgClone_Hash"), ["MD5", "SHA-1", "SHA-256"], 2);
        TextBox logFolder = DialogParts.Field("ImgClone_LogFolder", Loc.Get("ImgClone_LogFolder"),
            ImageForensics.Hooks.Case?.Folder is { } folder ? Path.Combine(folder, "reports") : string.Empty, monospace: false);
        foreach (FrameworkElement e in new FrameworkElement[] { startLba, retries, zeroFill, verify, hash, logFolder })
        {
            panel.Children.Add(e);
        }

        TextBlock error = DialogParts.Caption("ImgClone_Error");
        panel.Children.Add(error);
        ContentDialog dialog = DialogParts.Dialog(Root, "ImgCloneDialog", Loc.Get("ImgClone_Title"), new ScrollViewer { Content = panel, MaxHeight = 560 },
            Loc.Get("ImgClone_Next"));
        void Validate()
        {
            string? problem = null;
            if (list.SelectedIndex < 0)
            {
                problem = Loc.Get("ImgClone_ChooseTarget");
            }
            else if (!long.TryParse(startLba.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long lba))
            {
                problem = Loc.Get("ImgClone_StartInvalid");
            }
            else if ((disks[list.SelectedIndex].LogicalSectorSize ?? 512) != info.SectorSize)
            {
                problem = Loc.Get("ImgClone_SectorSizeMismatch");
            }
            else if (disks[list.SelectedIndex].Size is long size && lba * (disks[list.SelectedIndex].LogicalSectorSize ?? 512) >= size)
            {
                problem = Loc.Get("ImgClone_StartInvalid");
            }
            else if (!int.TryParse(retries.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                problem = Loc.Get("Img_RetriesInvalid");
            }

            error.Text = problem ?? string.Empty;
            dialog.IsPrimaryButtonEnabled = problem is null;
        }

        list.SelectionChanged += (_, _) => Validate();
        startLba.TextChanged += (_, _) => Validate();
        retries.TextChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0)
        {
            return null;
        }

        DiskInfo target = disks[list.SelectedIndex];
        string? log = logFolder.Text.Trim().Length > 0
            ? Path.Combine(logFolder.Text.Trim(), $"clone-{ImgDiskFileName(target)}-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.txt")
            : null;
        return new CloneRequest(doc, selectionOnly.IsChecked == true, target, long.Parse(startLba.Text.Trim(), CultureInfo.InvariantCulture), new CloneOptions
        {
            Retries = int.Parse(retries.Text, CultureInfo.InvariantCulture),
            ZeroFillRest = zeroFill.IsChecked == true,
            Verify = verify.IsChecked == true,
            Hash = hash.SelectedIndex switch { 0 => ImageHashes.Md5, 1 => ImageHashes.Sha1, _ => ImageHashes.Sha256 },
            LogPath = log,
            LogText = ImgLogText(),
            AppVersion = ImageAppVersion,
        });
    }

    private static string ImgDiskFileName(DiskInfo d) => "disk" + d.Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>出力先の一覧の 1 行 (番号、モデル、シリアル、容量、セクタサイズ、ボリューム)。</summary>
    private static string ImgDiskLabel(DiskInfo d, DeviceCatalog catalog)
    {
        string volumes = string.Join(", ", catalog.Volumes.Where(v => v.DiskNumber == d.Number).Select(ImgVolumeText));
        return Loc.Format("ImgClone_DiskLine", d.Number.ToString(CultureInfo.InvariantCulture), d.Model ?? Loc.Get("Common_Unknown"),
            d.SerialNumber ?? Loc.Get("Common_Unknown"), (d.Size ?? 0).ToString("N0", CultureInfo.CurrentCulture),
            (d.LogicalSectorSize ?? 512).ToString(CultureInfo.InvariantCulture), volumes.Length > 0 ? volumes : Loc.Get("ImgClone_NoVolumes"));
    }

    private static string ImgVolumeText(VolumeDeviceInfo v) => Loc.Format("ImgClone_VolumeLine", v.Name, string.IsNullOrEmpty(v.Label) ? Loc.Get("OpenDisk_NoLabel") : v.Label,
        v.FileSystem ?? Loc.Get("Common_Unknown"), (v.Size ?? 0).ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>
    /// クローンを実行する (FOR-24 の仕様 2〜7): 強い確認 → 出力先を書き込みのアクセス権で開く (デバイスの経路。補助プロセス・管理者) → ボリュームのロックを
    /// 保持して書く → 検証。キャンセルの確認では、出力先が途中まで書かれた状態になることを示す (「中止する」「続ける」)。
    /// </summary>
    internal async Task<CloneResult?> RunCloneAsync(CloneRequest request, DeviceCatalog catalog)
    {
        DocumentViewModel doc = request.Source;
        IByteSource source = doc.Document.Source;
        AcquisitionSourceInfo info = ImgSourceInfoOf(doc);
        int sector = info.SectorSize;
        long start = 0;
        long length = source.Length;
        if (request.SelectionOnly && doc.Editor.SelectionLength > 0)
        {
            start = doc.Editor.SelectionStart / sector * sector;
            length = Math.Min(source.Length, (doc.Editor.SelectionStart + doc.Editor.SelectionLength + sector - 1) / sector * sector) - start;
        }

        DiskInfo target = request.Target;
        IReadOnlyList<VolumeDeviceInfo> volumes = [.. catalog.Volumes.Where(v => v.DiskNumber == target.Number)];
        if (ImageForensics.Hooks.EvidenceDiskNumbers.Contains(target.Number) || ImageForensics.Hooks.EvidenceNumberOf(target.Path) is not null)
        {
            await ShowMessageAsync(Loc.Get("ImgClone_Title"), Loc.Get("ImgClone_TargetIsEvidence"), "ImgCloneRefusedDialog");
            return null;
        }

        if (volumes.FirstOrDefault(v => v.IsInUseByWindows) is { } system)
        {
            await ShowMessageAsync(Loc.Get("ImgClone_Title"), Loc.Format("DiskWrite_SystemVolume", system.Name), "ImgCloneRefusedDialog");
            return null;
        }

        long room = (target.Size ?? 0) - request.StartLba * sector;
        bool truncated = length > room;
        long bytes = Math.Min(length, Math.Max(0, room));
        string name = ImgDiskName(target.Number);
        var body = new StackPanel { Spacing = 6, MaxWidth = 520 };
        body.Children.Add(ImgNote("ImgCloneConfirm_Target", Loc.Format("ImgClone_ConfirmTarget", ImgDiskLabel(target, catalog))));
        body.Children.Add(ImgNote("ImgCloneConfirm_Range", Loc.Format("ImgClone_ConfirmRange", request.StartLba.ToString(CultureInfo.InvariantCulture),
            (bytes / sector).ToString("N0", CultureInfo.CurrentCulture), bytes.ToString("N0", CultureInfo.CurrentCulture))));
        body.Children.Add(ImgNote("ImgCloneConfirm_Content", Loc.Format("ImgClone_ConfirmContent", doc.DisplayName)));
        if (volumes.Count > 0)
        {
            body.Children.Add(ImgNote("ImgCloneConfirm_Volumes", Loc.Format("ImgClone_ConfirmVolumes", string.Join(Environment.NewLine, volumes.Select(ImgVolumeText))), warning: true));
        }

        if (truncated)
        {
            body.Children.Add(ImgNote("ImgCloneConfirm_Truncated", Loc.Format("ImgClone_Truncated", bytes.ToString("N0", CultureInfo.CurrentCulture)), warning: true));
        }

        body.Children.Add(ImgNote("ImgCloneConfirm_Irreversible", Loc.Get("ImgClone_Irreversible")));
        if (!await ImgStrongConfirmAsync(Loc.Get("ImgClone_Title"), body, name, Loc.Format("ImgClone_Execute", name), "ImgCloneConfirmDialog"))
        {
            return null;
        }

        // 出力先を書き込みのアクセス権で開く (ディスクを開くのと同じ経路。管理者権限がなければ案内)。
        OpenRoute route = DeviceService.RouteForDisk(isRemovableUsbVolume: false);
        if (route == OpenRoute.GuidanceNeeded)
        {
            await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_Disk"), forDisk: true);
            return null;
        }

        DeviceByteSource device;
        try
        {
            device = await DeviceService.OpenDeviceAsync(new DeviceOpenInfo { Path = target.Path, DisplayName = name, SerialNumber = target.SerialNumber, Disk = target }, true, route);
        }
        catch (Exception ex)
        {
            if (!await HandleElevatedOpenFailureAsync(ex, forDisk: true))
            {
                throw;
            }

            return null;
        }

        CloneResult? result = null;
        using (device)
        {
            ClonePlan plan = DiskClone.Plan(source, info, start, length, device, request.StartLba, request.Options.ZeroFillRest, catalog);
            if (plan.Problem != CloneProblem.None)
            {
                await ShowMessageAsync(Loc.Get("ImgClone_Title"), Loc.Get("ImgClone_Problem_" + plan.Problem), "ImgCloneRefusedDialog");
                return null;
            }

            try
            {
                result = await Vm.Operations.RunAsync(Loc.Format("ImgClone_Operation", name), OperationKind.WritesExternal, null, plan.BytesToWrite, async op =>
                {
                    op.ConfirmCancel = () => ImgOnUiAsync(() => ImgChooseAsync(Loc.Get("ImgClone_Title"), Loc.Get("ImgClone_CancelConfirm"), Loc.Get("ImgClone_Stop"),
                        Loc.Get("ImgClone_Continue"), "ImgCloneCancelDialog"));
                    return await DiskClone.RunAsync(plan, request.Options, op, ConfirmDismountSync, TestHooks.Time);
                });
            }
            catch (VolumeLockException ex)
            {
                ShowNotice(Loc.Format("ImgClone_LockFailed", ex.Volume), InfoBarSeverity.Error);
                return null;
            }
            catch (DiskWriteBlockedException ex)
            {
                ShowNotice(Loc.Format("DiskWrite_SystemVolume", ex.Volume), InfoBarSeverity.Error);
                return null;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                ShowNotice(Loc.Format("ImgClone_Failed", ex.Message), InfoBarSeverity.Error);
                return null;
            }
        }

        LastClone = result;
        foreach (DocumentViewModel open in Vm.Documents.Where(d => d.Device is { } dev && dev.Info.Disk?.Number == target.Number))
        {
            open.Document.RefreshFromSource();
        }

        string text = result.Status switch
        {
            AcquisitionStatus.Completed => Loc.Format("ImgClone_Done", bytes.ToString("N0", CultureInfo.CurrentCulture),
                result.Verification is { } v ? ImgVerdictText(v) : Loc.Get("ImgLog_NotVerified")),
            AcquisitionStatus.Cancelled => Loc.Get("ImgClone_Cancelled"),
            _ => Loc.Format("ImgClone_Failed", result.Message ?? string.Empty),
        };
        if (result.BadSectors.Count > 0)
        {
            text += " " + Loc.Format("Img_BadSectorsWarning", result.BadSectors.Sum(b => b.SectorCount).ToString("N0", CultureInfo.CurrentCulture));
        }

        ShowNotice(text, result.Status == AcquisitionStatus.Completed && result.Verification is not VerificationVerdict.Mismatch ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            actions: result.LogPath is { } logPath && File.Exists(logPath) ? [new Core.Notifications.NotificationAction(Loc.Get("Img_OpenLog"), () => TryOpen(logPath, readOnly: true))] : null);
        return result;
    }

    /// <summary>
    /// 強い確認 (共通の前提「破壊的な操作の確認」2・3): 対象の表示名を入力するまで実行ボタンを無効にし、既定のフォーカスを「キャンセル」に置く
    /// (Enter だけでは実行しない)。実行ボタンの文言は動作を表す。
    /// </summary>
    private async Task<bool> ImgStrongConfirmAsync(string title, StackPanel body, string targetName, string primary, string automationId)
    {
        TextBox confirm = DialogParts.Field(automationId + "_Name", Loc.Format("Img_StrongConfirmPrompt", targetName), monospace: false);
        body.Children.Add(confirm);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = title,
            Content = new ScrollViewer { Content = body, MaxHeight = 520 },
            PrimaryButtonText = primary,
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        confirm.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(confirm.Text.Trim(), targetName, StringComparison.Ordinal);
        dialog.Opened += (_, _) =>
        {
            // 既定のフォーカスは「キャンセル」(閉じるボタン)。
            if (ImgFindDescendant<Button>(dialog, "CloseButton") is { } close)
            {
                close.Focus(FocusState.Programmatic);
            }
        };
        Task<ContentDialogResult> shown = dialog.ShowQueuedAsync();
        if (ImgStrongConfirmInteraction is { } interact)
        {
            await interact(dialog);
        }

        return await shown == ContentDialogResult.Primary;
    }

    private static T? ImgFindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name)
            {
                return t;
            }

            if (ImgFindDescendant<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ---- セクタ範囲の保存 (FOR-25 の仕様 1) ----

    /// <summary>セクタ範囲の入力式の文脈 (<c>sector</c>、<c>sel.start</c>、<c>sel.len</c>、カーソル)。</summary>
    private sealed class ImgSectorContext(DocumentViewModel doc) : IExpressionContext
    {
        public long Cursor => doc.Editor.Cursor;

        public long Length => doc.Document.Length;

        public long SelectionStart => doc.Editor.SelectionStart;

        public long SelectionLength => doc.Editor.SelectionLength;

        public int SectorSize => Math.Max(1, doc.Document.Source.LogicalSectorSize);

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => doc.Document.Current.Read(offset, destination).IsComplete;
    }

    /// <summary>最後に保存した付随ファイルの内容 (テスト用)。</summary>
    internal SectorRangeSidecar? LastSectorSave { get; private set; }

    private async Task SaveSectorsAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        var context = new ImgSectorContext(doc);
        int sector = context.SectorSize;
        bool hasSelection = doc.Editor.SelectionLength > 0;
        TextBox start = DialogParts.Field("ImgSectors_Start", Loc.Get("ImgSectors_StartLba"), hasSelection ? "sel.start / sector" : "0");
        TextBox count = DialogParts.Field("ImgSectors_Count", Loc.Get("ImgSectors_Count"), hasSelection ? "(sel.start + sel.len + sector - 1) / sector - sel.start / sector" : "1");
        TextBox output = DialogParts.Field("ImgSectors_Output", Loc.Get("Img_Output"),
            Path.Combine(ImageForensics.Hooks.Case?.Folder is { } f ? Path.Combine(f, "exports") : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "sectors.bin"),
            monospace: false);
        var browse = new Button { Content = Loc.Get("Img_Browse") };
        AutomationProperties.SetAutomationId(browse, "ImgSectors_Browse");
        TextBlock error = DialogParts.Caption("ImgSectors_Error");
        var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
        panel.Children.Add(start);
        panel.Children.Add(count);
        panel.Children.Add(output);
        panel.Children.Add(browse);
        panel.Children.Add(error);
        ContentDialog dialog = DialogParts.Dialog(Root, "ImgSectorsSaveDialog", Loc.Get("ImgSectors_SaveTitle"), panel, Loc.Get("ImgSectors_Save"));
        (long Start, long Count)? Evaluate()
        {
            bool okStart = DialogParts.TryEvaluate(start.Text, context, out long s, out _);
            bool okCount = DialogParts.TryEvaluate(count.Text, context, out long n, out _);
            DialogParts.MarkInvalid(start, !okStart);
            DialogParts.MarkInvalid(count, !okCount);
            return okStart && okCount && s >= 0 && n > 0 && (s + n) * sector <= doc.Document.Source.Length ? (s, n) : null;
        }

        void Validate()
        {
            bool ok = Evaluate() is not null && output.Text.Trim().Length > 0;
            error.Text = ok ? string.Empty : Loc.Get("ImgSectors_RangeInvalid");
            dialog.IsPrimaryButtonEnabled = ok;
        }

        start.TextChanged += (_, _) => Validate();
        count.TextChanged += (_, _) => Validate();
        output.TextChanged += (_, _) => Validate();
        browse.Click += async (_, _) =>
        {
            if (!TestHooks.TrySavePicker(Path.GetFileName(output.Text), out string? chosen))
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = "sectors", SettingsIdentifier = "HexEditor.SectorRange" };
                picker.FileTypeChoices.Add(Loc.Get("ImgSectors_FileType"), [".bin"]);
                chosen = (await picker.PickSaveFileAsync())?.Path;
            }

            if (chosen is not null)
            {
                output.Text = chosen;
            }
        };
        Validate();
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary || Evaluate() is not { } range)
        {
            return;
        }

        await SaveSectorRangeAsync(doc, range.Start, range.Count, output.Text.Trim());
    }

    internal async Task<SectorRangeSidecar?> SaveSectorRangeAsync(DocumentViewModel doc, long startLba, long count, string path)
    {
        AcquisitionSourceInfo info = ImgSourceInfoOf(doc);
        IByteSource source = doc.Document.Source;
        long bytes = count * info.SectorSize;
        try
        {
            // 64 MiB 以上は長時間処理として処理センターに出す (FOR-25 の「巨大ファイル」)。
            SectorRangeSidecar sidecar = bytes >= 64L * 1024 * 1024
                ? await Vm.Operations.RunAsync(Loc.Format("ImgSectors_SaveOperation", Path.GetFileName(path)), OperationKind.ReadOnly, doc.Document, bytes,
                    op => Task.FromResult(SectorRangeArchive.Save(source, info, startLba, count, path, op, TestHooks.Time)))
                : await Task.Run(() => SectorRangeArchive.Save(source, info, startLba, count, path, null, TestHooks.Time));
            LastSectorSave = sidecar;
            ShowStatusMessage(Loc.Format("ImgSectors_Saved", count.ToString("N0", CultureInfo.CurrentCulture), Path.GetFileName(path)));
            return sidecar;
        }
        catch (Core.Saving.UnreadableDataException)
        {
            ShowNotice(Loc.Get("ImgSectors_Unreadable"), InfoBarSeverity.Error, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(Loc.Format("ImgSectors_SaveFailed", ex.Message), InfoBarSeverity.Error, doc);
        }

        return null;
    }

    // ---- セクタ範囲の復元 (FOR-25 の仕様 2〜6) ----

    /// <summary>最後の復元の結果と、表示した計画の文 (テスト用)。</summary>
    internal SectorRestoreResult? LastSectorRestore { get; private set; }

    internal string? LastSectorRestorePlanText { get; private set; }

    private async Task RestoreSectorsAsync()
    {
        if (Vm.Selected is not { Device: not null } doc)
        {
            return;
        }

        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.SectorRangeRestore");
        string? path = paths?.FirstOrDefault();
        if (path is null)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.SectorRangeRestore" };
            picker.FileTypeFilter.Add(".bin");
            path = (await picker.PickSingleFileAsync())?.Path;
        }

        if (path is not null)
        {
            await RestoreSectorRangeAsync(doc, path);
        }
    }

    /// <summary>
    /// 復元のダイアログ (データファイル、付随ファイルの内容、書き込み先、退避するか) → 確認 (範囲がディスク全体・パーティション全体なら強い確認) →
    /// 読み書きのアクセス権で開き直す (ENG-14 の経路) → 書き込み → 結果。
    /// </summary>
    internal async Task<SectorRestoreResult?> RestoreSectorRangeAsync(DocumentViewModel doc, string path, Func<ContentDialog, Task>? interact = null)
    {
        if (ImageForensics.Hooks.IsWriteBlocked || doc.Device is not { } device)
        {
            ShowNotice(Loc.Get("WriteBlock_Enabled"), InfoBarSeverity.Warning, doc);
            return null;
        }

        DeviceCatalog catalog = DeviceService.EnumerateDevices();
        int? diskNumber = device.Info.Disk?.Number ?? device.Info.Volume?.DiskNumber;
        var targetInfo = new SectorTargetInfo(device.Path, device.Info.Disk?.Model, device.Info.SerialNumber ?? device.Info.Disk?.SerialNumber, device.Length,
            device.LogicalSectorSize, device.Identity);
        SectorRestorePlan plan;
        try
        {
            plan = SectorRangeArchive.PlanRestore(path, device, targetInfo, null, catalog, diskNumber, device.Info.RangeStart);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("ImgSectors_ReadFailed", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        panel.Children.Add(ImgNote("ImgRestore_File", Loc.Format("ImgSectors_DataFile", path)));
        if (plan.Sidecar is { } s)
        {
            panel.Children.Add(ImgNote("ImgRestore_Sidecar", Loc.Format("ImgSectors_SidecarInfo", s.Model ?? "-", s.SerialNumber ?? "-",
                s.Capacity.ToString("N0", CultureInfo.CurrentCulture), s.SectorSize.ToString(CultureInfo.InvariantCulture),
                s.StartLba.ToString(CultureInfo.InvariantCulture), s.SectorCount.ToString("N0", CultureInfo.CurrentCulture), s.SavedAt)));
        }

        panel.Children.Add(ImgNote("ImgRestore_Target", Loc.Format("ImgSectors_TargetDisk", device.DisplayName)));
        TextBox startLba = DialogParts.Field("ImgRestore_StartLba", Loc.Get("ImgSectors_StartLba"), plan.Sidecar?.StartLba.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        CheckBox backup = DialogParts.Check("ImgRestore_Backup", Loc.Get("ImgSectors_Backup"), true);
        TextBlock warning = ImgNote("ImgRestore_Warning", string.Empty, warning: true);
        panel.Children.Add(startLba);
        panel.Children.Add(backup);
        panel.Children.Add(warning);
        ContentDialog dialog = DialogParts.Dialog(Root, "ImgSectorsRestoreDialog", Loc.Get("ImgSectors_RestoreTitle"), new ScrollViewer { Content = panel, MaxHeight = 520 },
            Loc.Get("ImgSectors_Next"));
        void Validate()
        {
            long? lba = long.TryParse(startLba.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : null;
            plan = SectorRangeArchive.PlanRestore(path, device, targetInfo, lba, catalog, diskNumber, device.Info.RangeStart);
            var problems = new List<string>();
            if (plan.HashMismatch)
            {
                problems.Add(Loc.Get("ImgSectors_HashMismatch"));
            }

            if (plan.NeedsStartLba)
            {
                problems.Add(Loc.Get("ImgSectors_NeedsStart"));
            }

            if (plan.OutOfRange || plan.NotAligned)
            {
                problems.Add(Loc.Get("ImgSectors_RangeInvalid"));
            }

            if (plan.IdentityMismatch)
            {
                problems.Add(Loc.Get("ImgSectors_OtherDisk"));
            }

            warning.Text = string.Join(Environment.NewLine, problems);
            LastSectorRestorePlanText = warning.Text;
            dialog.IsPrimaryButtonEnabled = plan.CanRun;
        }

        startLba.TextChanged += (_, _) => Validate();
        Validate();
        Task<ContentDialogResult> shown = dialog.ShowQueuedAsync();
        if (interact is not null)
        {
            await interact(dialog);
        }

        if (await shown != ContentDialogResult.Primary || !plan.CanRun)
        {
            return null;
        }

        // 確認 (共通の前提): 対象、書き込む範囲、内容、取り消せないこと。範囲がディスク全体・パーティション全体なら強い確認。
        string count = plan.SectorCount.ToString("N0", CultureInfo.CurrentCulture);
        var body = new StackPanel { Spacing = 6, MaxWidth = 520 };
        body.Children.Add(ImgNote("ImgRestoreConfirm_Target", Loc.Format("ImgSectors_ConfirmTarget", device.DisplayName,
            plan.VolumesToLock.Count == 0 ? Loc.Get("ImgClone_NoVolumes") : string.Join(", ", plan.VolumesToLock.Select(ImgVolumeText)))));
        body.Children.Add(ImgNote("ImgRestoreConfirm_Range", Loc.Format("ImgClone_ConfirmRange", plan.StartLba.ToString(CultureInfo.InvariantCulture), count,
            plan.Bytes.ToString("N0", CultureInfo.CurrentCulture))));
        body.Children.Add(ImgNote("ImgRestoreConfirm_Content", Loc.Format("ImgSectors_ConfirmContent", Path.GetFileName(path))));
        if (plan.IdentityMismatch)
        {
            body.Children.Add(ImgNote("ImgRestoreConfirm_OtherDisk", Loc.Get("ImgSectors_OtherDisk"), warning: true));
        }

        body.Children.Add(ImgNote("ImgRestoreConfirm_Irreversible", Loc.Get("ImgClone_Irreversible")));
        string primary = Loc.Format("ImgSectors_Write", count);
        bool confirmed = plan.WholeDiskOrPartition
            ? await ImgStrongConfirmAsync(Loc.Get("ImgSectors_RestoreTitle"), body, ImgTargetName(device), primary, "ImgRestoreConfirmDialog")
            : await ConfirmAsync(Loc.Get("ImgSectors_RestoreTitle"), body, primary, "ImgRestoreConfirmDialog");
        if (!confirmed || !await ReopenDeviceForWritingAsync(doc))
        {
            return null;
        }

        SectorRestoreResult result;
        try
        {
            SectorRestorePlan run = plan;
            result = await Vm.Operations.RunAsync(Loc.Format("ImgSectors_RestoreOperation", Path.GetFileName(path)), OperationKind.WritesExternal, doc.Document, plan.Bytes,
                op => Task.FromResult(SectorRangeArchive.Restore(run, path, device, backup.IsChecked == true, device, ConfirmDismountSync, op, TestHooks.Time)));
        }
        catch (VolumeLockException ex)
        {
            ShowNotice(Loc.Format("ImgClone_LockFailed", ex.Volume), InfoBarSeverity.Error, doc);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("ImgSectors_RestoreFailed", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }

        LastSectorRestore = result;
        doc.Document.RefreshFromSource();
        if (result.Success)
        {
            ShowNotice(Loc.Format("ImgSectors_Restored", count), InfoBarSeverity.Success, doc);
        }
        else if (result.FailedLba is { } failed)
        {
            ShowNotice(Loc.Format("ImgSectors_WriteFailedAt", (result.WrittenBytes / device.LogicalSectorSize).ToString("N0", CultureInfo.CurrentCulture),
                failed.ToString(CultureInfo.InvariantCulture), result.BackupPath ?? "-"), InfoBarSeverity.Error, doc);
        }
        else
        {
            ShowNotice(Loc.Get("ImgSectors_VerifyFailed"), InfoBarSeverity.Error, doc);
        }

        return result;
    }

    /// <summary>強い確認で入力する対象の名前 (ディスクなら「ディスク N」、ボリュームならボリュームの名前)。</summary>
    private static string ImgTargetName(DeviceByteSource device) =>
        device.Info.Disk is { } disk ? ImgDiskName(disk.Number) : device.Info.Volume?.Name ?? device.DisplayName;
}
