using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Encryption;
using HexEditor.Core.Forensics.Time;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 暗号化ボリュームの検出 (12 の FOR-29)。「暗号化の情報を表示」でドキュメント (ディスク・ボリューム・イメージ) の元のデータ
/// (<c>Document.DataSnapshot</c>) のパーティションを調べ、暗号化のメタデータを「暗号化の情報」ダイアログに出す。復号はしない。
/// ロック解除済みの BitLocker のボリュームが見つかれば、論理ボリュームとして開く案内とボタンを出す (仕様 3)。
/// パーティション解析 (FOR-06) とファイルシステムの判定 (FOR-09) は <see cref="EncryptionDetector.Detect"/> / <see cref="EncryptionDetector.FileSystemBlockReason"/>
/// を使う (ファイルシステムブラウザは暗号化されたボリュームを開かない。仕様 4)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>最後に調べた結果 (テスト用の命令が読む)。</summary>
    internal IReadOnlyList<EncryptionScanEntry>? LastEncryptionScan { get; private set; }

    /// <summary>最後のダイアログで「論理ボリュームとして開く」を出したボリューム (テスト用)。</summary>
    internal VolumeDeviceInfo? LastEncryptionVolume { get; private set; }

    private void InitializeEncryption() =>
        Commands.Register("forensics.encryption.info", () => ShowEncryptionInfoAsync(), NeedsDocument);

    /// <summary>ドキュメントを調べて結果を返す (ダイアログを出さない。テストとダイアログが使う)。</summary>
    internal async Task<IReadOnlyList<EncryptionScanEntry>> ScanEncryptionAsync(DocumentViewModel doc)
    {
        var data = new SnapshotCarveData(doc.Document.DataSnapshot);
        int sector = Math.Max(512, doc.Document.Source.LogicalSectorSize);
        IReadOnlyList<EncryptionScanEntry> scan = await Task.Run(() => EncryptionDetector.Scan(data, sector));
        LastEncryptionScan = scan;
        return scan;
    }

    private async Task ShowEncryptionInfoAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        IReadOnlyList<EncryptionScanEntry> scan = await ScanEncryptionAsync(doc);
        VolumeDeviceInfo? unlocked = UnlockedBitLockerVolume(doc, scan);
        LastEncryptionVolume = unlocked;

        var body = new StackPanel { Spacing = 10, MinWidth = 480 };
        if (!scan.Any(e => e.Encryption is not null))
        {
            body.Children.Add(Text(Loc.Get("Encryption_None"), "Encryption_None"));
        }

        foreach (EncryptionScanEntry entry in scan.Where(e => e.Encryption is not null))
        {
            body.Children.Add(EncryptionBlock(entry));
        }

        Button? open = null;
        if (unlocked is not null)
        {
            // 仕様 3: Windows がロック解除しているボリュームは、論理ボリュームとして開けば OS が復号した内容を読める。
            body.Children.Add(Text(Loc.Format("Encryption_UnlockedGuide", unlocked.DriveLetter ?? unlocked.Path), "Encryption_UnlockedGuide"));
            open = new Button { Content = Loc.Get("Encryption_OpenLogical") };
            AutomationProperties.SetAutomationId(open, "Encryption_OpenLogical");
            body.Children.Add(open);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "EncryptionInfoDialog", Loc.Get("Encryption_Title"), new ScrollViewer { Content = body, MaxHeight = 560 },
            Loc.Get("Common_Close"));
        dialog.CloseButtonText = string.Empty;
        if (open is not null)
        {
            open.Click += (_, _) =>
            {
                dialog.Hide();
                _ = OpenLogicalVolumeAsync(unlocked!);
            };
        }

        await dialog.ShowQueuedAsync();
    }

    private static TextBlock Text(string text, string? id = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        if (id is not null)
        {
            AutomationProperties.SetAutomationId(t, id);
        }

        return t;
    }

    /// <summary>ダイアログ・パーティションの詳細欄に出す行 (テスト用の命令も同じ行を返す)。</summary>
    internal static IReadOnlyList<string> EncryptionLines(EncryptionScanEntry entry)
    {
        EncryptionInfo info = entry.Encryption!;
        string invalid = Loc.Get("Time_Invalid");
        ForensicTimeContext time = ForensicsService.TimeContext;
        var lines = new List<string>
        {
            Loc.Format("Encryption_Range", entry.Index, entry.Offset.ToString("N0", CultureInfo.CurrentCulture), entry.Length.ToString("N0", CultureInfo.CurrentCulture)),
            Loc.Format("Encryption_Kind", Loc.Get("Encryption_Kind_" + info.Kind)),
        };
        if (info.BitLocker is { } b)
        {
            lines.Add(Loc.Format("Encryption_Version", b.Version));
            lines.Add(Loc.Format("Encryption_Method", b.MethodName));
            lines.Add(Loc.Format("Encryption_VolumeId", b.VolumeId.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant()));
            if (b.Created is { } created)
            {
                lines.Add(Loc.Format("Encryption_Created", time.Detail(created, invalid)));
            }

            if (b.Description is { Length: > 0 } description)
            {
                lines.Add(Loc.Format("Encryption_Description", description));
            }

            lines.Add(Loc.Format("Encryption_State", Loc.Get("Encryption_State_" + b.State)));
            if (b.EncryptedBytes is { } done)
            {
                lines.Add(Loc.Format("Encryption_Encrypted", done.ToString("N0", CultureInfo.CurrentCulture)));
            }

            lines.Add(Loc.Format("Encryption_Protectors", b.Protectors.Count));
            foreach (BitLockerProtector p in b.Protectors)
            {
                string kind = Loc.TryGet("Encryption_Protector_" + p.Type.ToString("X4", CultureInfo.InvariantCulture)) ?? Loc.Format("Encryption_Protector_Unknown", p.Type.ToString("X4", CultureInfo.InvariantCulture));
                lines.Add(Loc.Format("Encryption_Protector", kind, p.Id.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant(), time.Detail(p.Created, invalid)));
            }
        }

        if (info.Luks is { } l)
        {
            lines.Add(Loc.Format("Encryption_Method", l.Cipher));
            lines.Add(Loc.Format("Encryption_Uuid", l.Uuid));
            lines.Add(Loc.Format("Encryption_KeySlots", l.KeySlots));
        }

        foreach (ApfsVolumeEncryption v in info.ApfsVolumes)
        {
            lines.Add(Loc.Format(v.Encrypted ? "Encryption_ApfsEncrypted" : "Encryption_ApfsPlain", v.Name));
        }

        if (info.Entropy is { } entropy)
        {
            lines.Add(Loc.Format("Encryption_Entropy", entropy.ToString("0.000", CultureInfo.CurrentCulture)));
        }

        foreach (string warning in info.Warnings)
        {
            lines.Add(Loc.Get("Encryption_Warning_" + warning));
        }

        if (info.BlocksFileSystem)
        {
            lines.Add(Loc.Get("Encryption_NoBrowser"));
        }

        return lines;
    }

    private static StackPanel EncryptionBlock(EncryptionScanEntry entry)
    {
        var panel = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(panel, "Encryption_Entry_" + entry.Index.ToString(CultureInfo.InvariantCulture));
        foreach (string line in EncryptionLines(entry))
        {
            panel.Children.Add(Text(line));
        }

        return panel;
    }

    /// <summary>物理ディスクのドキュメントで、BitLocker のパーティションに対応する、ロック解除済みでドライブ文字のあるボリューム (仕様 3)。</summary>
    private VolumeDeviceInfo? UnlockedBitLockerVolume(DocumentViewModel doc, IReadOnlyList<EncryptionScanEntry> scan)
    {
        if (doc.Device is not { } device || DevicePath.DiskNumber(device.Path) is not { } disk)
        {
            return null;
        }

        DeviceCatalog catalog;
        try
        {
            catalog = DeviceService.EnumerateDevices();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

        foreach (EncryptionScanEntry entry in scan.Where(e => e.Encryption?.Kind is EncryptionKind.BitLocker or EncryptionKind.BitLockerToGo))
        {
            long start = device.Info.RangeStart + entry.Offset;
            if (catalog.Volumes.FirstOrDefault(v => v.DiskNumber == disk && v.DiskOffset == start && v.DriveLetter is { Length: > 0 }
                && v.BitLocker == BitLockerState.Unlocked) is { } volume)
            {
                return volume;
            }
        }

        return null;
    }

    private async Task OpenLogicalVolumeAsync(VolumeDeviceInfo volume)
    {
        DeviceCatalog catalog = DeviceService.EnumerateDevices();
        DeviceListItem? item = DeviceItems(catalog).FirstOrDefault(i => string.Equals(i.CreateInfo().Volume?.Path, volume.Path, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            await OpenDeviceItemAsync(item, readOnly: true);
        }
    }
}
