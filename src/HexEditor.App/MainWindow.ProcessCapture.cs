using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Processes;
using HexEditor.Core.Saving;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// プロセスのスナップショット・ミニダンプの保存 (ENG-35)。保存のダイアログで対象 (読めるすべての領域 / 選んだ領域・モジュール / 書き込み可能な
/// 領域だけ)、取得中の一時停止、保存先を選び、長時間処理 (進捗・キャンセル。キャンセル時は途中のファイルを削除) で書く。完了時に、読めなかった
/// 領域と一時停止できなかったことを知らせる。ANA-09 の「スナップショットを作成」も同じ対象・一時停止の選択を使う (保存先の代わりに名前)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>対象・一時停止の選択の部品 (保存のダイアログと「スナップショットを作成」で共通)。</summary>
    private sealed class CaptureOptionsInput
    {
        public required RadioButton All { get; init; }

        public required RadioButton Selected { get; init; }

        public required RadioButton Writable { get; init; }

        public required CheckBox Pause { get; init; }

        public (long Start, long Length)? Selection { get; init; }

        public ProcessCaptureOptions Options(IVolumeInfoProvider? volumes) => new()
        {
            Scope = Selected.IsChecked == true ? SnapshotScope.Selected : Writable.IsChecked == true ? SnapshotScope.WritableOnly : SnapshotScope.AllReadable,
            SelectedRanges = Selection is { } s && Selected.IsChecked == true ? [s] : [],
            PauseTarget = Pause.IsChecked == true,
            Volumes = volumes,
        };
    }

    /// <summary>対象と一時停止の欄を作る (ENG-35 の仕様 1・2)。メモリマップのパネルで何も選んでいなければ「選んだ領域・モジュール」は選べない。</summary>
    private CaptureOptionsInput BuildCaptureOptions(StackPanel panel, bool includeScope)
    {
        (long, long)? selection = FindPanelContent<Panels.MemoryMapPanel>(MemoryMapPanelId)?.SelectedRange;
        var input = new CaptureOptionsInput
        {
            All = DialogParts.Radio("Capture_ScopeAll", Loc.Get("Capture_ScopeAll"), "captureScope", true),
            Selected = DialogParts.Radio("Capture_ScopeSelected", Loc.Get("Capture_ScopeSelected"), "captureScope", false),
            Writable = DialogParts.Radio("Capture_ScopeWritable", Loc.Get("Capture_ScopeWritable"), "captureScope", false),
            Pause = DialogParts.Check("Capture_Pause", Loc.Get("Capture_Pause"), false),
            Selection = selection,
        };
        input.Selected.IsEnabled = selection is not null;
        if (includeScope)
        {
            var header = new TextBlock { Text = Loc.Get("Capture_Scope") };
            panel.Children.Add(header);
            panel.Children.Add(input.All);
            panel.Children.Add(input.Selected);
            panel.Children.Add(input.Writable);
        }

        panel.Children.Add(input.Pause);
        return input;
    }

    /// <summary>「スナップショットを保存…」「ミニダンプとして保存…」(ENG-35 の画面): 保存のダイアログ (対象・一時停止・保存先)。</summary>
    private async Task SaveProcessCaptureAsync(bool minidump)
    {
        if (Vm.Selected is not { ProcessMemory: { } process } doc)
        {
            return;
        }

        string extension = minidump ? MinidumpWriter.Extension : HexSnapshot.Extension;
        string suggested = SafeFileName($"{Path.GetFileNameWithoutExtension(process.ProcessName)} {DateTime.Now:yyyyMMdd-HHmmss}") + extension;
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };

        // ミニダンプは「全メモリを含む」ので対象を選ばない (仕様 6)。
        CaptureOptionsInput input = BuildCaptureOptions(panel, includeScope: !minidump);
        var path = DialogParts.Field("Capture_Path", Loc.Get("Capture_Path"), Path.Combine(folder, suggested), monospace: false);
        var browse = new Button { Content = Loc.Get("Capture_Browse") };
        AutomationProperties.SetAutomationId(browse, "Capture_Browse");
        browse.Click += async (_, _) =>
        {
            if (await PickCapturePathAsync(Path.GetFileName(path.Text), minidump) is { } chosen)
            {
                path.Text = chosen;
            }
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(path);
        Grid.SetColumn(browse, 1);
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(browse);
        panel.Children.Add(row);

        ContentDialog dialog = DialogParts.Dialog(Root, minidump ? "SaveMinidumpDialog" : "SaveSnapshotDialog",
            Loc.Get(minidump ? "Capture_MinidumpTitle" : "Capture_SnapshotTitle"), panel, Loc.Get("Capture_Save"));
        dialog.PrimaryButtonClick += (_, e) =>
        {
            bool valid = path.Text.Trim().Length > 0 && Path.IsPathFullyQualified(path.Text.Trim());
            DialogParts.MarkInvalid(path, !valid);
            e.Cancel = !valid;
        };
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string target = path.Text.Trim();
        if (!target.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            target += extension;
        }

        await SaveProcessCaptureCoreAsync(doc, process, target, input.Options(TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance), minidump);
    }

    private async Task<string?> PickCapturePathAsync(string suggested, bool minidump)
    {
        if (TestHooks.TrySavePicker(suggested, out string? path))
        {
            return path;
        }

        var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.SaveCapture" };
        picker.FileTypeChoices.Add(Loc.Get(minidump ? "FileType_Minidump" : "FileType_Snapshot"), [minidump ? MinidumpWriter.Extension : HexSnapshot.Extension]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>最後に保存したスナップショット・ミニダンプの結果 (テスト用)。</summary>
    internal ProcessCaptureSummary? LastCaptureSummary { get; private set; }

    /// <summary>
    /// 保存の本体 (長時間処理。進捗・キャンセル。ENG-35 の「巨大ファイル・長時間処理」)。テスト用の命令からも呼ぶ。失敗の理由はそれぞれ示す
    /// (空き容量不足、取得中のプロセスの終了、権限不足、書き込みの失敗)。
    /// </summary>
    internal async Task<bool> SaveProcessCaptureCoreAsync(DocumentViewModel doc, ProcessMemoryByteSource process, string path,
        ProcessCaptureOptions options, bool minidump)
    {
        string name = Loc.Format(minidump ? "Capture_SavingMinidump" : "Capture_SavingSnapshot", Path.GetFileName(path));
        try
        {
            ProcessCaptureSummary summary = await Vm.Operations.RunAsync(name, Core.Operations.OperationKind.ReadOnly, doc.Document, null,
                op => Task.FromResult(minidump ? MinidumpWriter.Write(process, path, options, op) : HexSnapshot.Write(process, path, options, op).Summary));
            LastCaptureSummary = summary;
            AppLog.Info($"{(minidump ? "Minidump" : "Snapshot")} saved: {path}");
            ShowStatusMessage(Loc.Format("Capture_Saved", Path.GetFileName(path)));
            ReportCaptureSummary(doc, summary);
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowStatusMessage(Loc.Get("Capture_Cancelled"));
        }
        catch (InsufficientSpaceException ex)
        {
            ShowNotice(Loc.Format("Error_NoSpace", ex.Drive, ex.Required.ToString("N0"), ex.Available.ToString("N0")), InfoBarSeverity.Error, doc);
        }
        catch (ProcessExitedDuringCaptureException)
        {
            ShowNotice(Loc.Get("Capture_ProcessExited"), InfoBarSeverity.Error, doc);
        }
        catch (ProcessAccessException ex) when (ex.Failure == ProcessOpenFailure.AccessDenied)
        {
            ShowNotice(Loc.Format("Capture_Error", Loc.Get("Snapshot_AccessDenied")), InfoBarSeverity.Error, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice(Loc.Format("Capture_Error", ex.Message), InfoBarSeverity.Error, doc);
        }

        return false;
    }

    /// <summary>完了時の知らせ: 一時停止できなかった (警告して続けた。仕様 2)、読めなかった領域 (「N 個の領域 (合計 M バイト) を読めませんでした」)。</summary>
    private void ReportCaptureSummary(DocumentViewModel doc, ProcessCaptureSummary summary)
    {
        if (summary.PauseFailed)
        {
            ShowNotice(Loc.Get("Capture_PauseFailed"), InfoBarSeverity.Warning, doc);
        }

        if (summary.UnreadableRegions > 0)
        {
            ShowNotice(Loc.Format("Capture_Unreadable", summary.UnreadableRegions, Size(summary.UnreadableBytes)), InfoBarSeverity.Informational, doc);
        }
    }
}
