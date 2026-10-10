using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Processes;
using HexEditor.Core.Saving;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>ディスク・プロセスを開く・書き込む (ENG-28〜ENG-34、VIEW-32)。</summary>
public sealed partial class MainWindow
{
    private const string ShieldGlyph = ""; // Segoe Fluent: Permissions (盾)。
    private const string LockGlyph = "";    // Lock。

    private DeviceService DeviceService => App.Devices;

    private void RegisterDeviceCommands()
    {
        HookDeviceIntegration();
        RegisterSnapshotCommands();
        var e = new RoutedEventArgs();
        Commands.Register("file.openDisk", () => OpenDisk_Click(this, e));
        Commands.Register("file.openProcess", () => OpenProcess_Click(this, e));
        Commands.Register("file.openDiskImage", () => OpenDiskImage_Click(this, e));
        Commands.Register("file.toggleImmediateWrite", ToggleImmediateWrite,
            () => NeedsDocument(d => d.IsProcessMemory ? null : Loc.Get("Command_ProcessOnly")));

        Commands.Register("go.nextSector", () => Vm.Selected?.Editor.MoveNextSector(), NeedsDocument);
        Commands.Register("go.previousSector", () => Vm.Selected?.Editor.MovePreviousSector(), NeedsDocument);
        Commands.Register("go.toSector", () => _ = GoToSectorAsync(), NeedsDocument);
        Commands.Register("go.nextMemoryRegion", () => Vm.Selected?.Editor.MoveNextRegion(),
            () => NeedsDocument(d => d.IsProcessMemory || d.Snapshot is not null ? null : Loc.Get("Command_ProcessOnly")));
        Commands.Register("go.previousMemoryRegion", () => Vm.Selected?.Editor.MovePreviousRegion(),
            () => NeedsDocument(d => d.IsProcessMemory || d.Snapshot is not null ? null : Loc.Get("Command_ProcessOnly")));
    }

    // ---- ディスクを開く (ENG-29) ----

    private async void OpenDisk_Click(object sender, RoutedEventArgs e)
    {
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

        var items = new List<DeviceListItem>();
        foreach (DiskInfo disk in catalog.Disks)
        {
            bool needsAdmin = !DeviceService.IsElevated;
            items.Add(new DeviceListItem(DiskDisplayName(disk), disk.Path, needsAdmin, false, () => new DeviceOpenInfo
            {
                Path = disk.Path,
                DisplayName = DiskDisplayName(disk),
                SerialNumber = disk.SerialNumber,
                Disk = disk,
            }, isRemovableUsb: false));
        }

        foreach (VolumeDeviceInfo volume in catalog.Volumes)
        {
            bool needsAdmin = !volume.IsRemovableUsb && !DeviceService.IsElevated;
            items.Add(new DeviceListItem(VolumeDisplayName(volume), volume.Path, needsAdmin, false, () => new DeviceOpenInfo
            {
                Path = volume.Path,
                DisplayName = VolumeDisplayName(volume),
                Volume = volume,
            }, volume.IsRemovableUsb));
        }

        var (ok, chosen, readOnly) = await ShowDeviceDialogAsync(Loc.Get("OpenDisk_Title"), items, "OpenDiskDialog");
        if (!ok || chosen is null)
        {
            return;
        }

        await OpenDeviceItemAsync(chosen, readOnly);
    }

    private async Task OpenDeviceItemAsync(DeviceListItem item, bool readOnly)
    {
        OpenRoute route = DeviceService.RouteForDisk(item.IsRemovableUsb);
        if (route == OpenRoute.GuidanceNeeded)
        {
            await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_Disk"));
            return;
        }

        try
        {
            DeviceOpenInfo info = item.CreateInfo();
            DeviceByteSource source = await DeviceService.OpenDeviceAsync(info, writable: !readOnly && route != OpenRoute.Direct ? false : false, route);
            Vm.OpenDevice(source);
            RefreshHelperIndicator();
        }
        catch (HexEditor.Core.Elevation.HelperElevationDeclinedException)
        {
            ShowNotice(Loc.Get("AdminGuide_Declined"), InfoBarSeverity.Informational);
        }
        catch (HexEditor.Core.Elevation.HelperTamperedException)
        {
            // 補助プロセスのファイルのハッシュが違う: 起動せずに知らせる (PKG-14 の仕様 2・「エラー」)。
            ShowNotice(Loc.Get("Helper_Tampered"), InfoBarSeverity.Error);
        }
        catch (DeviceException ex)
        {
            ShowNotice(DeviceErrorMessage(ex), InfoBarSeverity.Error);
        }
        UpdateTitle();
    }

