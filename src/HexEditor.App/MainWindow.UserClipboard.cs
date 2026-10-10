using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Panels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ユーザークリップボードとクリップボード履歴 (EDIT-28)、履歴パネル (EDIT-20) とクリップボードパネルの登録。
/// </summary>
public sealed partial class MainWindow
{
    public const string HistoryPanelId = "history";

    public const string ClipboardPanelId = "clipboard";

    private static UserClipboards? s_userClipboards;

    private HistoryPanelViewModel? _historyVm;
    private ClipboardPanelViewModel? _clipboardVm;

    /// <summary>
    /// アプリ全体で 1 つのユーザークリップボード。初めて使うときに、設定「終了後も残す」がオンなら設定フォルダから読む (仕様 6)。
    /// </summary>
    internal static UserClipboards UserClipboards
    {
        get
        {
            if (s_userClipboards is null)
            {
                s_userClipboards = new UserClipboards
                {
                    HistoryLimit = App.Settings.GetInt(EditingSettings.ClipboardHistoryCountKey, 20),
                    Persist = App.Settings.GetBool(EditingSettings.UserClipboardPersistKey, false),
                };
                if (s_userClipboards.Persist)
                {
                    s_userClipboards.Load(Hosting.Program.Environment.Locations.Settings);
                }

                App.Settings.Changed += keys =>
                {
                    if (keys.Contains(EditingSettings.ClipboardHistoryCountKey) || keys.Contains(EditingSettings.UserClipboardPersistKey))
                    {
                        s_userClipboards.HistoryLimit = App.Settings.GetInt(EditingSettings.ClipboardHistoryCountKey, 20);
                        s_userClipboards.Persist = App.Settings.GetBool(EditingSettings.UserClipboardPersistKey, false);
                        SaveUserClipboards();
                    }
                };
            }

            return s_userClipboards;
        }
    }

    /// <summary>終了後も残す内容を書く (番号・名前を変えたとき。設定がオフなら保存を消す)。</summary>
    internal static void SaveUserClipboards()
    {
        if (s_userClipboards is not { } clipboards)
        {
            return;
        }

        try
        {
            clipboards.Save(Hosting.Program.Environment.Locations.Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"ユーザークリップボードを保存できません: {ex.Message}");
        }
    }

