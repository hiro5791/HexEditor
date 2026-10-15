using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.App.ViewModels;
using HexEditor.Core.Commands;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Library;
using HexEditor.Core.Scripting.Macros;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>マクロの記録と再生 (08 の AUTO-27、AUTO-28) の画面: コマンド、記録の方式の選択、保存のダイアログ、繰り返し再生のダイアログ、ステータスバーの表示。</summary>
public sealed partial class MainWindow
{
    private static MacroService Macro => ScriptWorkspace.Macros;

    private void RegisterMacroCommands()
    {
        Commands.Register("macro.record", ToggleMacroRecordingAsync);
        Commands.Register("macro.playLast", () => PlayMacro(MacroPlayback.Once, null),
            () => Macro.IsRecording ? CommandState.Unavailable(Loc.Get("Macro_WhileRecording"))
                : Macro.LastScript is null && Macro.LastPath is null ? CommandState.Unavailable(Loc.Get("Macro_NoLast")) : CommandState.Available);
        Commands.Register("macro.repeat", RepeatMacroAsync,
            () => Macro.IsRecording ? CommandState.Unavailable(Loc.Get("Macro_WhileRecording")) : NeedsDocument());
        Macro.Changed += OnMacroStateChanged;
        Macro.Unrecordable += OnMacroUnrecordable;
        // 閉じる操作が取り消されたときも Closed は来る。実際に閉じるときだけ外す。
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                Macro.Changed -= OnMacroStateChanged;
                Macro.Unrecordable -= OnMacroUnrecordable;
            }
        };
        Vm.Documents.CollectionChanged += (_, e) =>
        {
            // ファイルを開いた操作を記録する (パスを引数として記録する。AUTO-27 の仕様 2)。
            foreach (DocumentViewModel d in e.NewItems?.OfType<DocumentViewModel>() ?? [])
            {
                if (d.FilePath is { } path && !d.IsPending)
                {
                    MacroHooks.Opened(path);
                }
            }
        };
    }

    private void OnMacroStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateRecordingStatus();
        UpdateCommandStates();
    });

    private void OnMacroUnrecordable(object? sender, UnrecordableEventArgs e) => DispatcherQueue.TryEnqueue(() =>
        ShowStatusMessage(Loc.Format("Macro_UnrecordableStatus", e.Name), TimeSpan.FromSeconds(4)));

    /// <summary>ステータスバーの「● 記録中 (n)」(AUTO-27 の仕様 4)。クリックで記録を止める。</summary>
    private void UpdateRecordingStatus()
    {
        bool recording = Macro.IsRecording;
        StatusRecording.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording)
        {
            StatusRecordingText.Text = Loc.Format("Macro_StatusRecording", Macro.Recorder!.Count);
            AutomationProperties.SetName(StatusRecording, Loc.Format("Macro_StatusRecordingName", Macro.Recorder.Count));
        }

        QueueStatusBarLayout();
    }

    private async void StatusRecording_Click(object sender, RoutedEventArgs e) => await StopMacroRecordingAsync();

    // ---- 記録 ----

    private async Task ToggleMacroRecordingAsync()
    {
        if (Macro.IsRecording)
        {
            await StopMacroRecordingAsync();
            return;
        }

        if (await ChoosePositionModeAsync() is not { } mode)
        {
            return;
        }

        Macro.Start(mode);
        ShowStatusMessage(Loc.Get("Macro_Started"), TimeSpan.FromSeconds(3));
    }

    /// <summary>位置の記録方式の選択 (AUTO-27 の仕様 3)。前回の選択を既定にする。「今後は確認しない」を選んでいれば尋ねない。</summary>
    private async Task<MacroPositionMode?> ChoosePositionModeAsync()
    {
        MacroPositionMode previous = AppState.GetString(MacroService.PositionModeKey, "relative") == "absolute" ? MacroPositionMode.Absolute : MacroPositionMode.Relative;
        if (AppState.GetString(MacroService.AskPositionKey, "true") == "false")
        {
            return previous;
        }

        RadioButton relative = DialogParts.Radio("Macro_ModeRelative", Loc.Get("Macro_ModeRelative"), "MacroPosition", previous == MacroPositionMode.Relative);
        RadioButton absolute = DialogParts.Radio("Macro_ModeAbsolute", Loc.Get("Macro_ModeAbsolute"), "MacroPosition", previous == MacroPositionMode.Absolute);
        CheckBox dontAsk = DialogParts.Check("Macro_DontAsk", Loc.Get("Macro_DontAsk"), false);
        var body = new StackPanel { Spacing = 8, MinWidth = 380 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Macro_ModeIntro"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(relative);
        body.Children.Add(absolute);
        body.Children.Add(dontAsk);
        ContentDialog dialog = DialogParts.Dialog(Root, "MacroPositionDialog", Loc.Get("Macro_StartTitle"), body, Loc.Get("Macro_Start"));
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        MacroPositionMode mode = absolute.IsChecked == true ? MacroPositionMode.Absolute : MacroPositionMode.Relative;
        AppState.SetString(MacroService.PositionModeKey, mode == MacroPositionMode.Absolute ? "absolute" : "relative");
        if (dontAsk.IsChecked == true)
        {
            AppState.SetString(MacroService.AskPositionKey, "false");
        }

        return mode;
    }

    private async Task StopMacroRecordingAsync()
    {
        MacroRecorder? recorder = Macro.Stop();
        if (recorder is null)
        {
            return;
        }

        UpdateRecordingStatus();
        if (recorder.Count == 0)
        {
            ShowStatusMessage(Loc.Get("Macro_NothingRecorded"), TimeSpan.FromSeconds(4));
            return;
        }

        await ShowMacroSaveDialogAsync(recorder);
    }

    /// <summary>停止時の保存ダイアログ (名前・保存先・ショートカット。AUTO-27 の仕様 6)。保存しなくても「直前のマクロ」として再生できる。</summary>
    private async Task ShowMacroSaveDialogAsync(MacroRecorder recorder)
    {
        string defaultFolder = MacroStore.FolderOf(ScriptingService.Folders);
        TextBox name = DialogParts.Field("MacroSave_Name", Loc.Get("MacroSave_Name"), recorder.DefaultName(), monospace: false);
        TextBox folder = DialogParts.Field("MacroSave_Folder", Loc.Get("MacroSave_Folder"), defaultFolder, monospace: false);
        folder.FlowDirection = FlowDirection.LeftToRight;
        TextBox shortcut = DialogParts.Field("MacroSave_Shortcut", Loc.Get("MacroSave_Shortcut"), string.Empty, monospace: false);
        shortcut.PlaceholderText = Loc.Get("MacroSave_ShortcutHint");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        AutomationProperties.SetAutomationId(error, "MacroSave_Error");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (UIElement element in new UIElement[] { name, folder, shortcut, error })
        {
            body.Children.Add(element);
        }

        string script = recorder.ToScript();
        Macro.SetLast(script, null);
        ContentDialog dialog = DialogParts.Dialog(Root, "MacroSaveDialog", Loc.Format("MacroSave_Title", recorder.Count), body, Loc.Get("MacroSave_Save"),
            Loc.Get("MacroSave_DontSave"));
        string? savedPath = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            error.Visibility = Visibility.Collapsed;
            string chosenName = name.Text.Trim();
            if (chosenName.Length == 0)
            {
                args.Cancel = true;
                error.Text = Loc.Get("MacroSave_NameRequired");
                error.Visibility = Visibility.Visible;
                return;
            }

            KeyChord? chord = null;
            if (shortcut.Text.Trim().Length > 0)
            {
                if (!KeyChord.TryParse(shortcut.Text.Trim(), out KeyChord parsed))
                {
                    args.Cancel = true;
                    error.Text = Loc.Format("MacroSave_ShortcutInvalid", shortcut.Text.Trim());
                    error.Visibility = Visibility.Visible;
                    return;
                }

                chord = parsed;
            }

            try
            {
                string target = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : defaultFolder;
                savedPath = MacroStore.Save(ScriptingService.Folders, ScriptingService.Trust, chosenName, script, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // 書き込めない: 理由を示して、別の場所を選ばせる (AUTO-27 の「エラー」)。
                args.Cancel = true;
                error.Text = Loc.Format("MacroSave_Failed", ex.Message);
                error.Visibility = Visibility.Visible;
                folder.Focus(FocusState.Programmatic);
                return;
            }

            if (chord is { } c)
            {
                AssignMacroShortcut(savedPath, c);
            }
        };
        await dialog.ShowQueuedAsync();
        if (savedPath is not null)
        {
            Macro.SetLast(File.ReadAllText(savedPath), savedPath);
            ScriptWorkspace.Library.Refresh();
            ScriptWorkspace.SyncCommands();
            ShowStatusMessage(Loc.Format("MacroSave_Saved", Path.GetFileName(savedPath)), TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>保存したマクロのコマンドにショートカットを割り当てる (標準のマクロのフォルダに保存したときだけ)。</summary>
    private static void AssignMacroShortcut(string path, KeyChord chord)
    {
        string macrosFolder = MacroStore.FolderOf(ScriptingService.Folders);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null || !string.Equals(Path.GetFullPath(macrosFolder).TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 一覧に反映してからコマンド ID を決める (登録されたコマンドにだけ割り当てられる)。
        ScriptWorkspace.Library.Refresh();
        ScriptWorkspace.SyncCommands();
        string id = ScriptCommandIds.For(ScriptRootKind.Macros, "macros", Path.GetFileName(path));
        if (CommandService.Catalog.Contains(id))
        {
            CommandService.Keys.Add(id, new KeyBinding(chord), replaceConflicts: false);
        }
    }

    // ---- 再生 (AUTO-28) ----

    /// <summary>マクロを再生する。<paramref name="macroPath"/> が null なら「直前のマクロ」。</summary>
    private void PlayMacro(MacroPlayback playback, string? macroPath)
    {
        string? path = macroPath ?? Macro.LastPath;
        string? script = macroPath is null && path is null ? Macro.LastScript : null;
        if (path is null && script is null)
        {
            ShowStatusMessage(Loc.Get("Macro_NoLast"), TimeSpan.FromSeconds(3));
            return;
        }

        string text;
        try
        {
            text = path is not null ? File.ReadAllText(path) : script!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Macro_ReadFailed", ex.Message), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            return;
        }

        if (path is not null)
        {
            Macro.SetLast(text, path);
        }

        string displayName = ScriptMetadata.Parse(text).DisplayName(CultureInfo.CurrentUICulture.Name, path is not null ? Path.GetFileNameWithoutExtension(path) : Loc.Get("Macro_LastName"));
        ScriptRun run = ScriptingService.Start(new ScriptRunRequest
        {
            Path = path,
            Code = path is null ? text : null,
            Origin = path is null ? ScriptOrigin.UnsavedMacro : ScriptOrigin.File,
            DisplayName = displayName,
            CodeWrapper = MacroPlayer.Wrap,
            PresetArgs = MacroPlayer.Arguments(playback),
            SkipParameterDialog = true,
        });
        _ = run.Completion.ContinueWith(task => DispatcherQueue.TryEnqueue(() => ReportMacroResult(text, task.Result)), TaskScheduler.Default);
    }

    /// <summary>再生が失敗したら、何回目のどの操作で止まったかを知らせる (AUTO-28 の仕様 5)。</summary>
    private void ReportMacroResult(string macro, ScriptRunResult result)
    {
        if (result.Status != ScriptRunStatus.Failed)
        {
            return;
        }

        (int iteration, int operation) = MacroPlayer.LocationOf(result.Message);
        string message = System.Text.RegularExpressions.Regex.Replace(result.Message ?? string.Empty, @"\[macro \d+:\d+\]\s*", string.Empty);
        string text = iteration > 0 && MacroPlayer.DescribeOperation(macro, operation) is { } op
            ? Loc.Format("Macro_FailedAt", iteration, op, message)
            : Loc.Format("Macro_Failed", message);
        ShowNotice(text, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
    }

    private async Task RepeatMacroAsync()
    {
        var macros = new List<(string Name, string? Path)> { (Loc.Get("Macro_LastName"), null) };
        macros.AddRange(ScriptWorkspace.Library.Scripts.Where(n => n.IsMacro).Select(n => (n.Name, (string?)n.Path)));
        ComboBox macroBox = DialogParts.Combo("MacroRepeat_Macro", Loc.Get("MacroRepeat_Macro"), macros.Select(m => m.Name), 0);
        RadioButton count = DialogParts.Radio("MacroRepeat_Count", Loc.Get("MacroRepeat_Count"), "MacroRepeat", true);
        TextBox times = DialogParts.Field("MacroRepeat_Times", Loc.Get("MacroRepeat_Times"), "10", monospace: false);
        RadioButton toEnd = DialogParts.Radio("MacroRepeat_UntilEnd", Loc.Get("MacroRepeat_UntilEnd"), "MacroRepeat", false);
        RadioButton perResult = DialogParts.Radio("MacroRepeat_PerResult", Loc.Get("MacroRepeat_PerResult"), "MacroRepeat", false);
        RadioButton inSelection = DialogParts.Radio("MacroRepeat_InSelection", Loc.Get("MacroRepeat_InSelection"), "MacroRepeat", false);
        SearchResultsPanel? results = SearchResults;
        perResult.IsEnabled = results is { HasResults: true };
        inSelection.IsEnabled = Editor is { HasSelection: true };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        var body = new StackPanel { Spacing = 8, MinWidth = 400 };
        foreach (UIElement element in new UIElement[] { macroBox, count, times, toEnd, perResult, inSelection, error })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "MacroRepeatDialog", Loc.Get("MacroRepeat_Title"), body, Loc.Get("MacroRepeat_Play"));
        MacroPlayback? chosen = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (count.IsChecked == true)
            {
                if (!int.TryParse(times.Text.Trim().Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    || n < 1 || n > MacroPlayback.MaxCount)
                {
                    args.Cancel = true;
                    error.Text = Loc.Format("MacroRepeat_CountInvalid", MacroPlayback.MaxCount.ToString("N0", CultureInfo.CurrentCulture));
                    error.Visibility = Visibility.Visible;
                    DialogParts.MarkInvalid(times, true);
                    return;
                }

                chosen = new MacroPlayback(MacroRepeatMode.Count, n);
            }
            else if (toEnd.IsChecked == true)
            {
                chosen = new MacroPlayback(MacroRepeatMode.UntilEnd);
            }
            else if (inSelection.IsChecked == true)
            {
                chosen = new MacroPlayback(MacroRepeatMode.InSelection);
            }
            else
            {
                chosen = new MacroPlayback(MacroRepeatMode.PerResult, ResultStarts: ResultStarts(results));
            }
        };
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && chosen is { } playback)
        {
            PlayMacro(playback, macros[Math.Max(0, macroBox.SelectedIndex)].Path);
        }
    }

    /// <summary>結果一覧の各一致の開始位置 (最大 100 万件)。</summary>
    private static IReadOnlyList<long> ResultStarts(SearchResultsPanel? panel) =>
        panel?.Results is { } results
            ? [.. results.GetRange(0, (int)Math.Min(MacroPlayback.MaxCount, results.LongCount)).Select(m => m.Offset)]
            : [];
}