    // ---- プロセスを開く (ENG-32) ----

    private async void OpenProcess_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ProcessEntry> processes;
        try
        {
            processes = DeviceService.EnumerateProcesses();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice(Loc.Format("OpenProcess_EnumError", ex.Message), InfoBarSeverity.Error);
            return;
        }

        var items = new List<DeviceListItem>();
        foreach (ProcessEntry p in processes.Where(p => p.Pid is not (0 or 4)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool needsAdmin = p.Access == ProcessAccessLevel.NeedsElevation && !DeviceService.IsElevated;
            bool locked = p.Access == ProcessAccessLevel.Protected;
            ProcessEntry entry = p;
            items.Add(new DeviceListItem(ProcessDisplayName(p), $"PID {p.Pid}", needsAdmin, locked,
                () => new DeviceOpenInfo { Path = $"process:{entry.Pid}", DisplayName = ProcessDisplayName(entry) }, false) { Process = entry });
        }

        var (ok, chosen, readOnly) = await ShowDeviceDialogAsync(Loc.Get("OpenProcess_Title"), items, "OpenProcessDialog");
        if (!ok || chosen?.Process is not { } selected)
        {
            return;
        }

        await OpenProcessEntryAsync(selected, readOnly);
    }

    private async Task OpenProcessEntryAsync(ProcessEntry entry, bool readOnly)
    {
        OpenRoute route = DeviceService.RouteForProcess(entry.Access);
        if (route == OpenRoute.Impossible)
        {
            await ShowMessageAsync(Loc.Get("OpenProcess_Title"), Loc.Get("OpenProcess_Protected"), "ProcessProtectedDialog");
            return;
        }

        if (route == OpenRoute.GuidanceNeeded)
        {
            await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_Process"));
            return;
        }

        if (route == OpenRoute.Helper && !await ConfirmAsync(Loc.Get("OpenProcess_Title"), Loc.Get("OpenProcess_NeedsAdmin"),
            Loc.Get("OpenProcess_OpenElevated"), "OpenProcessElevatedDialog"))
        {
            return;
        }

        try
        {
            var info = new ProcessOpenInfo { DisplayName = ProcessDisplayName(entry) };
            ProcessMemoryByteSource source = await DeviceService.OpenProcessAsync(entry.Pid, info, writable: false, route);
            DocumentViewModel vm = Vm.OpenProcess(source, readOnly);
            ShowMemoryMapPanel();
            RefreshHelperIndicator();
            _ = vm;
        }
        catch (HexEditor.Core.Elevation.HelperElevationDeclinedException)
        {
            ShowNotice(Loc.Get("AdminGuide_Declined"), InfoBarSeverity.Informational);
        }
        catch (HexEditor.Core.Elevation.HelperTamperedException)
        {
            // 補助プロセスのファイルのハッシュが違う: 起動せずに知らせる (PKG-14 の仕様 2・「エラー」)。
            ShowNotice(Loc.Get("Helper_Tampered"), InfoBarSeverity.Error);
        }
        catch (ProcessAccessException ex)
        {
            ShowNotice(Loc.Format("OpenProcess_Error", ex.Message), InfoBarSeverity.Error);
        }
        UpdateTitle();
    }

    // ---- ディスクイメージとして開く (ENG-31) ----

    private async void OpenDiskImage_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.OpenDiskImage");
        string? path = paths?.FirstOrDefault();
        if (path is null)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.OpenDiskImage" };
            picker.FileTypeFilter.Add("*");
            path = (await picker.PickSingleFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        await OpenDiskImageAsync(path);
    }

