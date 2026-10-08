using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Panels;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 検索・置換・すべて検索の結果一覧・検索履歴のつなぎ込み (FIND-20〜FIND-28)。結果一覧 (<see cref="SearchResultsPanel"/>) は
/// パネルの枠 (UI-05) に「searchResults」として登録する (既定は下)。結果一覧は結果を持つため、ウィンドウごとに 1 つの部品を使い、
/// 浮動パネルにはしない (要素はウィンドウをまたいで移せない)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>結果一覧のパネル ID (表示切り替えのコマンドは <c>view.panel.searchResults</c>)。</summary>
    public const string SearchResultsPanelId = "searchResults";

    /// <summary>すべて検索の結果一覧 (ウィンドウごとに 1 つ)。</summary>
    private readonly SearchResultsPanel SearchResults = new();

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterSearchResultsPanel()
    {
        if (PanelRegistry.Find(SearchResultsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(SearchResultsPanelId, "SearchResults_PanelTitle", PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window).SearchResults)
            { CanFloat = false });
        }
    }

    /// <summary>検索履歴のキー (FIND-28。設定ではなくアプリの状態 state.json に置く)。</summary>
    public const string SearchHistoryKey = "search.history";

    /// <summary>履歴の件数の上限 (0〜500、0 は保存しない。FIND-28 の仕様 2)。</summary>
    public const string SearchHistoryLimitKey = "search.history.limit";

    /// <summary>「履歴をディスクに保存しない」(FIND-28 の仕様 5)。</summary>
    public const string SearchHistoryDoNotSaveKey = "search.history.doNotSave";

    /// <summary>検索履歴はアプリ全体で 1 つ (state.json に保存し、再起動後も使う)。</summary>
    private static SearchHistory? s_searchHistory;

    /// <summary>アプリ全体の検索履歴。初回に state.json から読む。</summary>
    internal static SearchHistory SharedSearchHistory
    {
        get
        {
            if (s_searchHistory is null)
            {
                var history = new SearchHistory { Limit = App.Settings.GetInt(SearchHistoryLimitKey, SearchHistory.DefaultLimit) };
                if (!App.Settings.GetBool(SearchHistoryDoNotSaveKey, false))
                {
                    history.Load(global::HexEditor.App.Commands.CommandService.State.Get(SearchHistoryKey));
                }

                history.Changed += (_, _) => SaveSearchHistory(history);
                s_searchHistory = history;
            }

            return s_searchHistory;
        }
    }

    /// <summary>履歴を state.json に書く。上限 0 か「保存しない」なら消す。保存に失敗しても通知しない (FIND-28 の「エラー」)。</summary>
    private static void SaveSearchHistory(SearchHistory history)
    {
        try
        {
            bool save = history.Limit > 0 && !App.Settings.GetBool(SearchHistoryDoNotSaveKey, false);
            global::HexEditor.App.Commands.CommandService.State.Set(SearchHistoryKey, save && (history.Find.Count > 0 || history.Replace.Count > 0) ? history.ToJson() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"Search history not saved: {ex.Message}");
        }
    }

    /// <summary>検索バーと結果一覧をつなぐ (コンストラクターから呼ぶ)。</summary>
    private void InitializeSearch()
    {
        FindBar.ResultsPanel = SearchResults;
        FindBar.History = SharedSearchHistory;
        FindBar.Confirm = ConfirmAsync;
        FindBar.ReplaceAllCompleted += FindBar_ReplaceAllCompleted;
        FindBar.ReplaceFailed += (_, message) => ShowNotice(message, InfoBarSeverity.Error, Vm.Selected);
        AutomationProperties.SetAutomationId(SearchResults, "SearchResults");
        SearchResults.Visibility = Visibility.Collapsed;
        SearchResults.WindowId = AppWindow.Id;
        SearchResults.Operations = Vm.Operations;
        SearchResults.HighlightsChanged += (_, _) => UpdateMatchHighlights();
        SearchResults.Shown += (_, _) => ShowPanel(SearchResultsPanelId, focus: false);
        SearchResults.Closed += (_, _) =>
        {
            HidePanel(SearchResultsPanelId);
            FocusEditor();
        };
        SearchResults.BookmarksRequested += (_, e) => AddSearchResultBookmarks(e.Items, e.Group);
        SearchResultsPanel.Now = () => (TestHooks.Time ?? TimeProvider.System).GetUtcNow();
        SearchResults.NoticeRequested += (_, n) => ShowNotice(n.Message, n.Severity);
        SearchResults.ActivateRequested += (_, editor) => ActivateEditor(editor);

        // 「開いているすべてのドキュメント」(FIND-11 の仕様 1): タブの順に探し、一致のあるタブに切り替える。
        FindBar.OpenDocuments = () => [.. Vm.Documents.Select(d => (d.Editor, d.DisplayName))];
        FindBar.ActivateDocument = ActivateEditor;
    }

    /// <summary>そのビューのタブに切り替える。</summary>
    private void ActivateEditor(Core.View.EditorState editor)
    {
        if (Vm.Documents.FirstOrDefault(d => d.Editor == editor) is { } doc && Vm.Selected != doc)
        {
            Vm.Selected = doc;
        }
    }

    /// <summary>検索バーを開く (検索 > 検索、検索 > 置換)。</summary>
    private void OpenFindBar(bool replace)
    {
        if (Editor is null)
        {
            return;
        }

        GoToBar.Visibility = Visibility.Collapsed;
        FindBar.Editor = Editor;
        FindBar.Operations = Vm.Operations;
        FindBar.Open(replace);
    }

    /// <summary>検索のコマンド: 置換 (Ctrl+H。FIND-22 の仕様 1)、すべて検索 (FIND-20)、検索履歴を消去 (FIND-28)。</summary>
    private void RegisterSearchCommands()
    {
        Commands.Register("search.replace", () => OpenFindBar(replace: true), NeedsDocument);
        Commands.Register("search.findAll", async () =>
        {
            if (!FindBar.IsOpen)
            {
                OpenFindBar(replace: false);
            }

            if (FindBar.HasPattern)
            {
                await FindBar.FindAllAsync();
            }
        }, NeedsDocument);
        Commands.Register("search.clearHistory", () => FindBar.ClearHistory());
    }

    /// <summary>
    /// パネルの表示を結果一覧に反映する (配置を反映するたびに呼ぶ): パネルの枠から閉じたら一覧も閉じる (実行中のすべて検索も止める)。
    /// 表示切り替えのコマンドで出したときは、結果がなくても一覧を見せる。
    /// </summary>
    private void SyncSearchResultsPanel()
    {
        bool shown = IsPanelShown(SearchResultsPanelId);
        if (shown && !SearchResults.IsOpen)
        {
            SearchResults.Visibility = Visibility.Visible;
        }
        else if (!shown && SearchResults.IsOpen)
        {
            SearchResults.Close();
        }
    }

    /// <summary>結果一覧の「変換 &gt; ブックマークに」(FIND-21 の仕様 3): 結果のドキュメントのブックマークに、同じグループで加える。</summary>
    private void AddSearchResultBookmarks(IReadOnlyList<(long Offset, long Length, string Name)> items, string group)
    {
        if (Vm.Documents.FirstOrDefault(d => d.Editor == SearchResults.Editor) is not { } doc)
        {
            return;
        }

        Core.Bookmarks.BookmarkCollection bookmarks = AnnotationsFor(doc).Bookmarks;
        try
        {
            foreach ((long offset, long length, string name) in items)
            {
                bookmarks.SetGroup(bookmarks.Add(offset, length, name), group);
            }
        }
        catch (Core.Bookmarks.BookmarkLimitException)
        {
            ShowNotice(Loc.Format("Bookmarks_Limit", Core.Bookmarks.BookmarkCollection.MaxCount.ToString("N0", CultureInfo.CurrentCulture)),
                InfoBarSeverity.Error, doc);
        }

        ShowNotice(Loc.Format("SearchResults_BookmarksAdded", items.Count.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Success, doc);
    }

    /// <summary>
    /// F3 / Shift+F3 で結果一覧の次 / 前の結果に移るか (一覧に今のタブの結果があるとき。FIND-20 の仕様 11)。移ったら true。
    /// 設定「F3 は検索をやり直す」(<c>search.results.f3Repeats</c>) がオンなら一覧を使わない。
    /// </summary>
    private bool TryMoveInResults(bool forward) =>
        SearchResults.HasResults && SearchResults.Shows(Editor) && !App.Settings.GetBool("search.results.f3Repeats", false)
        && SearchResults.MoveNext(forward, Editor);

    /// <summary>すべて置換の完了: 「1,234 件置換しました」と「元に戻す」(FIND-23 の仕様 4)。</summary>
    private void FindBar_ReplaceAllCompleted(object? sender, ReplaceAllCompletedEventArgs e)
    {
        IReadOnlyList<Core.View.EditorState> editors = e.Editors;
        DocumentViewModel? doc = editors.Count == 1 ? Vm.Documents.FirstOrDefault(d => d.Editor == editors[0]) : null;
        ShowNotice(Loc.Format("Find_ReplacedCount", e.Count.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Success, doc,
            undo: new NotificationAction(Loc.Get("Common_Undo"), () =>
            {
                foreach (Core.View.EditorState editor in editors.Where(x => !x.Document.IsEditLocked && !x.Document.IsDisposed))
                {
                    editor.Undo();
                }
            }));
    }

    /// <summary>確認ダイアログ (すべて置換の件数の確認、末尾を超える上書きの確認)。</summary>
    private async Task<ConfirmChoice> ConfirmAsync(ConfirmRequest request)
    {
        ContentDialog dialog = NewDialog(request.Title, new TextBlock { Text = request.Body, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetAutomationId(dialog, request.AutomationId);
        dialog.PrimaryButtonText = request.Primary;
        if (request.Secondary is { } secondary)
        {
            dialog.SecondaryButtonText = secondary;
        }

        dialog.CloseButtonText = request.Close;
        dialog.DefaultButton = ContentDialogButton.Close;
        try
        {
            return await dialog.ShowAsync() switch
            {
                ContentDialogResult.Primary => ConfirmChoice.Primary,
                ContentDialogResult.Secondary => ConfirmChoice.Secondary,
                _ => ConfirmChoice.Cancel,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // ほかのダイアログが開いている。
            return ConfirmChoice.Cancel;
        }
    }
}
