using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 検索・置換・すべて検索の結果一覧・検索履歴のつなぎ込み (FIND-20〜FIND-28)。結果一覧 (<see cref="SearchResultsPanel"/>) は
/// UI-05 のパネルの仕組みができるまで、検索バーの下に置く。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>検索履歴の設定のキー (FIND-28)。</summary>
    public const string SearchHistoryKey = "search.history";

    /// <summary>履歴の件数の上限 (0〜500、0 は保存しない。FIND-28 の仕様 2)。</summary>
    public const string SearchHistoryLimitKey = "search.history.limit";

    /// <summary>「履歴をディスクに保存しない」(FIND-28 の仕様 5)。</summary>
    public const string SearchHistoryDoNotSaveKey = "search.history.doNotSave";

    /// <summary>検索履歴はアプリ全体で 1 つ (設定として保存し、再起動後も使う)。</summary>
    private static SearchHistory? s_searchHistory;

    /// <summary>アプリ全体の検索履歴。初回に設定から読む。</summary>
    internal static SearchHistory SharedSearchHistory
    {
        get
        {
            if (s_searchHistory is null)
            {
                var history = new SearchHistory { Limit = App.Settings.GetInt(SearchHistoryLimitKey, SearchHistory.DefaultLimit) };
                if (!App.Settings.GetBool(SearchHistoryDoNotSaveKey, false))
                {
                    history.Load(App.Settings.GetJson(SearchHistoryKey));
                }

                history.Changed += (_, _) => SaveSearchHistory(history);
                s_searchHistory = history;
            }

            return s_searchHistory;
        }
    }

    /// <summary>履歴を設定に書く。上限 0 か「保存しない」なら設定から消す。保存に失敗しても通知しない (FIND-28 の「エラー」)。</summary>
    private static void SaveSearchHistory(SearchHistory history)
    {
        try
        {
            bool save = history.Limit > 0 && !App.Settings.GetBool(SearchHistoryDoNotSaveKey, false);
            App.Settings.SetJson(SearchHistoryKey, save && (history.Find.Count > 0 || history.Replace.Count > 0) ? history.ToJson() : null);
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
        SearchResults.WindowId = AppWindow.Id;
        SearchResults.Operations = Vm.Operations;
        SearchResults.HighlightsChanged += (_, _) => UpdateMatchHighlights();
        SearchResults.Closed += (_, _) => FocusEditor();
        SearchResults.NoticeRequested += (_, n) => ShowNotice(n.Message, n.Severity);
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

    /// <summary>検索 > 置換 (Ctrl+H。FIND-22 の仕様 1)。</summary>
    private void Replace_Click(object sender, RoutedEventArgs e) => OpenFindBar(replace: true);

    /// <summary>検索 > 検索履歴を消去 (FIND-28)。</summary>
    private void ClearSearchHistory_Click(object sender, RoutedEventArgs e) => FindBar.ClearHistory();

    /// <summary>
    /// F3 / Shift+F3 で結果一覧の次 / 前の結果に移るか (一覧に今のタブの結果があるとき。FIND-20 の仕様 11)。移ったら true。
    /// 設定「F3 は検索をやり直す」(<c>search.results.f3Repeats</c>) がオンなら一覧を使わない。
    /// </summary>
    private bool TryMoveInResults(bool forward) =>
        SearchResults.HasResults && SearchResults.Editor == Editor && !App.Settings.GetBool("search.results.f3Repeats", false)
        && SearchResults.MoveNext(forward);

    /// <summary>すべて置換の完了: 「1,234 件置換しました」と「元に戻す」(FIND-23 の仕様 4)。</summary>
    private void FindBar_ReplaceAllCompleted(object? sender, long count)
    {
        DocumentViewModel? doc = Vm.Selected;
        Core.View.EditorState? editor = FindBar.Editor;
        ShowNotice(Loc.Format("Find_ReplacedCount", count.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Success, doc,
            undo: editor is null ? null : new NotificationAction(Loc.Get("Common_Undo"), () =>
            {
                if (!editor.Document.IsEditLocked)
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
