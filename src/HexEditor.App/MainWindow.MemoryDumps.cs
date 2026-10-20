using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.MemoryDumps;
using HexEditor.Core.Processes;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 物理メモリ (RAM) とメモリダンプ (01 の ENG-36)。物理メモリを直接読む機能はない (ドライバを作らない方針。仕様 1)。他のツールで取ったダンプを
/// 「メモリダンプを開く」で開く: raw (物理アドレス = オフセット)、Windows のクラッシュダンプ (物理アドレス空間、含まれないページは「未割り当て」)、
/// ミニダンプ (プロセスの仮想アドレス空間。メモリマップのパネルも使える)。どれも読み取り専用。形式が壊れていれば raw として開くかを尋ねる。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterMemoryDumpCommands() =>
        Commands.Register("file.openMemoryDump", async () =>
        {
            if (await PickOneFileAsync("HexEditor.OpenMemoryDump") is { } path)
            {
                await OpenMemoryDumpAsync(path);
            }
        });

    /// <summary>メモリダンプとして開く。開いたタブ (開けなければ null) を返す。</summary>
    internal async Task<DocumentViewModel?> OpenMemoryDumpAsync(string path)
    {
        OpenedMemoryDump opened;
        try
        {
            opened = await Task.Run(() => MemoryDumpOpener.Open(path));
        }
        catch (MemoryDumpFormatException ex)
        {
            // 「メモリダンプの形式を読めませんでした。raw として開きますか」(ENG-36 の「エラー」)。
            AppLog.Info($"Memory dump format error: {ex.Message}");
            var body = new TextBlock { Text = Loc.Format("MemoryDump_FormatError", Path.GetFileName(path)), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
            AutomationProperties.SetAutomationId(body, "MemoryDump_FormatErrorText");
            ContentDialog dialog = Controls.DialogParts.Dialog(Root, "MemoryDumpFormatDialog", Loc.Get("MemoryDump_FormatErrorTitle"), body, Loc.Get("MemoryDump_OpenRaw"));
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            opened = MemoryDumpOpener.Open(path, forceRaw: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("MemoryDump_OpenFailed", ex.Message), InfoBarSeverity.Error);
            return null;
        }

        DocumentViewModel vm = opened.Source switch
        {
            SnapshotByteSource snapshot => Vm.OpenSnapshotSource(snapshot),
            _ => Vm.OpenMemoryDumpSource(opened.Source, path),
        };
        vm.MemoryDump = opened.Info;
        ShowNotice(MemoryDumpSummary(opened.Info), InfoBarSeverity.Informational, vm);
        UpdateTitle();
        return vm;
    }

    /// <summary>ダンプの種類と取得日時 (ENG-36 の「画面」。ドキュメントのプロパティの画面がないため、タブのツールチップと通知に出す)。</summary>
    internal static string MemoryDumpSummary(MemoryDumpInfo info)
    {
        string kind = Loc.Get("MemoryDump_Kind_" + info.Kind);
        if (info.CapturedUtc is not { } captured)
        {
            return Loc.Format("MemoryDump_OpenedNoTime", kind);
        }

        string time = ForensicsService.TimeContext.Detail(Core.Forensics.Time.ForensicTimestamp.FromUtc(DateTime.SpecifyKind(captured, DateTimeKind.Utc)), Loc.Get("Time_Invalid"));
        return Loc.Format("MemoryDump_Opened", kind, time);
    }
}