    /// <summary>ファイルをディスクイメージとして開く (ENG-31): セクタサイズを選んで開く。「詳細を指定して開く」からも呼ぶ。</summary>
    private async Task OpenDiskImageAsync(string path)
    {
        int sectorSize = DiskImage.DefaultSectorSize(path);
        var (ok, chosenSize, allowResize) = await ShowDiskImageDialogAsync(path, sectorSize);
        if (!ok)
        {
            return;
        }

        try
        {
            DiskImageByteSource source = DiskImage.Open(path, new DiskImageOptions { SectorSize = chosenSize, AllowResize = allowResize });
            Vm.OpenDiskImageSource(source);
            if (DiskImage.Remainder(source.Length, chosenSize) is var rem and > 0)
            {
                ShowNotice(Loc.Format("DiskImage_Remainder", rem), InfoBarSeverity.Informational, Vm.Selected);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_Open", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
        }
        UpdateTitle();
    }

    // ---- 保存 (ENG-30、ENG-34) ----

    private async Task<bool> SaveDeviceAsync(DocumentViewModel doc)
    {
        if (doc.Device is not { } source)
        {
            return false;
        }

        DeviceCatalog catalog = DeviceService.EnumerateDevices();
        DiskWritePlan plan = DiskWrite.Plan(doc.Document, source, catalog);
        if (plan.RangeCount == 0)
        {
            return true;
        }

        if (plan.IsBlocked)
        {
            await ShowMessageAsync(Loc.Get("DiskWrite_Title"), Loc.Format("DiskWrite_SystemVolume", plan.BlockedVolumes[0].Name), "DiskWriteBlockedDialog");
            return false;
        }

        string body = Loc.Format("DiskWrite_Body", source.DisplayName, plan.RangeCount, plan.TotalBytes,
            plan.VolumesToLock.Count == 0 ? Loc.Get("DiskWrite_NoVolumes") : string.Join(", ", plan.VolumesToLock.Select(v => v.Name)));
        if (!await ConfirmAsync(Loc.Get("DiskWrite_Title"), body, Loc.Get("DiskWrite_Write"), "DiskWriteConfirmDialog"))
        {
            return false;
        }

        try
        {
            IReadOnlyList<SavedRange> saved = await Vm.Operations.RunAsync(
                Loc.Format("Operation_Save", source.DisplayName), OperationKind.WritesExternal, doc.Document, plan.TotalBytes,
                op => Task.FromResult(DiskWrite.Execute(plan, doc.Document.Current, Vm.RecoveryRoot, ConfirmDismountSync, op)),
                locked => doc.Document.SetEditLock(locked));
            doc.Document.CompleteDeviceWrite(source, saved);
            doc.OnSaved();
            ShowStatusMessage(Loc.Format("DiskWrite_Done", plan.TotalBytes));
            return true;
        }
        catch (VolumeLockException ex)
        {
            ShowNotice(Loc.Format("DiskWrite_LockFailed", ex.Volume), InfoBarSeverity.Error, doc);
        }
        catch (DiskWriteRolledBackException ex)
        {
            ShowNotice(Loc.Format("DiskWrite_RolledBack", ex.Message), InfoBarSeverity.Error, doc);
        }
        catch (DiskWritePartiallyWrittenException ex)
        {
            ShowNotice(Loc.Format("DiskWrite_Partial", ex.Message), InfoBarSeverity.Error, doc);
        }
        catch (DeviceException ex)
        {
            ShowNotice(DeviceErrorMessage(ex), InfoBarSeverity.Error, doc);
        }

        return false;
    }

    /// <summary>ロックできないボリュームのディスマウントの確認 (ENG-30 の仕様 3 の 2)。長時間処理のスレッドから UI スレッドで確かめる。</summary>
    private bool ConfirmDismountSync(VolumeDeviceInfo volume)
    {
        bool result = false;
        DispatcherQueue.TryEnqueue(() => { });
        var done = new System.Threading.ManualResetEventSlim();
        DispatcherQueue.TryEnqueue(async () =>
        {
            result = await ConfirmAsync(Loc.Get("DiskWrite_Title"), Loc.Format("DiskWrite_Dismount", volume.Name),
                Loc.Get("DiskWrite_DismountConfirm"), "DiskWriteDismountDialog");
            done.Set();
        });
        done.Wait(TimeSpan.FromMinutes(5));
        return result;
    }

    private async Task<bool> SaveProcessAsync(DocumentViewModel doc)
    {
        if (doc.ProcessMemory is not { } source)
        {
            return false;
        }

        if (!doc.Document.Current.EnumerateModifiedRanges().Any() && Vm.Selected?.Document.LastDeviceWriteRanges.Count == 0)
        {
            return true;
        }

        if (!doc.ProcessWriteConfirmed)
        {
            long bytes = doc.Document.Current.EnumerateModifiedRanges().Sum(r => r.Length);
            if (!await ConfirmAsync(Loc.Get("ProcessWrite_Title"), Loc.Format("ProcessWrite_Body", source.ProcessName, source.Pid, bytes),
                Loc.Get("ProcessWrite_Write"), "ProcessWriteConfirmDialog"))
            {
                return false;
            }

            doc.ProcessWriteConfirmed = true;
        }

        ProcessWriteResult result = await Task.Run(() => ProcessWrite.Execute(doc.Document, source, ConfirmProtectChangeSync));
        doc.Document.CompleteProcessWrite(source, result.Written, result.AllWritten);
        doc.OnSaved();
        if (!result.AllWritten)
        {
            ShowNotice(Loc.Format("ProcessWrite_Failed", result.Failed.Count), InfoBarSeverity.Warning, doc);
        }

        return result.AllWritten;
    }

    private bool ConfirmProtectChangeSync(uint protect)
    {
        bool result = false;
        var done = new System.Threading.ManualResetEventSlim();
        DispatcherQueue.TryEnqueue(async () =>
        {
            result = await ConfirmAsync(Loc.Get("ProcessWrite_Title"),
                Loc.Format("ProcessWrite_Protected", PageProtection.ShortText(protect)), Loc.Get("ProcessWrite_ChangeProtect"), "ProcessProtectDialog");
            done.Set();
        });
        done.Wait(TimeSpan.FromMinutes(5));
        return result;
    }

    private void ToggleImmediateWrite()
    {
        if (Vm.Selected is { IsProcessMemory: true } doc)
        {
            doc.ImmediateWrite = !doc.ImmediateWrite;
            ShowStatusMessage(Loc.Get(doc.ImmediateWrite ? "ProcessWrite_ImmediateOn" : "ProcessWrite_ImmediateOff"));
            doc.NotifyStatusChanged();
        }
    }

    private async Task GoToSectorAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        var box = new NumberBox { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, Minimum = 0, SmallChange = 1, LargeChange = 16 };
        AutomationProperties.SetName(box, Loc.Get("GoToSector_Label"));
        if (await ConfirmAsync(Loc.Get("GoToSector_Title"), box, Loc.Get("Common_Go"), "GoToSectorDialog") && !double.IsNaN(box.Value))
        {
            doc.Editor.GoToSector((long)box.Value);
        }
    }

