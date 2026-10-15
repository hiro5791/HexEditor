using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using CompareOptions = HexEditor.Core.Compare.CompareOptions;
using HexEditor.Core.Devices;
using HexEditor.Core.Expressions;
using HexEditor.Core.Processes;
using HexEditor.Core.View;
using HexEditor.Platform;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// コマンドラインで渡された引数の処理 (AUTO-37)。<c>hexed open</c> が絶対パスにした引数と、<c>HexEditor.exe</c> に直接渡された引数を同じに扱う。
/// 起動時と、既存のインスタンスに転送された起動 (UI-15) の両方から呼ばれる。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// コマンドラインで指定したものを開く: ファイル (<c>--readonly</c>)、比較 (<c>--compare</c>)、ディスク・ボリューム・プロセス、新しい空のドキュメント、
    /// 「ディスクを開く」のダイアログ。続けて、開いたドキュメントに位置・選択・文字コード・テンプレート・スクリプトを適用する。
    /// <c>--wait-id</c> があれば、開いたドキュメントがすべて閉じられたときに合図を送る (<see cref="WaitSignal"/>)。
    /// </summary>
    public void OpenFromCommandLine(CommandLine commandLine, bool activate)
    {
        var opened = new List<DocumentViewModel>();
        foreach (string file in commandLine.Files)
        {
            if (TryOpen(file, readOnly: commandLine.ReadOnly) is { } doc)
            {
                opened.Add(doc);
            }
        }

        // ジャンプリストの「新規作成」(--new-document。UI-35)。
        if (commandLine.NewDocument)
        {
            opened.Add(Vm.NewDocument());
        }

        // ジャンプリストの「ディスクを開く」(--open-disk。UI-35)。
        if (commandLine.OpenDisk)
        {
            DispatcherQueue.TryEnqueue(() => _ = Commands.ExecuteAsync("file.openDisk"));
        }

        bool devices = commandLine.Disk is not null || commandLine.Volume is not null || commandLine.Process is not null;
        bool compare = commandLine.Compare.Count == 2;
        if (devices || compare)
        {
            // 非同期の処理 (デバイスの一覧の取得、管理者権限の確認、比較) が終わってから、位置などを適用して合図を送る。
            _ = FinishCommandLineAsync(commandLine, opened, devices, compare, activate);
            return;
        }

        FinishCommandLine(commandLine, opened, activate);
    }

    private async Task FinishCommandLineAsync(CommandLine commandLine, List<DocumentViewModel> opened, bool devices, bool compare, bool activate)
    {
        try
        {
            if (devices)
            {
                await OpenDevicesFromCommandLineAsync(commandLine, opened);
            }

            if (compare)
            {
                await OpenCompareFromCommandLineAsync(commandLine.Compare[0], commandLine.Compare[1]);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning($"Command line open failed: {ex.GetType().Name}");
        }

        FinishCommandLine(commandLine, opened, activate);
    }

    private void FinishCommandLine(CommandLine commandLine, List<DocumentViewModel> opened, bool activate)
    {
        // 位置・選択・文字コードは最後に開いたドキュメントに (同じファイルが既に開いていれば、そのタブをアクティブにして適用する。AUTO-37 の仕様 4)。
        DocumentViewModel? target = opened.LastOrDefault() ?? (commandLine.HasOpenActions ? Vm.Selected : null);
        if (target is not null)
        {
            ApplyCommandLineToDocument(target, commandLine);
        }

        if (commandLine.WaitId is { } id)
        {
            WaitSignals.Register(id, opened);
        }

        if (activate)
        {
            Activate();
        }
    }

    private void ApplyCommandLineToDocument(DocumentViewModel doc, CommandLine commandLine)
    {
        if (doc.Document.IsDisposed)
        {
            return;
        }

        if (WindowManager.OwnerOf(doc) is { } owner)
        {
            owner.Vm.Selected = doc;
        }

        EditorState editor = doc.Editor;
        if (commandLine.Encoding is { } encoding)
        {
            if (IsKnownEncoding(encoding))
            {
                editor.TextEncoding = TextEncoding.FromId(encoding.Trim());
            }
            else
            {
                ShowNotice(Loc.Format("CmdLine_UnknownEncoding", encoding), InfoBarSeverity.Error, doc);
            }
        }

        var context = new EditorExpressionContext(editor);
        if (commandLine.Offset is { } offsetText)
        {
            // コマンドラインでは表示状態がないため、接頭辞のない数値は 10 進 (AUTO-36 の仕様 4)。
            if (!ExpressionEvaluator.TryEvaluate(offsetText, context, out long offset, out ExpressionException? error, DefaultRadix.Decimal))
            {
                ShowNotice(Loc.Format("CmdLine_BadValue", "--offset", offsetText, (error?.Position ?? 0) + 1), InfoBarSeverity.Error, doc);
            }
            else if (offset < 0 || offset > editor.Layout.MaxCursor)
            {
                ShowNotice(Loc.Format("CmdLine_OutsideFile", "--offset", offsetText, doc.Document.Length), InfoBarSeverity.Error, doc);
            }
            else
            {
                editor.GoTo(offset);
            }
        }

        if (commandLine.Select is { } selectText)
        {
            if (!RangeExpression.TryEvaluate(selectText, context, DefaultRadix.Decimal, out long start, out long length, out ExpressionException? error))
            {
                ShowNotice(Loc.Format("CmdLine_BadValue", "--select", selectText, (error?.Position ?? 0) + 1), InfoBarSeverity.Error, doc);
            }
            else if (!RangeExpression.IsInside(start, length, doc.Document.Length))
            {
                ShowNotice(Loc.Format("CmdLine_OutsideFile", "--select", selectText, doc.Document.Length), InfoBarSeverity.Error, doc);
            }
            else
            {
                editor.Select(start, length, cursorAtStart: true);
            }
        }

        if (commandLine.Template is { } template)
        {
            RunCommandLineHook(CommandLineActions.ApplyTemplate, "--template", w => CommandLineActions.ApplyTemplate!(w, doc, template));
        }

        if (commandLine.Run is { } script)
        {
            RunCommandLineHook(CommandLineActions.RunScript, "--run", w => CommandLineActions.RunScript!(w, script));
        }
    }

    /// <summary>テンプレート・スクリプトの機能 (別の領域) がつながっていれば呼ぶ。なければ「この版では使えません」と知らせる。</summary>
    private void RunCommandLineHook(object? hook, string option, Func<MainWindow, Task> run)
    {
        if (hook is null)
        {
            ShowNotice(Loc.Format("CmdLine_NotAvailable", option), InfoBarSeverity.Warning, Vm.Selected);
            return;
        }

        _ = run(this);
    }

    private static bool IsKnownEncoding(string id)
    {
        id = id.Trim();
        return TextEncoding.SelectableIds.Contains(id, StringComparer.OrdinalIgnoreCase) || id.Equals("oem", StringComparison.OrdinalIgnoreCase)
            || EncodingCatalog.Find(id) is not null
            || id.StartsWith("cp", StringComparison.OrdinalIgnoreCase) && int.TryParse(id.AsSpan(2), out int page) && page > 0;
    }

    // ---- 比較 (--compare) ----

    /// <summary>2 つのファイルを比較の画面で開く (ANA-01)。比較の方式は前回の設定。</summary>
    private Task OpenCompareFromCommandLineAsync(string first, string second)
    {
        CompareOptions options = CompareOptions.FromJson(CommandService.State?.Get(CompareOptionsKey));
        return OpenCompareAsync(
            new CompareTargetSpec(CompareSourceKind.File, null, Path.GetFullPath(first), 0, null),
            new CompareTargetSpec(CompareSourceKind.File, null, Path.GetFullPath(second), 0, null), options);
    }

    // ---- ディスク・ボリューム・プロセス (--disk、--volume、--process) ----

    /// <summary>
    /// 指定のデバイス・プロセスを、GUI の通常の手順で開く (管理者権限が要る場合は補助プロセスの昇格・案内。ENG-28 の仕様 10、AUTO-37 の仕様 6)。
    /// コマンドラインからは常に読み取り専用で開く (書き込みは開いた後に解除する。ENG-14)。
    /// </summary>
    private async Task OpenDevicesFromCommandLineAsync(CommandLine commandLine, List<DocumentViewModel> opened)
    {
        if (commandLine.Disk is not null || commandLine.Volume is not null)
        {
            DeviceCatalog catalog;
            try
            {
                catalog = App.Devices.EnumerateDevices();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowNotice(Loc.Format("OpenDisk_EnumError", ex.Message), InfoBarSeverity.Error);
                return;
            }

            List<DeviceListItem> items = DeviceItems(catalog);
            if (commandLine.Disk is { } disk)
            {
                await OpenListedDeviceAsync(items, DeviceSelector.TryParseDisk(disk, out int number) ? DeviceSelector.FindDisk(catalog, number)?.Path : null,
                    "CmdLine_DiskNotFound", disk, opened);
            }

            if (commandLine.Volume is { } volume)
            {
                await OpenListedDeviceAsync(items, DeviceSelector.TryParseVolume(volume, out char letter) ? DeviceSelector.FindVolume(catalog, letter)?.Path : null,
                    "CmdLine_VolumeNotFound", volume, opened);
            }
        }

        if (commandLine.Process is { } processSpec)
        {
            IReadOnlyList<ProcessEntry> processes;
            try
            {
                processes = App.Devices.EnumerateProcesses();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowNotice(Loc.Format("OpenProcess_EnumError", ex.Message), InfoBarSeverity.Error);
                return;
            }

            ProcessSelection selection = DeviceSelector.SelectProcess(processes, processSpec);
            if (selection.IsAmbiguous)
            {
                ShowNotice(Loc.Format("CmdLine_ProcessAmbiguous", processSpec, selection.Candidates.Count), InfoBarSeverity.Error);
            }
            else if (selection.Match is null)
            {
                ShowNotice(Loc.Format("CmdLine_ProcessNotFound", processSpec), InfoBarSeverity.Error);
            }
            else if (await OpenProcessEntryAsync(selection.Match, readOnly: true) is { } doc)
            {
                opened.Add(doc);
            }
        }
    }

    private async Task OpenListedDeviceAsync(List<DeviceListItem> items, string? path, string notFoundKey, string spec, List<DocumentViewModel> opened)
    {
        DeviceListItem? item = path is null ? null : items.FirstOrDefault(i => string.Equals(i.CreateInfo().Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            ShowNotice(Loc.Format(notFoundKey, spec), InfoBarSeverity.Error);
            return;
        }

        if (await OpenDeviceItemAsync(item, readOnly: true) is { } doc)
        {
            opened.Add(doc);
        }
    }
}

/// <summary>
/// コマンドラインの <c>--template</c>・<c>--run</c> (AUTO-37) を実行する機能のつなぎ目。テンプレート (07)・スクリプト (08) の機能が
/// 起動時に差し込む。差し込まれていない間は「この版では使えません」と知らせる。
/// </summary>
public static class CommandLineActions
{
    /// <summary>ドキュメントにテンプレート (名前またはパス) を適用して開く。</summary>
    public static Func<MainWindow, DocumentViewModel, string, Task>? ApplyTemplate { get; set; }

    /// <summary>スクリプトを実行する (信頼の確認は通常どおり)。</summary>
    public static Func<MainWindow, string, Task>? RunScript { get; set; }
}
