using System.Runtime.InteropServices;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.View;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace HexEditor.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        // 右から左に書く言語では画面全体を左右反転する。Hex ビューは自分で左から右に固定している (VIEW-01 の仕様 9)。
        Root.FlowDirection = Localization.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // ドキュメントの編集画面なので幅 1280 epx 以上で開く。
        double scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(800 * scale)));

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                WatchSelectedForTitle();
            }
        };
        Closed += MainWindow_Closed;

        // 非アクティブのときはタイトルを薄い色にする (UI-02 の仕様 6)。
        Activated += (_, e) => WindowTitle.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            e.WindowActivationState == WindowActivationState.Deactivated ? "TextFillColorDisabledBrush" : "TextFillColorPrimaryBrush"];

        InitializeStatusBar();
        InitializeDragDrop();

        // タブ (UI-09〜UI-11)。選択の変化を他の処理より先に受け、復元したタブを開く (UI-31 の仕様 6)。
        InitializeTabs();
        FindBar.MatchesChanged += (_, _) => UpdateMatchHighlights();
        InitializeSearch();
        InitializeEditingSettings();
        Vm.MaterializeFailed += (_, ex) => DispatcherQueue.TryEnqueue(() => OnMaterializeFailed(this, ex));
        InitializeRegions();
        InitializeExternalChanges();

        // 読み取り専用の解除 (EDIT-16) で書き込めるようにする処理 (ENG-14 の仕様 3)。
        ReopenForWriting = ReopenForWritingAsync;

        // データインスペクタ・ブックマーク (INSP-01〜INSP-26)。パネルとコマンドより先に作る。
        InitializeAnnotations();

        // 表示メニューの項目 (VIEW-*)。コマンドとメニューをつなぐ前に作る。
        InitializeViewMenu();

        // ズーム・全画面表示・領域の折りたたみなど、ウィンドウの枠の機能 (UI-01、UI-07、UI-08)。
        InitializeShell();

        // コマンド・ツールバー・パネル・設定画面・コマンドパレット (UI-04、UI-05、UI-16〜UI-22)。
        InitializeCommands();
        InitializePanels();
        InitializeToolPages();
        InitializePalette();
        RefreshToolbar();
        SubscribeWindowSettings();

        // 自動で閉じる通知の時間を数える (UI-36 の仕様 4)。タイマーはフィールドに持つ (ローカル変数だけだとガベージコレクションで
        // 回収され、通知が閉じなくなる)。
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.Interval = TimeSpan.FromSeconds(1);
        _noticeTimer.Tick += (_, _) => Vm.Notifications.Tick();
        _noticeTimer.Start();
    }

    /// <summary>自動で閉じる通知のタイマー (UI-36 の仕様 4)。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer = null!;

    /// <summary>テーマ・背景素材を反映する (UI-26、UI-27)。</summary>
    public void ApplyAppearance()
    {
#if HEX_TEST_HOOKS
        ElementTheme before = Root.ActualTheme;
#endif
        Appearance.Apply(this, Root, App.Settings);
        UpdateThemeMenu();
#if HEX_TEST_HOOKS
        if (Root.ActualTheme != before)
        {
            _themeChangedAtMs = TestClock.NowMs;
        }
#endif
    }

    /// <summary>設定ファイルを読んだ結果を知らせる (UI-23 の「エラー」と仕様 6)。</summary>
    public void ShowSettingsStatus(Core.Settings.SettingsLoadStatus status)
    {
        if (status == Core.Settings.SettingsLoadStatus.Broken)
        {
            // 「ファイルを開く」: 名前を変えて残したファイルを開く (UI-23 の「エラー」)。
            var actions = new List<NotificationAction>();
            if (App.Settings.BrokenFilePath is { } broken)
            {
                actions.Add(new NotificationAction(Loc.Get("Keys_OpenFile"), () => _ = OpenUriAsync(new Uri(broken))));
            }

            actions.Add(new NotificationAction(Loc.Get("Settings_OpenFolder"), () => _ = Windows.System.Launcher.LaunchFolderPathAsync(App.Settings.Folder)));
            ShowNotice(Loc.Get("Settings_Broken"), InfoBarSeverity.Warning, actions: actions);
        }
        else if (status == Core.Settings.SettingsLoadStatus.TooNew)
        {
            ShowNotice(Loc.Get("Settings_TooNew"), InfoBarSeverity.Warning);
        }
        else if (status == Core.Settings.SettingsLoadStatus.Unreadable)
        {
            ShowNotice(Loc.Format("Settings_Unreadable", App.Settings.LoadError ?? string.Empty), InfoBarSeverity.Warning);
        }
    }

    /// <summary>keybindings.json の誤り (読める部分だけを使う。UI-18 の「エラー」)。</summary>
    public void ShowKeybindingsStatus()
    {
        if (HexEditor.App.Commands.CommandService.KeybindingsTooNew)
        {
            ShowNotice(Loc.Get("Keys_FileTooNew"), InfoBarSeverity.Warning);
        }

        if (HexEditor.App.Commands.CommandService.KeybindingsError is { } error)
        {
            ShowNotice(error.Line > 0 ? Loc.Format("Keys_FileError", error.Line) : Loc.Format("Keys_FileErrorNoLine", error.Message), InfoBarSeverity.Warning, actions:
            [
                new NotificationAction(Loc.Get("Keys_OpenFile"), () => _ = OpenUriAsync(new Uri(HexEditor.App.Commands.CommandService.KeybindingsPath))),
            ]);
        }
    }

    /// <summary>外部で編集された設定ファイルが読めない (直前の値を使い続ける)。</summary>
    public void ShowSettingsEditError(Core.Settings.SettingsEditError error) =>
        ShowNotice(error.Line > 0 ? Loc.Format("Settings_ExternalErrorLine", error.Line) : Loc.Format("Settings_ExternalError", error.Detail), InfoBarSeverity.Error);

    /// <summary>設定・キー割り当てを書けなかった (UI-22 の「エラー」。値はセッション中だけ有効)。</summary>
    public void ShowSettingsSaveFailed(string reason) =>
        ShowNotice(Loc.Format("Settings_SaveFailed", reason), InfoBarSeverity.Error);

    /// <summary>通知の履歴の時刻の表示。</summary>
    public static string FormatHistoryTime(DateTime utc) => utc.ToLocalTime().ToString("T");

    /// <summary>「他に N 件」: 通知の履歴を開く (UI-36 の仕様 3)。</summary>
    private void Notifications_OverflowClicked(object? sender, EventArgs e) =>
        NotificationHistoryFlyout.ShowAt(NotificationHistoryButton);

    public MainViewModel Vm { get; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private WindowId WindowId => AppWindow.Id;

    /// <summary>コマンドラインで指定したファイルを開く (起動時、または転送された 2 つ目の起動)。</summary>
    public void OpenFromCommandLine(CommandLine commandLine, bool activate)
    {
        foreach (string file in commandLine.Files)
        {
            TryOpen(file);
        }

        // ジャンプリストの「新規作成」(--new-document。UI-35)。
        if (commandLine.NewDocument)
        {
            Vm.NewDocument();
        }

        if (commandLine.Offset is { } offset && Editor is { } editor
            && Core.Expressions.ExpressionEvaluator.TryEvaluate(offset, new Core.View.EditorExpressionContext(editor), out long target, out _))
        {
            editor.GoTo(target);
        }

        if (activate)
        {
            Activate();
        }
    }

    /// <summary>
    /// タイトル (UI-02 の仕様 3): 「● 文書名 - HexEditor」。未保存の変更があれば ●、管理者として実行中は「(管理者)」、
    /// セーフモードでは「(セーフモード)」を付ける。プレビュー版は「HexEditor Preview」。
    /// </summary>
    private void UpdateTitle()
    {
        IAppEnvironment env = Program.Environment;
        string app = env.Channel == ReleaseChannel.Preview ? "HexEditor Preview" : "HexEditor";
        string title = Vm.Selected is { } d ? $"{(d.Document.IsModified ? "● " : string.Empty)}{d.DisplayName} - {app}" : app;
        if (env.IsElevated || TestHooks.SimulatesElevation)
        {
            title += " " + Loc.Get("Title_Administrator");
        }

        if (Program.CommandLine.SafeMode)
        {
            title += " " + Loc.Get("Title_SafeMode");
        }

        Title = title;
        WindowTitle.Text = title;
    }

    private DocumentViewModel? _titleSource;

    /// <summary>選んでいる文書の変更 (編集・保存) でタイトルを更新する。</summary>
    private void WatchSelectedForTitle()
    {
        if (_titleSource is not null)
        {
            _titleSource.PropertyChanged -= TitleSource_PropertyChanged;
        }

        _titleSource = Vm.Selected;

        // 検索バーの状態はウィンドウごと。タブを切り替えたら、新しいタブを対象にする (FIND-04 の仕様 10)。
        if (FindBar.IsOpen && Editor is { } editor)
        {
            FindBar.Editor = editor;
            UpdateMatchHighlights();
        }
        if (_titleSource is not null)
        {
            _titleSource.PropertyChanged += TitleSource_PropertyChanged;
        }

        ApplyEditorSettings();
        UpdateSelectedViews();
        UpdateTitle();
        UpdateCommandStates();
        UpdateEncodingMenu();
        UpdateSyncStatus();
        QueueStatusBarLayout();
    }

    /// <summary>カーソル移動とスクロールの設定 (VIEW-26、VIEW-34) を全タブに反映する。</summary>
    public void ApplyEditorSettings()
    {
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            EditorSettings.Apply(App.Settings, doc);
        }

        ApplyViewOptions();
    }

    private void TitleSource_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateTitle();
        UpdateCommandStates();
        QueueStatusBarLayout();
    }

    // ---- ファイル ----

    private void New_Click(object sender, RoutedEventArgs e) => Vm.NewDocument();

    private void Tabs_AddTabButtonClick(TabView sender, object args) => Vm.NewDocument();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await SaveAsync(doc, saveAs: false);
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await SaveAsync(doc, saveAs: true);
        }
    }

    /// <summary>保存する。保存しなかった (キャンセル・失敗) 場合は false。</summary>
    private async Task<bool> SaveAsync(DocumentViewModel doc, bool saveAs)
    {
        // 連動ビューの保存は親のドキュメントを保存する (ENG-39 の仕様 1)。
        if (doc.LinkParent is { } linkParent)
        {
            return await SaveAsync(linkParent, saveAs);
        }

        // デコードして開いたドキュメントは元の形式で保存する (TOOL-11 の仕様 2・3)。
        if (doc.Encoded is { } encoded && doc.FilePath is { } encodedPath)
        {
            if (!saveAs)
            {
                return !doc.Document.IsModified || await SaveEncodedAsync(doc, encodedPath, encoded);
            }

            if (await SaveEncodedAsAsync(doc, encoded) is { } done)
            {
                return done;
            }

            // バイナリとして保存する: 以後はバイナリのドキュメント。
            doc.Encoded = null;
        }

        string? path = doc.FilePath;

        // 変更のない文書を同じファイルに保存しても、ファイルには触れない (ENG-20。更新日時を変えない)。
        if (!saveAs && path is not null && doc.Document.CanSave && !doc.Document.IsModified)
        {
            return true;
        }

        // 読み取り専用のドキュメントは元の場所に保存しない (ENG-14 の仕様 4、EDIT-16 の仕様 5)。「すべて保存」・閉じるときの「保存」でも
        // 「名前を付けて保存」になる (元のファイルが変わっていたため読み取り専用で復旧したドキュメントで、元のファイルを上書きしない)。
        if (saveAs || path is null || !doc.Document.CanSave || doc.Document.IsReadOnly)
        {
            // 初期フォルダは元のファイルのフォルダ、無題なら前回保存したフォルダ (ENG-21 の仕様 1)。
            string suggestedName = doc.IsUntitled ? doc.DisplayName + ".bin" : doc.DisplayName;
            if (_binarySaveAsPath is { } binaryPath)
            {
                // デコードしたドキュメントの「名前を付けて保存」でバイナリの保存先を選んだ (TOOL-11 の仕様 3)。
                path = binaryPath;
                _binarySaveAsPath = null;
            }
            else if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
            {
                path = chosen;
            }
            else
            {
                var picker = new FileSavePicker(WindowId)
                {
                    SuggestedFileName = suggestedName,
                    SettingsIdentifier = "HexEditor.SaveAs",
                };
                // 読み取り専用のボリューム (読み取り専用のメディアなど) のフォルダは初期フォルダにしない (ENG-21 の仕様 1。保存ダイアログが別のパスの入力も断るため)。
                if (doc.FilePath is { } current && Path.GetDirectoryName(current) is { } folder && IsWritableFolder(folder))
                {
                    picker.SuggestedFolder = folder;
                }
                picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [Path.GetExtension(picker.SuggestedFileName) is { Length: > 0 } ext ? ext : ".bin"]);
                path = (await picker.PickSaveFileAsync())?.Path;
            }

            if (path is null)
            {
                return false;
            }

            // 別のウィンドウで開いているファイルにも保存しない (UI-14)。
            if (Vm.Documents.Any(d => !ReferenceEquals(d.Document, doc.Document) && string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase))
                || WindowManager.FindOpenElsewhere(this, path) is not null)
            {
                ShowNotice(Loc.Get("Error_SaveOpenElsewhere"), InfoBarSeverity.Error, doc);
                return false;
            }
        }

        // 外部で変更されたファイルを上書きする・削除されたファイルを作り直すときは確かめる (ENG-19 の仕様 5・8)。
        if (string.Equals(path, doc.FilePath, StringComparison.OrdinalIgnoreCase) && !await ConfirmExternalSaveAsync(doc))
        {
            return false;
        }

        try
        {
            _saveElsewhereRequested = false;
            if (!await Vm.SaveAsync(doc, path, plan => ConfirmSavePlanAsync(plan, doc)))
            {
                // 空き容量不足のダイアログの「別の場所に保存」: 名前を付けて保存のダイアログを開く (ENG-25 の仕様 4)。
                return _saveElsewhereRequested && await SaveAsync(doc, saveAs: true);
            }

            UpdateTitle();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InsufficientSpaceException ex)
        {
            ShowNotice(Loc.Format("Error_NoSpace", ex.Drive, ex.Required.ToString("N0"), ex.Available.ToString("N0")), InfoBarSeverity.Error, doc);
        }
        catch (ShiftSaveFailedException)
        {
            // ずらしながらのその場保存の途中のエラー (ENG-24 の「エラー」)。内容はこのタブに残っているので、別の場所に保存してもらう。
            ShowNotice(Loc.Get("Shift_Failed"), InfoBarSeverity.Error, doc, actions:
            [
                new NotificationAction(Loc.Get("Menu_File_SaveAs/Text").TrimEnd('.', '…'), () => _ = SaveAsync(doc, saveAs: true)),
            ]);
        }
        catch (InPlaceSaveRolledBackException)
        {
            ShowNotice(Loc.Get("Error_SaveRolledBack"), InfoBarSeverity.Error, doc);
        }
        catch (InPlaceSavePartiallyWrittenException)
        {
            ShowNotice(Loc.Get("Error_SavePartial"), InfoBarSeverity.Error, doc);
        }
        catch (UnreadableDataException)
        {
            ShowNotice(Loc.Get("Error_Unreadable"), InfoBarSeverity.Error, doc);
        }
        catch (UnauthorizedAccessException)
        {
            ShowNotice(Loc.Get("Error_SaveDenied"), InfoBarSeverity.Error, doc);
        }
        catch (BackupFailedException ex)
        {
            // バックアップを作れないため保存を始めなかった。「バックアップなしで保存」で続けられる (ENG-26 の「エラー」)。
            // 置き場所の空き容量不足 (コピーで作る場合) は、理由を表示言語で示す。
            string reason = ex.InnerException is InsufficientSpaceException space
                ? Loc.Format("Error_NoSpace", space.Drive, space.Required.ToString("N0"), space.Available.ToString("N0")).TrimEnd('.', '。')
                : ex.Reason;
            ShowNotice(Loc.Format("Backup_Failed", reason), InfoBarSeverity.Error, doc, actions:
            [
                new NotificationAction(Loc.Get("Backup_SaveWithout"), () =>
                {
                    Vm.SkipBackupOnce = true;
                    _ = SaveAsync(doc, saveAs: false);
                }),
            ]);
        }
        catch (IOException ex)
        {
            ShowNotice(Loc.Format("Error_SaveIo", ex.Message), InfoBarSeverity.Error, doc);
        }

        return false;
    }

    // ---- 編集・移動 ----

    private EditorState? Editor => Vm.Selected?.Editor;

    private void Undo_Click(object sender, RoutedEventArgs e) => Editor?.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Editor?.Redo();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Editor?.SelectAll();

    private readonly ClipboardService _clipboard = new();

    private async void Copy_Click(object sender, RoutedEventArgs e) => await RunEditorCommandAsync(EditorCommand.Copy);

    private async void Cut_Click(object sender, RoutedEventArgs e) => await RunEditorCommandAsync(EditorCommand.Cut);

    private async void Paste_Click(object sender, RoutedEventArgs e) => await RunEditorCommandAsync(EditorCommand.Paste);

    private async void PasteOverwrite_Click(object sender, RoutedEventArgs e) => await RunEditorCommandAsync(EditorCommand.PasteOverwrite);

    private async void HexView_CommandRequested(object? sender, EditorCommand command) => await RunEditorCommandAsync(command);

    /// <summary>エディタの範囲のコマンドを実行する (EDIT-22・EDIT-23)。</summary>
    /// <summary>「貼り付けるデータのうち N バイトは末尾を越えるため貼り付けません」(ENG-07 の仕様 5)。</summary>
    private async Task<bool> ConfirmTruncateAsync(long overflow)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("PasteTruncate_Title"),
            Content = Loc.Format("PasteTruncate_Body", overflow.ToString("N0")),
            PrimaryButtonText = Loc.Get("PasteTruncate_Paste"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "PasteTruncateDialog");
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    private async Task RunEditorCommandAsync(EditorCommand command)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        switch (command)
        {
            case EditorCommand.SelectAll:
                editor.SelectAll();
                break;
            case EditorCommand.Copy:
            case EditorCommand.Cut:
                if (!editor.HasSelection)
                {
                    return;
                }

                if (command == EditorCommand.Cut && editor.ReadOnly && Vm.Selected is { } readOnlyDoc)
                {
                    // 読み取り専用では切り取らない (コピーもしない。EDIT-16 の仕様 3)。
                    ShowReadOnlyNotice(readOnlyDoc);
                    return;
                }

                if (command == EditorCommand.Cut && !editor.Document.CanResize)
                {
                    ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, Vm.Selected);
                    return;
                }

                _clipboard.CompatFormatsEnabled = App.Settings.GetBool(CompatClipboardFormats.SettingKey, true);
                if (editor.HasMultipleRanges && Vm.Selected is { } multiDoc)
                {
                    // マルチ選択・矩形: 要素を連結してコピーする (EDIT-07 の仕様 7、EDIT-17 の仕様 2)。
                    if (command == EditorCommand.Cut && editor.CheckRectangleRows() is not null)
                    {
                        ShowRectangleRowLimit(multiDoc);
                        return;
                    }

                    ClipboardPlan plan = await _clipboard.CopyRangesAsync(editor);
                    if (plan.InAppOnly)
                    {
                        ShowNotice(Loc.Get("Clipboard_RangesTooLarge"), InfoBarSeverity.Error, multiDoc);
                        return;
                    }

                    RecordClipboardHistory(multiDoc, _clipboard.LastCopiedRanges);
                    if (command == EditorCommand.Cut)
                    {
                        await DeleteSelectedRangesAsync(multiDoc, "切り取り");
                    }

                    break;
                }

                ClipboardPlan? copied = await _clipboard.CopyAsync(editor);
                if (copied is not null && Vm.Selected is { } copiedDoc)
                {
                    RecordClipboardHistory(copiedDoc, null);
                }

                if (copied?.InAppOnly == true)
                {
                    // 「選択範囲 (12.3 GB) は大きすぎるため…」と「ファイルに書き出す」(EDIT-22 の仕様 5、TOOL-16)。
                    string size = StatusFormat.ShortSize(editor.SelectionLength, System.Globalization.CultureInfo.CurrentCulture)
                        ?? editor.SelectionLength.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
                    ShowNotice(Loc.Format("Clipboard_InAppOnlySize", size), InfoBarSeverity.Informational, Vm.Selected, actions:
                    [
                        new NotificationAction(Loc.Get("Clipboard_WriteToFile"), () => _ = Commands.ExecuteAsync("file.saveSelection")),
                    ]);
                }
                else if (copied?.TextOmitted == true)
                {
                    // テキスト形式だけが上限を超えた (EDIT-22 の仕様 6)。
                    ShowNotice(Loc.Get("Clipboard_TextOmitted"), InfoBarSeverity.Informational, Vm.Selected);
                }

                if (command == EditorCommand.Cut)
                {
                    editor.DeleteSelectionForCut();
                }

                break;
            default:
                if (editor.ReadOnly && Vm.Selected is { } readOnly)
                {
                    ShowReadOnlyNotice(readOnly);
                    return;
                }

                _clipboard.PasteDetectedWithoutConfirmation = App.Settings.GetBool(PasteWithoutConfirmationKey, false);
                PasteOutcome outcome = await _clipboard.PasteAsync(editor, command == EditorCommand.PasteOverwrite, ConfirmTruncateAsync);
                if (outcome == PasteOutcome.NeedsSpecialPaste && Vm.Selected is { } special)
                {
                    // Hex 列で Hex として読めず、他の形式に当てはまる: 形式を選択して貼り付けを開く (EDIT-23 の仕様 2、EDIT-26)。
                    await PasteSpecialAsync(special, new SpecialClipboard(_clipboard.LastSpecialText, null, []));
                    break;
                }

                if (outcome == PasteOutcome.Files && Vm.Selected is { } target && _clipboard.LastFiles.Count > 0)
                {
                    // エクスプローラーでコピーしたファイル: ファイルの内容の挿入 (EDIT-23 の仕様 1 の 4、EDIT-30)。
                    await InsertFileAsync(target, _clipboard.LastFiles[0]);
                    break;
                }

                if (outcome == PasteOutcome.PastedAsText)
                {
                    // Hex として読めないテキストはテキストとして貼り、「元に戻す」を付けて知らせる (EDIT-23 の仕様 2)。
                    ShowNotice(Loc.Get("Clipboard_PastedAsText"), InfoBarSeverity.Informational, Vm.Selected,
                        undo: new NotificationAction(Loc.Get("Common_Undo"), () => editor.Undo()));
                    break;
                }

                string? key = outcome switch
                {
                    PasteOutcome.NotEncodable => "Notice_NotEncodable",
                    PasteOutcome.Truncated => "Clipboard_Truncated",
                    PasteOutcome.FixedLength => "Notice_FixedLength",
                    PasteOutcome.NotEditable => "Notice_Busy",
                    _ => null,
                };
                if (key is not null)
                {
                    // 末尾を越えて書かなかったバイト数を示す (EDIT-23 の仕様 5)。
                    ShowNotice(Loc.Format(key, _clipboard.LastTruncatedBytes.ToString("N0")), InfoBarSeverity.Error, Vm.Selected);
                }

                break;
        }
    }

    private void ToggleInsert_Click(object sender, RoutedEventArgs e)
    {
        if (Editor?.ToggleInsertMode() == EditResult.FixedLength)
        {
            ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, Vm.Selected);
        }
    }

    private void GoStart_Click(object sender, RoutedEventArgs e) => Editor?.MoveToStart();

    private void GoEnd_Click(object sender, RoutedEventArgs e) => Editor?.MoveToEnd();

    private readonly List<HexView> _views = [];

    private void HexView_Loaded(object sender, RoutedEventArgs e)
    {
        var view = (HexView)sender;
        if (!_views.Contains(view))
        {
            _views.Add(view);

            // タブを閉じた直後に開いたタブで同じ HexView が使い回されると、前の Unloaded が新しい Loaded の後に届くことがある。
            // その時点で木に戻っていれば (IsLoaded) 一覧から外さない。
            view.Unloaded += (_, _) =>
            {
                if (!view.IsLoaded)
                {
                    _views.Remove(view);
                }
            };

            // F6 / Shift+F6 で Hex ビューから他の領域へ (UI-52)。読み取れない範囲などの一時的な文は文書の通知で出す。
            view.FocusRegionRequested += (_, args) =>
            {
                MoveToRegion(args.Forward);
                args.Handled = true;
            };
            view.StatusMessageRequested += (_, args) =>
                ShowNotice(args.Message, InfoBarSeverity.Informational, view.DataContext as DocumentViewModel);
            AttachSelectionEvents(view);
            view.TextColumnEncodingRequested += HexView_TextColumnEncodingRequested;
        }

        // 画面分割 (VIEW-37)・バイトテーマ (VIEW-17)・ミニマップ (VIEW-35)。
        HookPanes(view);
        view.ByteTheme = new ByteThemeStore(App.Settings.Folder).Resolve(App.Settings.GetString(ByteThemeKey, "none"));
        AttachMinimap(view);

        // スクリーンリーダーが読む名前は文書名 (VIEW-41)。
        TrackDocumentName(view);
        ConfigureHexView(view);
        AttachZoom(view);
        UpdateMatchHighlights(view);
        AttachAnnotations(view);

        view.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 検索の一致の強調とスクロールバーの印 (FIND-04 の仕様 9、VIEW-02)。検索バーが開いている間、選択中のタブの Hex ビューに出す。
    /// </summary>
    private void UpdateMatchHighlights(HexView? only = null)
    {
        foreach (HexView view in only is null ? _views : [only])
        {
            // 検索バーが開いていれば検索バーの一致、閉じていても結果一覧が開いていれば一覧の一致 (FIND-20 の仕様 10)。
            bool active = FindBar.IsOpen && view.Editor == FindBar.Editor;
            bool results = !active && SearchResults.IsOpen && SearchResults.Shows(view.Editor);
            // 一致の強調は設定で無効にできる (FIND-04 の仕様 9)。
            view.MatchProvider = !MatchHighlightEnabled ? null : active ? FindBar.MatchesInView : results ? SearchResults.MatchesInView : StringsHighlights(view);
            view.SetSearchMarkers(active ? FindBar.MarkerOffsets() : null);
        }
    }

    /// <summary>選択中のタブの Hex ビューにフォーカスを戻す。</summary>
    private void FocusEditor() => _views.FirstOrDefault(v => v.Editor == Editor)?.Focus(FocusState.Programmatic);

    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        if (Editor is null)
        {
            return;
        }

        // 移動バーは検索バーと同じ場所に出す (VIEW-29 の仕様 1)。
        FindBar.Visibility = Visibility.Collapsed;
        GoToBar.Editor = Editor;
        GoToBar.Open();
    }

    private void Bar_Closed(object? sender, EventArgs e) => FocusEditor();

    private void Find_Click(object sender, RoutedEventArgs e) => OpenFindBar(replace: false);

    private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindAgainAsync(forward: true);

    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindAgainAsync(forward: false);

    /// <summary>F3 / Shift+F3。一度も検索していなければ検索バーを開く (FIND-09 の仕様 7)。</summary>
    private async Task FindAgainAsync(bool forward)
    {
        if (Editor is null)
        {
            return;
        }

        // 結果一覧に結果があれば、一覧の次 / 前の結果に移る (FIND-20 の仕様 11)。
        if (TryMoveInResults(forward))
        {
            return;
        }

        if (!FindBar.HasPattern)
        {
            Find_Click(this, new RoutedEventArgs());
            return;
        }

        FindBar.Editor = Editor;
        FindBar.Operations = Vm.Operations;
        await FindBar.FindAsync(forward);
    }

    private void GoBack_Click(object sender, RoutedEventArgs e) => Editor?.GoBack();

    private void GoForward_Click(object sender, RoutedEventArgs e) => Editor?.GoForward();

    private void HexView_EditRejected(object? sender, EditResult result)
    {
        if (result == EditResult.CellFormatNotEditable)
        {
            // Hex 以外のセルの表示形式では直接編集しない (VIEW-10 の仕様 7)。ステータスバーの一時的な文で知らせる。
            ShowStatusMessage(Loc.Get("HexView_Status_CellFormatReadOnly"));
            return;
        }

        DocumentViewModel? doc = Vm.Selected;
        IReadOnlyList<LongRunningOperation> busy = doc is null ? [] : Vm.Operations.ActiveFor(doc.Document);
        if (result == EditResult.NotEditable && doc is { Editor.ReadOnly: true } && busy.Count == 0)
        {
            // 読み取り専用: データを変えずに「編集を許可する」付きの InfoBar を出す (EDIT-16 の仕様 3)。
            ShowReadOnlyNotice(doc);
            return;
        }

        if (result is EditResult.TooManyRows or EditResult.TooManyCarets && doc is not null)
        {
            // 矩形の行数・カーソル数の上限 (EDIT-17 の仕様 6、EDIT-08 の仕様 3)。
            ReportEdit(doc, result);
            return;
        }

        if (result == EditResult.FixedLengthDelete && doc is not null)
        {
            // 長さを変えられないドキュメントでの削除は、代わりに 00 で塗りつぶせるようにする (EDIT-13 の仕様 5)。
            EditorState editor = doc.Editor;
            ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, doc,
                actions: [new NotificationAction(Loc.Get("Notice_FillWithZero"), () => editor.FillWithZero())]);
            return;
        }

        string message = result switch
        {
            EditResult.FixedLength or EditResult.FixedLengthDelete => Loc.Get("Notice_FixedLength"),

            // 表せない文字と文字コードを示す (EDIT-12 の仕様 4)。
            EditResult.NotEncodable when sender is HexView { LastRejectedText: { } text } && doc is not null =>
                Loc.Format("Notice_NotEncodableChar", doc.Editor.TextEncoding.FirstUnencodable(text) ?? text, EncodingDisplayName(doc.Editor.TextEncoding)),
            EditResult.NotEncodable => Loc.Get("Notice_NotEncodable"),

            // 処理中は処理名を添える (ENG-09 の仕様 7)。
            _ when busy.Count > 0 => Loc.Format("Notice_BusyWith", busy[0].Name),
            _ when doc?.Editor.ReadOnly == true => Loc.Get("Notice_ReadOnly"),
            _ => Loc.Get("Notice_Busy"),
        };
        ShowNotice(message, InfoBarSeverity.Error, doc);
    }

    /// <summary>文字コードの表示名 (ASCII、ANSI (コードページ 932) など)。</summary>
    private static string EncodingDisplayName(TextEncoding encoding) =>
        encoding.IsAscii || encoding.Id != "ansi" ? encoding.Name : Loc.Format("Menu_View_EncodingAnsi", encoding.CodePage);

    /// <summary>
    /// 通知を出す (UI-36)。<paramref name="document"/> を指定すると、その文書のタブの中に出す (文書の範囲)。
    /// </summary>
    private Notification ShowNotice(string message, InfoBarSeverity severity, DocumentViewModel? document = null,
        NotificationAction? undo = null, IReadOnlyList<NotificationAction>? actions = null)
    {
        AppLog.Info($"Notice ({severity}): {message}");
        return Vm.Notifications.Show(
            document is null ? NotificationScope.Window : NotificationScope.Document,
            (NotificationSeverity)(int)severity,
            message,
            document,
            undo,
            actions);
    }

    /// <summary>
    /// フォルダのボリュームに書き込めるか (読み取り専用のボリューム: 読み取り専用で接続した VHD、CD/DVD など)。ファイルは作らない。
    /// 調べられなければ書き込めるとみなす。
    /// </summary>
    private static bool IsWritableFolder(string folder)
    {
        const uint FileReadOnlyVolume = 0x00080000;
        string? root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        if (!root.EndsWith('\\'))
        {
            root += "\\";
        }

        return !GetVolumeInformation(root, null, 0, out _, out _, out uint flags, null, 0) || (flags & FileReadOnlyVolume) == 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true,
        EntryPoint = "GetVolumeInformationW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, System.Text.StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, System.Text.StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);
}