    // ---- 補助プロセスの案内 (ENG-28 の仕様 12) ----

    private async Task ShowAdminGuidanceAsync(string reason)
    {
        // 1. 理由、2. 配布形態ごとの起動し直す手順 (番号付き)、3. 管理者として実行中の制限 (UI-34 の仕様 7)、4. 管理者権限なしで使える代わりの方法
        // (ディスクを開く操作だけ)、5. Store 版ではインストーラ版・ポータブル版の案内とダウンロードページへのリンク (ENG-28 の仕様 12)。
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        bool portable = HexEditor.App.Hosting.Program.Environment.Distribution == HexEditor.Platform.Distribution.Portable;
        panel.Children.Add(new TextBlock { Text = Loc.Get(portable ? "AdminGuide_StepsPortable" : "AdminGuide_Steps"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.Get("Drop_AdminLimited"), TextWrapping = TextWrapping.Wrap });
        if (reason == Loc.Get("AdminGuide_Disk"))
        {
            panel.Children.Add(new TextBlock { Text = Loc.Get("AdminGuide_UsbAlternative"), TextWrapping = TextWrapping.Wrap });
        }

        bool canRestart = DeviceService.HelperSupported;
        if (!canRestart)
        {
            panel.Children.Add(new TextBlock { Text = Loc.Get("AdminGuide_StoreDownload"), TextWrapping = TextWrapping.Wrap });
            var link = new HyperlinkButton { Content = Loc.Get("AdminGuide_DownloadLink"), NavigateUri = new Uri(AboutInfo.RepositoryUrl + "/releases") };
            AutomationProperties.SetAutomationId(link, "AdminGuide_DownloadLink");
            panel.Children.Add(link);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("AdminGuide_Title"),
            Content = panel,
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (canRestart)
        {
            dialog.PrimaryButtonText = Loc.Get("AdminGuide_Restart");
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        AutomationProperties.SetAutomationId(dialog, "AdminGuidanceDialog");
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && canRestart)
        {
            await RestartElevatedAsync();
        }
    }

    private async Task RestartElevatedAsync()
    {
        // 終了時と同じ確認・セッションの保存をしてから、管理者として起動し直す (ENG-28 の仕様 12 の 7)。
        if (!await ConfirmExitAsync())
        {
            return;
        }

        SaveSession();
        try
        {
            if (AppRestart.RestartAsAdministrator())
            {
                Application.Current.Exit();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // UAC で拒否された: 終了せず続ける (仕様 12 の 7)。
            AppLog.Info($"Relaunch as admin cancelled: {ex.Message}");
        }
    }

    private void RefreshHelperIndicator() => UpdateHelperShield();
}
