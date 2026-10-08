using System.Globalization;
using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 結果のタブと固定 (FIND-20 の仕様 3)。すべて検索のたびに、設定「新しいタブに出す」がオンなら新しいタブを作り、オフなら今のタブの結果を
/// 置き換える。「固定」したタブは置き換えず、新しいタブを作る。今のタブの状態 (結果・並べ替え・絞り込み・選んだ行・実行中の検索) は
/// パネルのフィールドに持ち、裏のタブの状態は <see cref="ResultTab"/> に預ける。裏のタブのすべて検索は止めずに続ける。
/// </summary>
public sealed partial class SearchResultsPanel
{
    private readonly List<ResultTab> _tabs = [];
    private int _activeTab = -1;
    private bool _loadingTab;

    /// <summary>タブの数 (テスト用)。</summary>
    internal int TabCount => _tabs.Count;

    /// <summary>今のタブの番号 (0 から。なければ −1。テスト用)。</summary>
    internal int ActiveTabIndex => _activeTab;

    /// <summary>今のタブを固定しているか。</summary>
    internal bool IsPinned => _activeTab >= 0 && _tabs[_activeTab].Pinned;

    /// <summary>タブの見出し (テスト用)。</summary>
    internal IReadOnlyList<string> TabTitles => [.. _tabs.Select((t, i) => TitleOf(t, i == _activeTab))];

    /// <summary>新しい結果を出すタブを用意する: 置き換えなら今のタブを空にし、そうでなければ新しいタブを作って今のタブにする。</summary>
    private void PrepareTabForNewResults()
    {
        bool newTab = _activeTab < 0 || _tabs[_activeTab].Pinned
            || (App.Settings?.GetBool(SearchSettings.NewTabKey, false) ?? false);
        if (newTab)
        {
            OpenNewTab();
        }
        else
        {
            Detach();
        }
    }

    /// <summary>今のタブを裏に預けて、空の新しいタブを今のタブにする。</summary>
    private void OpenNewTab()
    {
        if (_activeTab >= 0)
        {
            StashActive();
        }
        else
        {
            Detach();
        }

        _tabs.Add(new ResultTab());
        _activeTab = _tabs.Count - 1;
        ResetLiveState();
    }

    /// <summary>今のタブの状態を預け、結果の通知を外す (結果は破棄しない)。</summary>
    private void StashActive()
    {
        ResultTab t = _tabs[_activeTab];
        t.Groups.Clear();
        t.Groups.AddRange(_groups);
        t.KindName = _kindName;
        t.Query = _query;
        t.Encoding = _encoding;
        t.Running = _running;
        t.View = _view;
        t.SortKey = _sortKey;
        t.SortDescending = _sortDescending;
        t.Filter = _filter;
        t.Top = _top;
        t.Selected = _selected;
        t.Anchor = _anchor;
        foreach (Group g in _groups)
        {
            g.Results.MatchesAdded -= Results_Changed;
            g.Results.StateChanged -= Results_Changed;
            g.Editor.Document.Changed -= Document_Changed;
        }

        _groups.Clear();
        _running = null;
        _viewCts?.Cancel();
        _generation++;
        _cache.Clear();
        _fetching.Clear();
    }

    /// <summary>今のタブの表示の状態を初期値にする (結果は空)。</summary>
    private void ResetLiveState()
    {
        _view = null;
        _sortKey = SearchResultSortKey.Number;
        _sortDescending = false;
        SetFilterSilently(string.Empty);
        _top = 0;
        _selected = _anchor = -1;
    }

    /// <summary>タブに切り替える (テスト用の命令の通り道からも呼ぶ)。</summary>
    internal void SelectTab(int index)
    {
        if (index < 0 || index >= _tabs.Count || index == _activeTab)
        {
            return;
        }

        StashActive();
        LoadTab(index);
    }

    private void LoadTab(int index)
    {
        _activeTab = index;
        ResultTab t = _tabs[index];
        foreach (Group g in t.Groups)
        {
            g.Factory = null;
            g.Results.MatchesAdded += Results_Changed;
            g.Results.StateChanged += Results_Changed;
            g.Editor.Document.Changed += Document_Changed;
            _groups.Add(g);
        }

        // 今のタブの結果はフィールドが持つ (預けた一覧は空にして、二重に持たない)。
        t.Groups.Clear();
        _kindName = t.KindName;
        _query = t.Query;
        _encoding = t.Encoding;
        _running = t.Running;
        t.Running = null;
        _view = t.View;
        _sortKey = t.SortKey;
        _sortDescending = t.SortDescending;
        SetFilterSilently(t.Filter);
        _top = t.Top;
        _selected = t.Selected;
        _anchor = t.Anchor;
        _generation++;
        _cache.Clear();
        _fetching.Clear();
        BuildHeaders();
        UpdateTabStrip();
        if (IsOrdered)
        {
            // 預けている間に結果が増えていれば並びを作り直す。
            ScheduleView(immediately: true);
        }

        Render();
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>タブを閉じる (実行中のすべて検索も止める)。最後のタブなら一覧を閉じる。</summary>
    internal void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count)
        {
            return;
        }

        if (_tabs.Count == 1)
        {
            Close();
            return;
        }

