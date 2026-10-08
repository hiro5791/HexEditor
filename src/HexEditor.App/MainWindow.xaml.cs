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
        FindBar.MatchesChanged += (_, _) => UpdateMatchHighlights();
        Vm.MaterializeFailed += (_, ex) => DispatcherQueue.TryEnqueue(() => OnMaterializeFailed(this, ex));
        InitializeRegions();
        InitializeExternalChanges();

        // コマンド・ツールバー・パネル・設定画面・コマンドパレット (UI-04、UI-05、UI-16〜UI-22)。
        InitializeCommands();
        InitializePanels();
        InitializeToolPages();
        InitializePalette();
        RefreshToolbar();

        // 自動で閉じる通知の時間を数える (UI-36 の仕様 4)。
        var noticeTimer = DispatcherQueue.CreateTimer();
        noticeTimer.Interval = TimeSpan.FromSeconds(1);
        noticeTimer.Tick += (_, _) => Vm.Notifications.Tick();
        noticeTimer.Start();
    }

    /// <summary>テーマ・背景素材を反映する (UI-26、UI-27)。</summary>
    public void ApplyAppearance()
    {
        Appearance.Apply(this, Root, App.Settings);
        UpdateThemeMenu();
    }

    /// <summary>設定ファイルを読んだ結果を知らせる (UI-23 の「エラー」と仕様 6)。</summary>
    public void ShowSettingsStatus(Core.Settings.SettingsLoadStatus status)
    {
        if (status == Core.Settings.SettingsLoadStatus.Broken)
        {
            ShowNotice(Loc.Get("Settings_Broken"), InfoBarSeverity.Warning, actions:
            [
                new NotificationAction(Loc.Get("Settings_OpenFolder"), () => _ = Windows.System.Launcher.LaunchFolderPathAsync(App.Settings.Folder)),
            ]);
        }
        else if (status == Core.Settings.SettingsLoadStatus.TooNew)
        {
            ShowNotice(Loc.Get("Settings_TooNew"), InfoBarSeverity.Warning);
        }
    }

    /// <summary>keybindings.json の誤り (読める部分だけを使う。UI-18 の「エラー」)。</summary>
    public void ShowKeybindingsStatus()
    {
        if (HexEditor.App.Commands.CommandService.KeybindingsError is { } error)
        {
            ShowNotice(error.Line > 0 ? Loc.Format("Keys_FileError", error.Line) : Loc.Format("Keys_FileErrorNoLine", error.Message), InfoBarSeverity.Warning, actions:
            [
                new NotificationAction(Loc.Get("Keys_OpenFile"), () => _ = OpenUriAsync(new Uri(HexEditor.App.Commands.CommandService.KeybindingsPath))),
            ]);
        }
    }

    /// <summary>外部で編集された設定ファイルが読めない (直前の値を使い続ける)。</summary>
    public void ShowSettingsEditError(string reason) =>
        ShowNotice(Loc.Format("Settings_ExternalError", reason), InfoBarSeverity.Error);

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
        if (env.IsElevated)
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
        UpdateTitle();
        UpdateCommandStates();
        UpdateEncodingMenu();
        QueueStatusBarLayout();
    }

    /// <summary>カーソル移動とスクロールの設定 (VIEW-26、VIEW-34) を全タブに反映する。</summary>
    public void ApplyEditorSettings()
    {
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            EditorSettings.Apply(App.Settings, doc);
        }
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
        string? path = doc.FilePath;

        // 変更のない文書を同じファイルに保存しても、ファイルには触れない (ENG-20。更新日時を変えない)。
        if (!saveAs && path is not null && doc.Document.CanSave && !doc.Document.IsModified)
        {
            return true;
        }

        if (saveAs || path is null || !doc.Document.CanSave)
        {
            // 初期フォルダは元のファイルのフォルダ、無題なら前回保存したフォルダ (ENG-21 の仕様 1)。
            string suggestedName = doc.IsUntitled ? doc.DisplayName + ".bin" : doc.DisplayName;
            if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
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
                if (doc.FilePath is { } current && Path.GetDirectoryName(current) is { } folder)
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

            if (Vm.Documents.Any(d => d != doc && string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)))
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
            ShowNotice(Loc.Format("Backup_Failed", ex.Reason), InfoBarSeverity.Error, doc, actions:
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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

                if (command == EditorCommand.Cut && (!editor.Document.CanResize || editor.ReadOnly))
                {
                    ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, Vm.Selected);
                    return;
                }

                ClipboardPlan? copied = await _clipboard.CopyAsync(editor);
                if (copied?.InAppOnly == true)
                {
                    ShowNotice(Loc.Get("Clipboard_InAppOnly"), InfoBarSeverity.Informational, Vm.Selected);
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
                PasteOutcome outcome = await _clipboard.PasteAsync(editor, command == EditorCommand.PasteOverwrite, ConfirmTruncateAsync);
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
        }

        // スクリーンリーダーが読む名前は文書名 (VIEW-41)。
        view.DocumentName = (view.DataContext as DocumentViewModel)?.DisplayName;
        UpdateMatchHighlights(view);

        view.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// 検索の一致の強調とスクロールバーの印 (FIND-04 の仕様 9、VIEW-02)。検索バーが開いている間、選択中のタブの Hex ビューに出す。
    /// </summary>
    private void UpdateMatchHighlights(HexView? only = null)
    {
        foreach (HexView view in only is null ? _views : [only])
        {
            bool active = FindBar.IsOpen && view.Editor == FindBar.Editor;
            view.MatchProvider = active ? FindBar.MatchesInView : null;
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

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (Editor is null)
        {
            return;
        }

        GoToBar.Visibility = Visibility.Collapsed;
        FindBar.Editor = Editor;
        FindBar.Operations = Vm.Operations;
        FindBar.Open();
    }

    private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindAgainAsync(forward: true);

    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindAgainAsync(forward: false);

    /// <summary>F3 / Shift+F3。一度も検索していなければ検索バーを開く (FIND-09 の仕様 7)。</summary>
    private async Task FindAgainAsync(bool forward)
    {
        if (Editor is null)
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
        DocumentViewModel? doc = Vm.Selected;
        IReadOnlyList<LongRunningOperation> busy = doc is null ? [] : Vm.Operations.ActiveFor(doc.Document);
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
        encoding.IsAscii ? encoding.Name : Loc.Format("Menu_View_EncodingAnsi", encoding.CodePage);

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
}
