using System.Runtime.InteropServices;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
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
        InitializeRegions();

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
        string app = env.Channel == "Preview" ? "HexEditor Preview" : "HexEditor";
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
        if (_titleSource is not null)
        {
            _titleSource.PropertyChanged += TitleSource_PropertyChanged;
        }

        UpdateTitle();
        UpdateCommandStates();
        QueueStatusBarLayout();
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
            var picker = new FileSavePicker(WindowId)
            {
                SuggestedFileName = doc.IsUntitled ? doc.DisplayName + ".bin" : doc.DisplayName,
                SettingsIdentifier = "HexEditor.SaveAs",
            };
            if (doc.FilePath is { } current && Path.GetDirectoryName(current) is { } folder)
            {
                picker.SuggestedFolder = folder;
            }
            picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [Path.GetExtension(picker.SuggestedFileName) is { Length: > 0 } ext ? ext : ".bin"]);
            PickFileResult? result = await picker.PickSaveFileAsync();
            if (result is null)
            {
                return false;
            }

            path = result.Path;
            if (Vm.Documents.Any(d => d != doc && string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                ShowNotice(Loc.Get("Error_SaveOpenElsewhere"), InfoBarSeverity.Error, doc);
                return false;
            }
        }

        try
        {
            await Vm.SaveAsync(doc, path);
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
        catch (UnreadableDataException)
        {
            ShowNotice(Loc.Get("Error_Unreadable"), InfoBarSeverity.Error, doc);
        }
        catch (UnauthorizedAccessException)
        {
            ShowNotice(Loc.Get("Error_SaveDenied"), InfoBarSeverity.Error, doc);
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
    private async Task RunEditorCommandAsync(EditorCommand command)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        var encoding = System.Text.Encoding.Latin1;
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

                if (!await _clipboard.CopyAsync(editor, encoding))
                {
                    ShowNotice(Loc.Get("Clipboard_InAppOnly"), InfoBarSeverity.Informational, Vm.Selected);
                }

                if (command == EditorCommand.Cut)
                {
                    editor.DeleteSelectionForCut();
                }

                break;
            default:
                PasteOutcome outcome = await _clipboard.PasteAsync(editor, encoding, command == EditorCommand.PasteOverwrite);
                string? key = outcome switch
                {
                    PasteOutcome.NotHex => "Clipboard_NotHex",
                    PasteOutcome.NotEncodable => "Notice_NotEncodable",
                    PasteOutcome.Truncated => "Clipboard_Truncated",
                    PasteOutcome.FixedLength => "Notice_FixedLength",
                    PasteOutcome.NotEditable => "Notice_Busy",
                    _ => null,
                };
                if (key is not null)
                {
                    ShowNotice(Loc.Get(key), InfoBarSeverity.Error, Vm.Selected);
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
            view.Unloaded += (_, _) => _views.Remove(view);
        }

        view.Focus(FocusState.Programmatic);
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
        string message = result switch
        {
            EditResult.FixedLength => Loc.Get("Notice_FixedLength"),
            EditResult.NotEncodable => Loc.Get("Notice_NotEncodable"),

            // 処理中は処理名を添える (ENG-09 の仕様 7)。
            _ when busy.Count > 0 => Loc.Format("Notice_BusyWith", busy[0].Name),
            _ when doc?.Editor.ReadOnly == true => Loc.Get("Notice_ReadOnly"),
            _ => Loc.Get("Notice_Busy"),
        };
        ShowNotice(message, InfoBarSeverity.Error, doc);
    }

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