        if (index == _activeTab)
        {
            _running?.Cancel();
            Detach();
            _tabs.RemoveAt(index);
            _activeTab = -1;
            LoadTab(Math.Min(index, _tabs.Count - 1));
            return;
        }

        DiscardTab(_tabs[index]);
        _tabs.RemoveAt(index);
        if (index < _activeTab)
        {
            _activeTab--;
        }

        UpdateTabStrip();
    }

    /// <summary>今のタブの固定を切り替える。</summary>
    internal void TogglePin()
    {
        if (_activeTab >= 0)
        {
            _tabs[_activeTab].Pinned = !_tabs[_activeTab].Pinned;
            PinButton.IsChecked = IsPinned;
            UpdateTabStrip();
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => TogglePin();

    /// <summary>裏のタブをすべて捨てる (一覧を閉じるとき)。今のタブはこの前に <see cref="Detach"/> で空にしておく。</summary>
    private void DiscardOtherTabs()
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i != _activeTab)
            {
                DiscardTab(_tabs[i]);
            }
        }

        _tabs.Clear();
        _activeTab = -1;
        UpdateTabStrip();
    }

    /// <summary>裏のタブの検索を止め、結果を破棄する (実行中なら、検索の後始末で破棄される)。</summary>
    private static void DiscardTab(ResultTab t)
    {
        if (t.Running is { } running)
        {
            running.Cancel();
        }
        else
        {
            foreach (Group g in t.Groups)
            {
                g.Results.Dispose();
            }
        }

        t.Groups.Clear();
    }

    /// <summary>終わったすべて検索の記録を、それを持つ裏のタブから外す。</summary>
    private void ForgetRun(CancellationTokenSource cts)
    {
        foreach (ResultTab t in _tabs.Where(t => t.Running == cts))
        {
            t.Running = null;
        }
    }

    /// <summary>その結果を裏のタブが持っているか。</summary>
    private bool HoldsInOtherTab(List<Group> groups) =>
        _tabs.Where((_, i) => i != _activeTab).Any(t => t.Groups.Count > 0 && t.Groups.SequenceEqual(groups));

    /// <summary>絞り込み欄の文字を、絞り込みのやり直しをせずに変える (タブの切り替え)。</summary>
    private void SetFilterSilently(string text)
    {
        _filter = text;
        if (FilterBox.Text != text)
        {
            _loadingTab = true;
            FilterBox.Text = text;
            _loadingTab = false;
        }
    }

    /// <summary>タブの並びを作り直す (2 つ以上あるときだけ出す)。</summary>
    private void UpdateTabStrip()
    {
        TabStripHost.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TabStrip.Children.Clear();
        if (_tabs.Count <= 1)
        {
            return;
        }

        Brush accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        Brush stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        for (int i = 0; i < _tabs.Count; i++)
        {
            int index = i;
            bool active = i == _activeTab;
            string title = TitleOf(_tabs[i], active);
            var select = new ToggleButton
            {
                IsChecked = active,
                Padding = new Thickness(8, 2, 8, 2),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 12, Visibility = _tabs[i].Pinned ? Visibility.Visible : Visibility.Collapsed },
                        new TextBlock { Text = title, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            AutomationProperties.SetAutomationId(select, "SearchResults_Tab" + i.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(select, _tabs[i].Pinned ? Loc.Format("SearchResults_TabPinned", title) : title);
            select.Click += (_, _) =>
            {
                SelectTab(index);
                UpdateTabStrip();
            };
            var close = new Button
            {
                Padding = new Thickness(4, 2, 4, 2),
                Content = new FontIcon { Glyph = "", FontSize = 10 },
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetAutomationId(close, "SearchResults_TabClose" + i.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(close, Loc.Format("SearchResults_TabClose", title));
            ToolTipService.SetToolTip(close, Loc.Format("SearchResults_TabClose", title));
            close.Click += (_, _) => CloseTab(index);
            var chip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                BorderBrush = active ? accent : stroke,
                BorderThickness = new Thickness(0, 0, 0, active ? 2 : 1),
                Children = { select, close },
            };
            TabStrip.Children.Add(chip);
        }
    }

    /// <summary>タブの見出し: 「Hex: AB CD (100)」。</summary>
    private string TitleOf(ResultTab t, bool active)
    {
        string kind = active ? _kindName : t.KindName;
        string query = active ? _query : t.Query;
        long count = active ? _groups.Sum(g => g.Results.LongCount) : t.Groups.Sum(g => g.Results.LongCount);
        return Loc.Format("SearchResults_TabTitle", kind, query, count.ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>1 つのタブの預かった状態。</summary>
    private sealed class ResultTab
    {
        public List<Group> Groups { get; } = [];

        public string KindName { get; set; } = string.Empty;

        public string Query { get; set; } = string.Empty;

        public Encoding Encoding { get; set; } = Encoding.ASCII;

        public CancellationTokenSource? Running { get; set; }

        public long[]? View { get; set; }

        public SearchResultSortKey SortKey { get; set; } = SearchResultSortKey.Number;

        public bool SortDescending { get; set; }

        public string Filter { get; set; } = string.Empty;

        public long Top { get; set; }

        public long Selected { get; set; } = -1;

        public long Anchor { get; set; } = -1;

        public bool Pinned { get; set; }
    }
}
