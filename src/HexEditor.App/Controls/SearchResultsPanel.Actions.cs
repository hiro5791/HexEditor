using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App.Controls;

/// <summary>
/// 結果一覧の操作: 変換・エクスポートの対象の選択 (FIND-21 の仕様 1)、ブックマークへの変換と件数の確認 (仕様 3)、右クリックメニュー、
/// 古い結果の再検索 (FIND-03 の「エラー」)、読み込めなかった範囲の一覧 (FIND-01 の「エラー」)。
/// </summary>
public sealed partial class SearchResultsPanel
{
    /// <summary>読み込めなかった範囲のメニューに出す項目の上限。</summary>
    private const int SkippedMenuLimit = 100;

    /// <summary>利用者が選んだ変換・エクスポートの対象 (null なら既定: 選んだ行が 2 行以上ならその行、そうでなければすべての行)。</summary>
    private bool? _targetSelected;

    private MenuFlyout? _listMenu;

    /// <summary>確認ダイアログ (ブックマークへの変換が 10,000 件を超える場合)。null なら確認せずに変換する。</summary>
    public Func<ConfirmRequest, Task<ConfirmChoice>>? Confirm { get; set; }

    /// <summary>対象が選んだ行か (既定の規則: 選んだ行が 2 行以上なら選んだ行。FIND-21 の仕様 1)。</summary>
    internal bool TargetsSelectedRows => SelectionCount > 0 && (_targetSelected ?? SelectionCount > 1);

    /// <summary>
    /// 変換・エクスポートの対象の行 (結果の番号の一覧)。null は「一覧のすべての行を見つかった順に」(並べ替え・絞り込みをしていなければ)。
    /// </summary>
    private IReadOnlyList<long>? TargetIndices() => TargetsSelectedRows ? [.. SelectedIndices().Select(Map)] : _view;

    /// <summary>対象を選ぶ (テスト用の命令の通り道からも呼ぶ。null で既定に戻す)。</summary>
    internal void SetTarget(bool? selectedRows) => _targetSelected = selectedRows;

    private void ConvertMenu_Opening(object? sender, object e)
    {
        // 開くたびに既定の対象に戻す (選んだ行が 2 行以上なら選んだ行)。
        _targetSelected = null;
        TargetSelectedItem.IsEnabled = SelectionCount > 0;
        TargetSelectedItem.IsChecked = TargetsSelectedRows;
        TargetAllItem.IsChecked = !TargetsSelectedRows;
        string count = SelectionCount.ToString("N0", CultureInfo.CurrentCulture);
        TargetSelectedItem.Text = Loc.Format("SearchResults_TargetSelectedCount", count);
        TargetAllItem.Text = Loc.Format("SearchResults_TargetAllCount", RowCount.ToString("N0", CultureInfo.CurrentCulture));
    }

    private void Target_Click(object sender, RoutedEventArgs e) => _targetSelected = ReferenceEquals(sender, TargetSelectedItem);

    // ---- ブックマークへの変換 (FIND-21 の仕様 3) ----

    /// <summary>
    /// 対象の一致をブックマークにする。名前は「検索: 検索語 #番号」、グループは「検索結果 日時」。「開いているすべてのドキュメント」の結果は、
    /// それぞれのドキュメントのブックマークにする。10,000 件を超える場合は確認する。
    /// </summary>
    internal async Task ToBookmarksAsync()
    {
        if (_groups.Count == 0 || BookmarksRequested is null)
        {
            return;
        }

        IReadOnlyList<long> indices = TargetIndices() ?? LongRange(TotalCount);
        if (indices.Count > SearchResultsConversion.BookmarkConfirmThreshold && Confirm is not null)
        {
            ConfirmChoice choice = await Confirm(new ConfirmRequest(
                "BookmarksConfirm",
                Loc.Get("SearchResults_BookmarksConfirm_Title"),
                Loc.Format("SearchResults_BookmarksConfirm_Body", indices.Count.ToString("N0", CultureInfo.CurrentCulture)),
                Loc.Get("SearchResults_BookmarksConfirm_Convert"),
                null,
                Loc.Get("SearchResults_BookmarksConfirm_Cancel")));
            if (choice != ConfirmChoice.Primary)
            {
                return;
            }
        }

        string prefix = Loc.Get("SearchResults_BookmarkPrefix");
        string group = SearchResultsConversion.BookmarkGroup(Loc.Get("SearchResults_BookmarkGroup"), Now());
        var byEditor = new Dictionary<EditorState, List<(long Offset, long Length, string Name)>>();
        foreach (long i in indices)
        {
            if (LocateResult(i) is not (Group g, long local))
            {
                continue;
            }

            TrackedMatch t = Factory(g).Track(g.Results[local]);
            if (!byEditor.TryGetValue(g.Editor, out List<(long, long, string)>? items))
            {
                byEditor[g.Editor] = items = [];
            }

            items.Add((t.Offset, t.Length, SearchResultsConversion.BookmarkName(prefix, _query, i + 1)));
        }

        foreach ((EditorState editor, List<(long, long, string)> items) in byEditor)
        {
            BookmarksRequested.Invoke(this, (editor, items, group));
        }
    }

