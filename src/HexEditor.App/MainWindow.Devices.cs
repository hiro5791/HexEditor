using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Processes;
using HexEditor.Core.Saving;
using HexEditor.Core.Settings;
using HexEditor.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>ディスク・プロセスを開く・書き込む (ENG-28〜ENG-34、VIEW-32)。</summary>
public sealed partial class MainWindow
{
    private const string ShieldGlyph = ""; // Segoe Fluent: Permissions (盾)。
    private const string LockGlyph = "";    // Lock。

    /// <summary>インストーラ版・ポータブル版のダウンロードページ (ENG-28 の仕様 12 の 5)。</summary>
    internal const string DownloadPageUrl = AboutInfo.RepositoryUrl + "/releases/latest";

    private DeviceService DeviceService => App.Devices;

    private void RegisterDeviceCommands()
    {
        HookDeviceIntegration();
        RegisterSnapshotCommands();
        var e = new RoutedEventArgs();
        Commands.Register("file.openDisk", () => OpenDisk_Click(this, e));
        Commands.Register("file.openProcess", () => OpenProcess_Click(this, e));
        Commands.Register("file.openDiskImage", () => OpenDiskImage_Click(this, e));
        Commands.Register("file.toggleImmediateWrite", () => _ = ToggleImmediateWriteAsync(),
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

        DiskDialogResult? choice = await ShowOpenDiskDialogAsync(DeviceItems(catalog));
        if (choice is null)
        {
            return;
        }

        await OpenDeviceItemAsync(choice.Item, choice.ReadOnly, choice.Range);
    }

    /// <summary>「ディスクを開く」の一覧の行: 物理ディスク、ボリューム、光学ドライブ (ENG-29 の仕様 1)。</summary>
    internal List<DeviceListItem> DeviceItems(DeviceCatalog catalog)
    {
        // 盾のアイコン: 管理者権限なしでは開けない項目 (アプリを管理者として実行している場合は付けない。仕様 3 の 3)。
        bool elevated = DeviceService.IsElevated;
        var items = new List<DeviceListItem>();
        foreach (DiskInfo disk in catalog.Disks)
        {
            DiskInfo d = disk;
            items.Add(new DeviceListItem(DiskDisplayName(d), DiskDetail(d), !elevated, false, () => new DeviceOpenInfo
            {
                Path = d.Path,
                DisplayName = DiskDisplayName(d),
                SerialNumber = d.SerialNumber,
                Disk = d,
            }, isRemovableUsb: false) { Size = d.Size, SectorSize = d.LogicalSectorSize });
        }

        foreach (VolumeDeviceInfo volume in catalog.Volumes)
        {
            VolumeDeviceInfo v = volume;
            int? sector = v.DiskNumber is { } n ? catalog.Disks.FirstOrDefault(x => x.Number == n)?.LogicalSectorSize : null;
            items.Add(new DeviceListItem(VolumeDisplayName(v), VolumeDetail(v), !v.IsRemovableUsb && !elevated, false, () => new DeviceOpenInfo
            {
                Path = v.Path,
                DisplayName = VolumeDisplayName(v),
                Volume = v,
            }, v.IsRemovableUsb) { Size = v.Size, SectorSize = sector });
        }

        foreach (OpticalDriveInfo drive in catalog.OpticalDrives)
        {
            OpticalDriveInfo o = drive;
            string name = Loc.Format("OpenDisk_OpticalName", o.DriveLetter ?? o.Path);
            string media = Loc.Get(o.HasMedia switch { true => "OpenDisk_MediaPresent", false => "OpenDisk_MediaAbsent", _ => "Common_Unknown" });
            items.Add(new DeviceListItem(name, media, !elevated, false, () => new DeviceOpenInfo { Path = o.Path, DisplayName = name },
                isRemovableUsb: false) { SectorSize = 2048 });
        }

        return items;
    }

    /// <summary>
    /// 一覧で選んだデバイスを開く (ENG-29 の仕様 3)。取り外し可能な USB ストレージのボリュームは UI のプロセスから直接開き、アクセスが
    /// 拒否されたら補助プロセス・管理者権限の経路に進む (仕様 3 の 1)。どちらも使えなければ管理者権限の案内 (ENG-28 の仕様 12)。
    /// 「読み取り専用で開く」をオフにした場合は読み書きのアクセス権で開く (ENG-14 の仕様 1)。
    /// </summary>
    internal async Task<DocumentViewModel?> OpenDeviceItemAsync(DeviceListItem item, bool readOnly, DeviceRangeInput? range = null)
    {
        OpenRoute route = DeviceService.RouteForDisk(item.IsRemovableUsb);
        if (route == OpenRoute.GuidanceNeeded)
        {
            await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_Disk"), forDisk: true);
            return null;
        }

        DocumentViewModel? opened = null;
        try
        {
            DeviceOpenInfo info = item.CreateInfo();
            DeviceByteSource source;
            try
            {
                source = await OpenDeviceWithRangeAsync(info, !readOnly, route, range);
            }
            catch (DeviceException ex) when (route == OpenRoute.Direct && ex.ErrorCode == Win32Errors.AccessDenied)
            {
                // USB ストレージでも直接開けなかった: 管理者権限の経路で開き直す (仕様 3 の 1 → 3 の 2)。
                route = DeviceService.RouteForDisk(isRemovableUsbVolume: false);
                if (route == OpenRoute.GuidanceNeeded)
                {
                    await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_Disk"), forDisk: true);
                    return null;
                }

                source = await OpenDeviceWithRangeAsync(info, !readOnly, route, range);
            }

            opened = Vm.OpenDevice(source, readOnly);
            RefreshHelperIndicator();
        }
        catch (Exception ex)
        {
        if (!await HandleElevatedOpenFailureAsync(ex, forDisk: true))
        {
            throw;
        }
        }

        UpdateTitle();
        return opened;
    }

    private async Task<DeviceByteSource> OpenDeviceWithRangeAsync(DeviceOpenInfo info, bool writable, OpenRoute route, DeviceRangeInput? range)
    {
        if (range is null)
        {
            return await DeviceService.OpenDeviceAsync(info, writable, route);
        }

        return await DeviceService.OpenDeviceAsync(info, writable, route, adjust: (geometry, open) =>
        {
            (long start, long length) = ResolveRange(range, geometry.Length, geometry.LogicalSectorSize);
            return open with
            {
                RangeStart = start,
                RangeLength = length,
                DisplayName = open.DisplayName + " " + DocumentViewModel.FormatRange(start, length),
            };
        });
    }

    /// <summary>
    /// 管理者権限が要る経路で開くときの失敗を利用者に示す。示したら true (例外を飲み込む)。UAC の拒否 (ENG-28 の仕様 8)、補助プロセスがない
    /// (仕様 12 の案内)、補助プロセスのハッシュが違う (PKG-14 の「エラー」)、補助プロセスを起動・接続できない、デバイス・プロセスのエラー。
    /// </summary>
    private async Task<bool> HandleElevatedOpenFailureAsync(Exception ex, bool forDisk)
    {
        switch (ex)
        {
            case HelperElevationDeclinedException:
                ShowNotice(Loc.Get(forDisk ? "AdminGuide_Declined" : "AdminGuide_DeclinedProcess"), InfoBarSeverity.Informational);
                return true;
            case HelperNotFoundException:
                await ShowAdminGuidanceAsync(Loc.Get(forDisk ? "AdminGuide_Disk" : "AdminGuide_Process"), forDisk);
                return true;
            case HelperTamperedException:
                ShowNotice(Loc.Get("Helper_Tampered"), InfoBarSeverity.Error);
                return true;
            case DeviceException device:
                ShowNotice(DeviceErrorMessage(device), InfoBarSeverity.Error);
                return true;
            case ProcessAccessException process:
                ShowNotice(Loc.Format("OpenProcess_Error", process.Message), InfoBarSeverity.Error);
                return true;
            case ArgumentException range:
                // 範囲の入力が開いたデバイスに合わない (ENG-29 の仕様 2)。
                ShowNotice(range.Message, InfoBarSeverity.Error);
                return true;
            case IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException:
                AppLog.Info($"Elevated open failed: {ex}");
                ShowNotice(Loc.Format("Helper_StartFailed", ex.Message), InfoBarSeverity.Error);
                return true;
            default:
                return false;
        }
    }

    // ---- プロセスを開く (ENG-32) ----

    private async void OpenProcess_Click(object sender, RoutedEventArgs e)
    {
        ProcessDialogResult? choice;
        try
        {
            choice = await ShowOpenProcessDialogAsync(DeviceService.EnumerateProcesses);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice(Loc.Format("OpenProcess_EnumError", ex.Message), InfoBarSeverity.Error);
            return;
        }

        if (choice is null)
        {
            return;
        }

        await OpenProcessEntryAsync(choice.Entry, choice.ReadOnly, choice.Scope, choice.Range);
    }

    /// <summary>
    /// プロセスを開く (ENG-32 の仕様 2・4): まず UI のプロセスから直接、拒否されたら確認の後に補助プロセス (管理者として実行中なら同じプロセス)、
    /// どちらも使えなければ案内。モジュール・範囲を選んだ場合は、その範囲だけのドキュメントにする (オフセット 0 が開始アドレス。仕様 6)。
    /// </summary>
    internal async Task<DocumentViewModel?> OpenProcessEntryAsync(ProcessEntry entry, bool readOnly,
        ProcessOpenScope scope = ProcessOpenScope.Whole, DeviceRangeInput? range = null, Func<IReadOnlyList<ProcessModule>, Task<ProcessModule?>>? chooseModule = null)
    {
        OpenRoute route = DeviceService.RouteForProcess(entry.Access);
        if (route == OpenRoute.Impossible)
        {
            await ShowMessageAsync(Loc.Get("OpenProcess_Title"), Loc.Get("OpenProcess_Protected"), "ProcessProtectedDialog");
            return null;
        }

        if (route == OpenRoute.GuidanceNeeded)
        {
            await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_Process"), forDisk: false);
            return null;
        }

        if (route == OpenRoute.Helper && !await ConfirmAsync(Loc.Get("OpenProcess_Title"), Loc.Get("OpenProcess_NeedsAdmin"),
            Loc.Get("OpenProcess_OpenElevated"), "OpenProcessElevatedDialog"))
        {
            return null;
        }

        DocumentViewModel? opened = null;
        try
        {
            var info = new ProcessOpenInfo { DisplayName = ProcessDisplayName(entry) };
            ProcessMemoryByteSource source = await DeviceService.OpenProcessAsync(entry.Pid, info, writable: !readOnly, route);
            if (scope != ProcessOpenScope.Whole)
            {
                ProcessOpenInfo? narrowed = scope == ProcessOpenScope.Module
                    ? await ModuleInfoAsync(source, info, chooseModule ?? ChooseModuleAsync)
                    : RangeInfo(source, info, range!);
                source.Dispose();
                if (narrowed is null)
                {
                    return null;
                }

                source = await DeviceService.OpenProcessAsync(entry.Pid, narrowed, writable: !readOnly, route);
            }

            opened = Vm.OpenProcess(source, readOnly);
            ShowMemoryMapPanel();
            RefreshHelperIndicator();
        }
        catch (Exception ex)
        {
        if (!await HandleElevatedOpenFailureAsync(ex, forDisk: false))
        {
            throw;
        }
        }

        UpdateTitle();
        return opened;
    }

    /// <summary>モジュールを選んで開く範囲 (表示名「notepad.exe (PID 1234) - kernel32.dll」。仕様 8)。取り消したら null。</summary>
    private static async Task<ProcessOpenInfo?> ModuleInfoAsync(ProcessMemoryByteSource whole, ProcessOpenInfo info,
        Func<IReadOnlyList<ProcessModule>, Task<ProcessModule?>> choose)
    {
        if (await choose(whole.Modules) is not { } module)
        {
            return null;
        }

        return info with
        {
            RangeStart = module.BaseAddress,
            RangeLength = module.Size,
            ModuleName = module.Name,
            DisplayName = Loc.Format("OpenProcess_ModuleName", info.DisplayName, module.Name),
        };
    }

    /// <summary>開始アドレスと長さを指定して開く範囲 (仕様 2 の「範囲を指定」)。</summary>
    private static ProcessOpenInfo RangeInfo(ProcessMemoryByteSource whole, ProcessOpenInfo info, DeviceRangeInput range)
    {
        (long start, long length) = ResolveRange(range with { InSectors = false }, whole.Length, 1);
        return info with
        {
            RangeStart = start,
            RangeLength = length,
            DisplayName = info.DisplayName + " " + DocumentViewModel.FormatRange(start, length),
        };
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

    /// <summary>
    /// 拡張子 .img .dd .raw .iso .bin のファイルを通常の「開く」で開いたら、ディスクイメージとして開き直すかを InfoBar で提案する
    /// (ENG-31 の仕様 6。設定でオフにできる)。
    /// </summary>
    private void SuggestDiskImage(DocumentViewModel doc)
    {
        if (doc.FilePath is not { } path || !DiskImage.LooksLikeImage(path) || TestHooks.SuppressDiskImageSuggestion
            || !App.Settings.GetBool(DeviceSettings.SuggestDiskImageKey, true))
        {
            return;
        }

        ShowNotice(Loc.Get("DiskImage_Suggest"), InfoBarSeverity.Informational, doc, actions:
        [
            new Core.Notifications.NotificationAction(Loc.Get("DiskImage_Title"), () =>
            {
                if (Vm.Documents.Contains(doc) && !doc.Document.IsModified)
                {
                    Vm.Close(doc);
                    _ = OpenDiskImageAsync(path);
                }
            }),
        ]);
    }

    // ---- 読み取り専用の解除 (ENG-14 の仕様 3) ----

    /// <summary>
    /// ディスク・ボリューム・プロセスの読み取り専用を解除するときの確認 (ENG-14 の仕様 3): 「このデバイスに書き込めるようにします。保存するまで
    /// 実際には書き込みません」と対象 (ディスクのモデル名・サイズ、またはプロセス名・PID)、「書き込みを許可」「キャンセル」。
    /// </summary>
    private async Task<bool> ConfirmDeviceWritingAsync(DocumentViewModel doc)
    {
        string target = doc.Device is { } device
            ? device.Info.Disk is { } disk ? DiskDisplayName(disk) : device.DisplayName
            : doc.ProcessMemory is { } process ? Loc.Format("OpenProcess_Name", process.ProcessName, process.Pid) : doc.DisplayName;
        var body = new TextBlock { Text = Loc.Format("ReadOnly_Confirm_DeviceTarget", target), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        AutomationProperties.SetAutomationId(body, "DeviceWriteConfirm_Body");
        return await ConfirmAsync(Loc.Get("ReadOnly_Confirm_Title"), body, Loc.Get("ReadOnly_AllowWrite"), "DeviceWriteConfirmDialog");
    }

    /// <summary>
    /// 読み書きのアクセス権で開き直す (ENG-14 の仕様 3。補助プロセス経由のものは補助プロセスの OpenDevice / OpenProcess)。既に読み書きで開いて
    /// いれば何もしない。開けなければ理由を示して false。
    /// </summary>
    private async Task<bool> ReopenDeviceForWritingAsync(DocumentViewModel doc)
    {
        try
        {
            if (doc.Device is { } device)
            {
                if (device.Handle.Writable && !device.IsDisconnected)
                {
                    return true;
                }

                IDeviceAccess access = await DeviceService.DeviceAccessForAsync(RouteOf(device.Route));
                IDeviceHandle handle = access.Open(device.Path, writable: true);
                device.ReplaceHandle(handle, access);
                return true;
            }

            if (doc.ProcessMemory is { } process)
            {
                if (process.Memory.Writable && !process.HasExited)
                {
                    return true;
                }

                if (process.HasExited)
                {
                    ShowNotice(Loc.Get("Process_Exited"), InfoBarSeverity.Warning, doc);
                    return false;
                }

                IProcessAccess access = await DeviceService.ProcessAccessForAsync(RouteOf(process.Info.Route));
                IProcessMemory memory = access.Open(process.Pid, writable: true);
                process.ReplaceMemory(memory, access);
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!await HandleElevatedOpenFailureAsync(ex, doc.Device is not null))
            {
                throw;
            }

            return false;
        }

        return true;
    }

    private static OpenRoute RouteOf(DeviceRoute route) => route switch
    {
        DeviceRoute.Helper => OpenRoute.Helper,
        DeviceRoute.Elevated => OpenRoute.SameProcess,
        _ => OpenRoute.Direct,
    };

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

        string target = source.Info.Disk is { } disk ? DiskDisplayName(disk) : source.DisplayName;
        string body = Loc.Format("DiskWrite_Body", target, plan.RangeCount, plan.TotalBytes,
            plan.VolumesToLock.Count == 0 ? Loc.Get("DiskWrite_NoVolumes") : string.Join(", ", plan.VolumesToLock.Select(v => v.Name)));
        if (!await ConfirmAsync(Loc.Get("DiskWrite_Title"), body, Loc.Get("DiskWrite_Write"), "DiskWriteConfirmDialog"))
        {
            return false;
        }

        // 旧内容のジャーナルの上限を超える場合 (ENG-30 の仕様 4 の 2 → ENG-23 の仕様 3): 保護なしで書くかを確かめる。
        bool journal = true;
        if (plan.TotalBytes > InPlaceSaver.DefaultJournalLimit)
        {
            if (!await ConfirmAsync(Loc.Get("DiskWrite_Title"), Loc.Get("DiskWrite_JournalTooLarge"), Loc.Get("DiskWrite_WriteUnprotected"),
                "DiskWriteJournalDialog"))
            {
                return false;
            }

            journal = false;
        }

        try
        {
            IReadOnlyList<SavedRange> saved = await Vm.Operations.RunAsync(
                Loc.Format("Operation_Save", source.DisplayName), OperationKind.WritesExternal, doc.Document, plan.TotalBytes,
                op => Task.FromResult(DiskWrite.Execute(plan, doc.Document.Current, Vm.RecoveryRoot, ConfirmDismountSync, op, journal)),
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
        catch (DiskWriteBlockedException ex)
        {
            ShowNotice(Loc.Format("DiskWrite_SystemVolume", ex.Volume), InfoBarSeverity.Error, doc);
        }
        catch (DiskWriteRolledBackException ex)
        {
            ShowNotice(Loc.Format("DiskWrite_RolledBack", ex.InnerException is DeviceWriteException w ? WriteErrorText(w, source) : ex.Message),
                InfoBarSeverity.Error, doc);
        }
        catch (DiskWritePartiallyWrittenException ex)
        {
            ShowNotice(Loc.Format(ex.JournalPath is null ? "DiskWrite_PartialUnprotected" : "DiskWrite_Partial", ex.Message), InfoBarSeverity.Error, doc);
        }
        catch (OperationCanceledException)
        {
            // キャンセル: 書き込みを始めていれば書き戻した (ENG-30 の仕様 6)。
            ShowStatusMessage(Loc.Get("DiskWrite_Cancelled"));
        }
        catch (UnreadableDataException)
        {
            ShowNotice(Loc.Get("DiskWrite_Unreadable"), InfoBarSeverity.Error, doc);
        }
        catch (DeviceException ex)
        {
            ShowNotice(DeviceErrorMessage(ex), InfoBarSeverity.Error, doc);
        }

        return false;
    }

    /// <summary>書き込みエラーの説明「セクタ番号、OS のメッセージ」(ENG-30 の「エラー」)。</summary>
    private static string WriteErrorText(DeviceWriteException error, DeviceByteSource source)
    {
        long sector = (source.Info.RangeStart + error.Offset) / Math.Max(1, source.LogicalSectorSize);
        string os = error.ErrorCode == Win32Errors.WriteProtect ? Loc.Get("DiskWrite_WriteProtected")
            : new System.ComponentModel.Win32Exception(error.ErrorCode).Message;
        return Loc.Format("DiskWrite_ErrorAt", sector, os);
    }

    /// <summary>ロックできないボリュームのディスマウントの確認 (ENG-30 の仕様 3 の 2)。長時間処理のスレッドから UI スレッドで確かめる。</summary>
    private bool ConfirmDismountSync(VolumeDeviceInfo volume)
    {
        bool result = false;
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

        if (!doc.Document.Current.EnumerateModifiedRanges().Any() && doc.Document.LastDeviceWriteRanges.Count == 0)
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

        return await WriteProcessAsync(doc, source);
    }

    /// <summary>
    /// 変更範囲をプロセスに書き込み、ドキュメントに反映する。失敗した範囲は一覧 (アドレス、長さ、理由) で示す (ENG-34 の仕様 5 と「エラー」)。
    /// </summary>
    private async Task<bool> WriteProcessAsync(DocumentViewModel doc, ProcessMemoryByteSource source)
    {
        ProcessWriteResult result = await Task.Run(() => ProcessWrite.Execute(doc.Document, source, ConfirmProtectChangeSync));
        if (doc.Document.IsDisposed)
        {
            return false;
        }

        doc.Document.CompleteProcessWrite(source, result.Written, result.AllWritten);
        doc.OnSaved();
        if (!result.AllWritten)
        {
            ShowNotice(ProcessWriteFailureText(source, result), InfoBarSeverity.Warning, doc);
        }

        return result.AllWritten;
    }

    /// <summary>書き込みに失敗した範囲の一覧の文 (先頭の 10 件まで)。プロセスが終了していた場合は「プロセスが終了したため書き込めませんでした」。</summary>
    internal static string ProcessWriteFailureText(ProcessMemoryByteSource source, ProcessWriteResult result)
    {
        if (result.Failed.Any(f => f.Reason == ProcessWriteReason.ProcessExited))
        {
            return Loc.Get("ProcessWrite_Exited");
        }

        IEnumerable<string> lines = result.Failed.Take(10).Select(f => Loc.Format("ProcessWrite_FailedRange",
            "0x" + (source.BaseAddress + f.Offset).ToString("X", System.Globalization.CultureInfo.InvariantCulture),
            f.Length.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            Loc.Get("ProcessWrite_Reason_" + f.Reason)));
        string text = Loc.Format("ProcessWrite_Failed", result.Failed.Count) + Environment.NewLine + string.Join(Environment.NewLine, lines);
        return result.Failed.Count > 10 ? text + Environment.NewLine + "…" : text;
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

    // ---- 即時書き込み (ENG-34 の仕様 2) ----

    private readonly Dictionary<DocumentViewModel, EventHandler<DocumentChangedEventArgs>> _immediateWriters = [];

    /// <summary>即時書き込みモードの切り替え。オンにするときは確認する (ENG-34 の仕様 2)。オンにしたら、未保存の変更もすぐに書き込む。</summary>
    internal async Task ToggleImmediateWriteAsync()
    {
        if (Vm.Selected is not { IsProcessMemory: true } doc || doc.ProcessMemory is not { } source)
        {
            return;
        }

        if (!doc.ImmediateWrite)
        {
            if (!await ConfirmAsync(Loc.Get("ProcessWrite_Title"), Loc.Format("ProcessWrite_ImmediateConfirm", source.ProcessName, source.Pid),
                Loc.Get("ProcessWrite_ImmediateEnable"), "ImmediateWriteConfirmDialog"))
            {
                return;
            }

            doc.ProcessWriteConfirmed = true;
        }

        SetImmediateWrite(doc, !doc.ImmediateWrite);
        ShowStatusMessage(Loc.Get(doc.ImmediateWrite ? "ProcessWrite_ImmediateOn" : "ProcessWrite_ImmediateOff"));
        if (doc.ImmediateWrite && doc.Document.IsModified)
        {
            await WriteImmediatelyAsync(doc);
        }
    }

    /// <summary>即時書き込みモードを設定する: オンの間は、編集・Undo・Redo のたびに変更をプロセスに書き込む。</summary>
    internal void SetImmediateWrite(DocumentViewModel doc, bool enabled)
    {
        if (_immediateWriters.Remove(doc, out EventHandler<DocumentChangedEventArgs>? old))
        {
            doc.Document.Changed -= old;
        }

        doc.ImmediateWrite = enabled;
        if (enabled)
        {
            void OnChanged(object? sender, DocumentChangedEventArgs e)
            {
                if (e.Kind is DocumentChangeKind.Edit or DocumentChangeKind.Undo or DocumentChangeKind.Redo && !doc.Document.IsDisposed)
                {
                    DispatcherQueue.TryEnqueue(() => _ = WriteImmediatelyAsync(doc));
                }
            }

            doc.Document.Changed += OnChanged;
            _immediateWriters[doc] = OnChanged;
        }

        doc.NotifyStatusChanged();
    }

    private readonly HashSet<DocumentViewModel> _immediateBusy = [];
    private readonly HashSet<DocumentViewModel> _immediatePending = [];

    /// <summary>即時書き込み: 書き込み中に次の編集が来たら、終わってからもう一度書く (書き込みを重ねない)。</summary>
    private async Task WriteImmediatelyAsync(DocumentViewModel doc)
    {
        if (doc.ProcessMemory is not { } source || !doc.ImmediateWrite || doc.Document.IsDisposed || doc.Document.IsReadOnly)
        {
            return;
        }

        if (!_immediateBusy.Add(doc))
        {
            _immediatePending.Add(doc);
            return;
        }

        try
        {
            do
            {
                _immediatePending.Remove(doc);
                if (doc.Document.Current.EnumerateModifiedRanges().Any() || doc.Document.LastDeviceWriteRanges.Count > 0)
                {
                    await WriteProcessAsync(doc, source);
                }
            }
            while (_immediatePending.Contains(doc) && !doc.Document.IsDisposed);
        }
        finally
        {
            _immediateBusy.Remove(doc);
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

    // ---- 管理者権限の案内 (ENG-28 の仕様 12) ----

    /// <summary>最後に出した管理者権限の案内の本文 (テスト用)。</summary>
    internal string? LastAdminGuidance { get; private set; }

    /// <summary>
    /// 管理者権限の案内 (ENG-28 の仕様 12): 理由 (操作ごと)、配布形態ごとの起動し直す手順、管理者として実行中の制限、代わりの方法 (ディスクでは
    /// USB ストレージ)、Store 版ではインストーラ版・ポータブル版の案内とダウンロードページへのリンク。「管理者として再起動」はプログラムから
    /// 起動し直せる配布形態 (インストーラ版・ポータブル版) だけに出す。
    /// </summary>
    private async Task ShowAdminGuidanceAsync(string reason, bool forDisk = true)
    {
        (List<string> texts, bool canRestart) = AdminGuidanceContent(reason, forDisk);

        var panel = new StackPanel { Spacing = 8, MaxWidth = 480 };
        foreach (string text in texts)
        {
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        }

        if (!canRestart)
        {
            var link = new HyperlinkButton { Content = Loc.Get("AdminGuide_DownloadLink"), NavigateUri = new Uri(DownloadPageUrl), Padding = new Thickness(0) };
            AutomationProperties.SetAutomationId(link, "AdminGuide_DownloadLink");
            panel.Children.Add(link);
        }

        LastAdminGuidance = string.Join(Environment.NewLine, texts);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("AdminGuide_Title"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
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

    /// <summary>管理者権限の案内の本文 (段落) と、「管理者として再起動」を出すか (ENG-28 の仕様 12)。</summary>
    internal (List<string> Texts, bool CanRestart) AdminGuidanceContent(string reason, bool forDisk)
    {
        Distribution distribution = DeviceService.Distribution;
        bool canRestart = DeviceService.HelperSupported;
        var texts = new List<string>
        {
            reason,
            Loc.Get(distribution is Distribution.Portable or Distribution.Development ? "AdminGuide_StepsPortable" : "AdminGuide_StepsStart"),
            Loc.Get("AdminGuide_DragDropLimit"),
        };
        if (forDisk)
        {
            texts.Add(Loc.Get("AdminGuide_UsbAlternative"));
        }

        if (!canRestart)
        {
            texts.Add(Loc.Get("AdminGuide_StoreDownload"));
        }

        return (texts, canRestart);
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
