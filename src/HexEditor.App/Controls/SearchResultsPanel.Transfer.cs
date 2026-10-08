using HexEditor.Core.View;

namespace HexEditor.App.Controls;

/// <summary>
/// 結果一覧を別のウィンドウの一覧に移す (タブを別のウィンドウに移したとき。UI-11 の仕様 3)。実行中のすべて検索は止めずに、移した先の一覧に
/// 結果が届く。
/// </summary>
public sealed partial class SearchResultsPanel
{
    /// <summary>実行中のすべて検索を移した先 (検索が終わったときの後始末をその一覧で行う)。</summary>
    private readonly Dictionary<CancellationTokenSource, SearchResultsPanel> _movedRuns = [];

    /// <summary>
    /// 今の結果 (実行中を含む) を <paramref name="target"/> に移し、この一覧を閉じる。複数のドキュメントの結果は、そのタブだけを移すと
    /// 他の結果が残らないため移さない。移したら true。
    /// </summary>
    public bool TransferTo(SearchResultsPanel target)
    {
        if (_groups.Count != 1 || target == this)
        {
            return false;
        }

        List<Group> groups = [.. _groups];
        CancellationTokenSource? running = _running;
        foreach (Group g in groups)
        {
            g.Results.MatchesAdded -= Results_Changed;
            g.Results.StateChanged -= Results_Changed;
            g.Editor.Document.Changed -= Document_Changed;
        }

        _groups.Clear();
        _running = null;
        _generation++;
        _cache.Clear();
        _fetching.Clear();
        if (running is not null)
        {
            _movedRuns[running] = target;
        }

        Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
        Closed?.Invoke(this, EventArgs.Empty);

        target.Receive(groups, running, _kindName, _query, _encoding);
        return true;
    }

    private void Receive(List<Group> groups, CancellationTokenSource? running, string kindName, string query, System.Text.Encoding encoding)
    {
        _running?.Cancel();
        Detach();
        foreach (Group g in groups)
        {
            g.Factory = null;
            g.Results.MatchesAdded += Results_Changed;
            g.Results.StateChanged += Results_Changed;
            g.Editor.Document.Changed += Document_Changed;
            _groups.Add(g);
        }

        _running = running;
        _kindName = kindName;
        _query = query;
        _encoding = encoding;
        _selected = _anchor = -1;
        _top = 0;
        ToBookmarksItem.IsEnabled = BookmarksRequested is not null && _groups.Count == 1;
        BuildHeaders();
        Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        Shown?.Invoke(this, EventArgs.Empty);
        UpdateHeader();
        _dirty = true;
        Refresh();
    }

    /// <summary>すべて検索が終わったときに後始末をする一覧 (別のウィンドウに移していればその一覧)。</summary>
    private SearchResultsPanel OwnerOfRun(CancellationTokenSource cts)
    {
        SearchResultsPanel owner = this;
        while (owner._movedRuns.Remove(cts, out SearchResultsPanel? next))
        {
            owner = next;
        }

        return owner;
    }

    /// <summary>この一覧が結果を出しているビューか (移した後の確認用)。</summary>
    internal bool ShowsOnly(EditorState editor) => _groups.Count == 1 && _groups[0].Editor == editor;
}