    private static List<long> LongRange(long count)
    {
        var list = new List<long>((int)Math.Min(count, int.MaxValue));
        for (long i = 0; i < count && list.Count < int.MaxValue; i++)
        {
            list.Add(i);
        }

        return list;
    }

    // ---- エクスポート (形式を保存のダイアログで選ぶ。コマンドパレット「検索結果をエクスポート」) ----

    /// <summary>保存のダイアログで CSV か JSON を選んで書き出す (拡張子で形式を決める)。</summary>
    public async Task ExportChoosingFormatAsync()
    {
        if (_groups.Count == 0)
        {
            return;
        }

        if (!TestHooks.TrySavePicker("results.csv", out string? path))
        {
            var picker = new FileSavePicker(WindowId)
            {
                SuggestedFileName = "results",
                SettingsIdentifier = "HexEditor.ExportSearchResults",
            };
            picker.FileTypeChoices.Add(Loc.Get("SearchResults_FileType_Csv"), [".csv"]);
            picker.FileTypeChoices.Add(Loc.Get("SearchResults_FileType_Json"), [".json"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is not null)
        {
            ExportFormat format = string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Json : ExportFormat.Csv;
            await ExportToAsync(format, path);
        }
    }

    // ---- 右クリックメニュー (FIND-21 の「呼び出し」) ----

    private void InitializeListMenu()
    {
        _listMenu = new MenuFlyout();
        AutomationProperties.SetAutomationId(_listMenu, "SearchResults_ListMenu");
        _listMenu.Items.Add(MenuItem("SearchResults_Menu_GoTo", () =>
        {
            if (_selected >= 0)
            {
                Jump(_selected);
            }
        }));
        _listMenu.Items.Add(new MenuFlyoutSeparator());
        _listMenu.Items.Add(MenuItem("SearchResults_CopyHex/Text", () => CopyRows(hex: true)));
        _listMenu.Items.Add(MenuItem("SearchResults_CopyText/Text", () => CopyRows(hex: false)));
        _listMenu.Items.Add(new MenuFlyoutSeparator());
        _listMenu.Items.Add(MenuItem("SearchResults_ToSelection/Text", ToSelection));
        _listMenu.Items.Add(MenuItem("SearchResults_ToBookmarks/Text", () => _ = ToBookmarksAsync()));
        _listMenu.Items.Add(MenuItem("SearchResults_ExportCsv/Text", () => _ = ExportAsync(ExportFormat.Csv)));
        _listMenu.Items.Add(MenuItem("SearchResults_ExportJson/Text", () => _ = ExportAsync(ExportFormat.Json)));
        _listMenu.Opening += (_, _) =>
        {
            // 右クリックメニューの対象は、選んだ行があればその行 (なければすべての行)。
            _targetSelected = SelectionCount > 0;
            foreach (MenuFlyoutItem item in _listMenu.Items.OfType<MenuFlyoutItem>())
            {
                item.IsEnabled = (string)item.Tag switch
                {
                    "SearchResults_Menu_GoTo" or "SearchResults_CopyHex/Text" or "SearchResults_CopyText/Text" => SelectionCount > 0,
                    "SearchResults_ToBookmarks/Text" => BookmarksRequested is not null && RowCount > 0,
                    _ => RowCount > 0,
                };
            }
        };
        _listMenu.Closed += (_, _) => _targetSelected = null;
        ListHost.ContextFlyout = _listMenu;

        static MenuFlyoutItem MenuItem(string key, Action action)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), Tag = key };
            AutomationProperties.SetAutomationId(item, "SearchResultsMenu_" + key.Split('/')[0]);
            item.Click += (_, _) => action();
            return item;
        }
    }

    // ---- 古い結果の再検索 (FIND-03 の「エラー」) ----

    private async void Research_Click(object sender, RoutedEventArgs e) => await ResearchAsync();

    /// <summary>今のタブの結果を、同じ条件で今の状態に対して探し直す (タブは置き換える)。</summary>
    internal async Task ResearchAsync()
    {
        if (_groups.Count == 0 || _running is not null)
        {
            return;
        }

        var targets = _groups
            .Where(g => !g.Editor.Document.IsDisposed)
            .Select(g => new SearchTarget(g.Editor, g.Name, new SearchResults(g.Editor.Document.Current, g.Results.Pattern, g.Results.Options)))
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        string kindName = _kindName, query = _query;
        System.Text.Encoding encoding = _encoding;
        Detach();
        AttachToActiveTab(targets, kindName, query, encoding);
        await RunSearchAsync(new CancellationTokenSource(), null, continued: false);
    }

    /// <summary>今のタブに結果を出す (置き換え。<see cref="Attach"/> の、タブを選ばない版)。</summary>
    private void AttachToActiveTab(IReadOnlyList<SearchTarget> targets, string kindName, string query, System.Text.Encoding encoding)
    {
        foreach (SearchTarget t in targets)
        {
            t.Editor.Document.Changed += Document_Changed;
            t.Results.MatchesAdded += Results_Changed;
            t.Results.StateChanged += Results_Changed;
            _groups.Add(new Group(t.Editor, t.Name, t.Results));
        }

        _kindName = kindName;
        _query = query;
        _encoding = encoding;
        _selected = _anchor = -1;
        _top = 0;
        _view = null;
        if (IsOrdered)
        {
            ScheduleView(immediately: true);
        }

        BuildHeaders();
        UpdateTabStrip();
        Render();
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 読み込めなかった範囲 (FIND-01 の「エラー」) ----

    /// <summary>テスト用: 再検索のボタンを出しているか (古い結果)。</summary>
    internal bool ResearchVisible => ResearchButton.Visibility == Visibility.Visible;

    /// <summary>テスト用: 読み込めなかった範囲 (すべてのドキュメントの)。</summary>
    internal IReadOnlyList<UnreadableRange> SkippedRanges => [.. _groups.SelectMany(g => g.Results.SkippedRanges)];

    /// <summary>「読み込めなかった範囲 (N)」のボタンとメニュー (選ぶとその位置に移動する)。</summary>
    private void UpdateSkipped()
    {
        int count = _groups.Sum(g => g.Results.SkippedRanges.Count);
        SkippedButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0)
        {
            return;
        }

        string text = Loc.Format("SearchResults_Skipped", count.ToString("N0", CultureInfo.CurrentCulture));
        SkippedButton.Content = text;
        AutomationProperties.SetName(SkippedButton, text);
        if (SkippedMenu.Items.Count == Math.Min(count, SkippedMenuLimit))
        {
            return;
        }

        SkippedMenu.Items.Clear();
        foreach (Group g in _groups)
        {
            foreach (UnreadableRange r in g.Results.SkippedRanges.Take(SkippedMenuLimit - SkippedMenu.Items.Count))
            {
                var item = new MenuFlyoutItem
                {
                    Text = Loc.Format("SearchResults_SkippedItem", StatusFormat.Hex(r.Offset), r.Length.ToString("N0", CultureInfo.CurrentCulture),
                        Loc.Get("Find_Unreadable_Reason_" + r.Reason)),
                };
                EditorState editor = g.Editor;
                long offset = r.Offset;
                item.Click += (_, _) =>
                {
                    ActivateRequested?.Invoke(this, editor);
                    editor.GoTo(Math.Min(offset, editor.Document.Length));
                };
                SkippedMenu.Items.Add(item);
            }
        }
    }
}