    /// <summary>履歴パネルとクリップボードパネルを登録する (ウィンドウを作る前に 1 回)。</summary>
    public static void RegisterSelectionPanels()
    {
        if (PanelRegistry.Find(HistoryPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(HistoryPanelId, "Panel_History_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateHistoryPanel()));
        }

        if (PanelRegistry.Find(ClipboardPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(ClipboardPanelId, "Panel_Clipboard_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateClipboardPanel()));
        }
    }

    private HistoryPanelViewModel HistoryVm
    {
        get
        {
            if (_historyVm is null)
            {
                _historyVm = new HistoryPanelViewModel(Vm);
                _historyVm.Attach(Vm.Selected);
                _panelContext.ActiveDocumentChanged += (_, _) => _historyVm.Attach(Vm.Selected);
                Closed += (_, _) =>
                {
                    if (_closingConfirmed)
                    {
                        _historyVm.Detach();
                    }
                };
            }

            return _historyVm;
        }
    }

    private ClipboardPanelViewModel ClipboardVm
    {
        get
        {
            if (_clipboardVm is null)
            {
                _clipboardVm = new ClipboardPanelViewModel(UserClipboards);
                Closed += (_, _) =>
                {
                    // アプリ全体の項目の変更の通知から外す (ウィンドウを本当に閉じたとき)。
                    if (_closingConfirmed)
                    {
                        _clipboardVm.Detach();
                    }
                };
            }

            return _clipboardVm;
        }
    }

    private HistoryPanel CreateHistoryPanel()
    {
        var panel = new HistoryPanel(HistoryVm);
        AutomationProperties.SetName(panel, Loc.Get("Panel_History_Title"));
        return panel;
    }

    private ClipboardPanel CreateClipboardPanel()
    {
        var panel = new ClipboardPanel(ClipboardVm);
        AutomationProperties.SetName(panel, Loc.Get("Panel_Clipboard_Title"));
        panel.PasteRequested += (_, e) => PasteUserEntry(e.Entry, e.Overwrite);
        return panel;
    }

    // ---- コマンド ----

    private void RegisterUserClipboardCommands()
    {
        string noSelection = Loc.Get("Command_NoSelection");
        for (int n = 1; n <= UserClipboards.SlotCount; n++)
        {
            int number = n;
            Commands.Register($"edit.userClipboard.copy{n}", () => CopyToUserClipboard(number),
                () => NeedsDocument(d => d.Editor.HasSelection ? null : noSelection));

            // 空の番号からの貼り付けは無効にする (EDIT-28 の「エラー」)。
            Commands.Register($"edit.userClipboard.paste{n}", () => PasteFromUserClipboard(number),
                () => NeedsEditable(_ => UserClipboards.Get(number) is null ? Loc.Get("Command_UserClipboardEmpty") : null));
        }

        // 番号ごとのメニュー項目 (アクセスキーは番号)。
        for (int n = 1; n <= UserClipboards.SlotCount; n++)
        {
            string digit = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var copy = new MenuFlyoutItem { Text = Loc.Format("Menu_UserClipboard_CopyN", n), AccessKey = digit };
            AutomationProperties.SetAutomationId(copy, $"Command_UserClipboardCopy{n}");
            HexEditor.App.Commands.CommandUi.SetId(copy, $"edit.userClipboard.copy{n}");
            var paste = new MenuFlyoutItem { Text = Loc.Format("Menu_UserClipboard_PasteN", n), AccessKey = digit };
            AutomationProperties.SetAutomationId(paste, $"Command_UserClipboardPaste{n}");
            HexEditor.App.Commands.CommandUi.SetId(paste, $"edit.userClipboard.paste{n}");
            UserCopyMenu.Items.Add(copy);
            UserPasteMenu.Items.Add(paste);
        }

        // 「クリップボード履歴から貼り付け」: クリップボードパネルを開き、履歴から選ぶ。
        Commands.Register("edit.clipboardHistory.paste", () => ShowPanel(ClipboardPanelId),
            () => NeedsEditable(_ => UserClipboards.History.Count > 0 ? null : Loc.Get("Command_ClipboardHistoryEmpty")));
    }

    /// <summary>「N にコピー」(仕様 2): 選択範囲を N 番に入れる。システムのクリップボードは変えない。</summary>
    private void CopyToUserClipboard(int number)
    {
        if (Vm.Selected is not { } doc || !doc.Editor.HasSelection)
        {
            return;
        }

        if (EntryForSelection(doc) is { } entry)
        {
            UserClipboards.Set(number, entry);
            SaveUserClipboards();
        }
    }

    /// <summary>選択範囲の項目 (単一の選択は範囲の参照、マルチ選択・矩形は連結したバイト列)。大きすぎるマルチ選択は null。</summary>
    private ClipboardEntry? EntryForSelection(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        if (!editor.HasMultipleRanges)
        {
            return ClipboardEntry.Capture(doc.Document, editor.SelectionStart, editor.SelectionLength);
        }

        SelectionSnapshot selection = editor.CaptureSelection();
        if (selection.TotalLength > ClipboardService.SystemLimit)
        {
            ShowNotice(Loc.Get("Clipboard_RangesTooLarge"), InfoBarSeverity.Error, doc);
            return null;
        }

        byte[] data = new byte[selection.TotalLength];
        long at = 0;
        foreach (Core.Selection.ByteRange r in selection.Ranges)
        {
            doc.Document.Current.Read(r.Start, data.AsSpan((int)at, (int)r.Length));
            at += r.Length;
        }

        return ClipboardEntry.FromBytes(data, doc.Document.Source.DisplayName, selection.Bounds.Start, DateTimeOffset.Now);
    }

    /// <summary>アプリ内のコピー・切り取りをクリップボード履歴に加える (仕様 4)。<paramref name="ranges"/> はマルチ選択を連結したデータ。</summary>
    private void RecordClipboardHistory(DocumentViewModel doc, byte[]? ranges)
    {
        if (UserClipboards.HistoryLimit == 0)
        {
            return;
        }

        EditorState editor = doc.Editor;
        ClipboardEntry entry = ranges is not null
            ? ClipboardEntry.FromBytes(ranges, doc.Document.Source.DisplayName, editor.CaptureSelection().Bounds.Start, DateTimeOffset.Now)
            : ClipboardEntry.Capture(doc.Document, editor.SelectionStart, editor.SelectionLength);
        UserClipboards.AddHistory(entry);
    }

    /// <summary>「N から貼り付け」(仕様 2): 現在の入力モードで貼り付ける。</summary>
    private void PasteFromUserClipboard(int number)
    {
        if (UserClipboards.Get(number) is { } entry)
        {
            PasteUserEntry(entry, overwrite: false);
        }
    }

    private void PasteUserEntry(ClipboardEntry entry, bool overwrite)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        if (doc.Editor.ReadOnly)
        {
            ShowReadOnlyNotice(doc);
            return;
        }

        EditResult result = entry.Range is { } range
            ? doc.Editor.Paste(range, overwrite, allowTruncate: true)
            : doc.Editor.Paste(entry.Data!, overwrite, allowTruncate: true);
        ReportEdit(doc, result);
        FocusEditor();
    }
}
