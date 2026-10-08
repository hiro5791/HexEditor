using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.View;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索範囲「開いているすべてのドキュメント」(FIND-11 の仕様 1)。次を検索では今のドキュメントの末尾に達したら次のタブのドキュメントへ
/// 進み、一致のあるタブに切り替える。すべて検索では結果一覧をドキュメントごとにまとめる。
/// </summary>
public sealed partial class FindBar
{
    /// <summary>範囲の選択肢の「開いているすべてのドキュメント」の位置。</summary>
    private const int AllDocumentsScope = 3;

    /// <summary>開いているドキュメント (タブの順)。null なら「開いているすべてのドキュメント」は使えない。</summary>
    public Func<IReadOnlyList<(EditorState Editor, string Name)>>? OpenDocuments { get; set; }

    /// <summary>そのビューのタブに切り替える。</summary>
    public Action<EditorState>? ActivateDocument { get; set; }

    /// <summary>範囲が「開いているすべてのドキュメント」か。</summary>
    private bool SearchesAllDocuments => ScopeChoice.SelectedIndex == AllDocumentsScope && OpenDocuments is not null;

    /// <summary>
    /// 次 / 前を検索 (すべてのドキュメント)。今のドキュメントの開始位置から末尾 (先頭) まで、次に後ろ (前) のタブのドキュメントを
    /// 順に、最後に折り返しがオンなら今のドキュメントの先頭 (末尾) から開始位置まで探す。
    /// </summary>
    private async Task FindInAllDocumentsAsync(bool forward)
    {
        if (Editor is not { } editor || _pattern is not { } pattern || OpenDocuments?.Invoke() is not { Count: > 0 } docs)
        {
            return;
        }

        AddToHistory();
        StopIncremental();
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        bool wrap = WrapChoice.IsChecked == true;
        int current = Math.Max(0, docs.ToList().FindIndex(d => d.Editor == editor));
        var order = new List<int>(docs.Count);
        for (int k = 1; k < docs.Count; k++)
        {
            order.Add(((current + (forward ? k : -k)) % docs.Count + docs.Count) % docs.Count);
        }

        DocumentSnapshot[] snapshots = [.. docs.Select(d => d.Editor.Document.Current)];
        long start = _navigator.StartFor(forward, editor.Cursor, editor.SelectionStart, editor.SelectionLength);
        Status.Text = Loc.Get("Find_Searching");
        StartProgress();
        try
        {
            (int Doc, SearchHit Hit)? Work(LongRunningOperation? op)
            {
                CancellationToken token = op?.CancellationToken ?? cts.Token;
                SearchOptions options = NewOptions(editor, SearchScope.WholeDocument, cts.Token);
                if (SearchEngine.Find(snapshots[current], pattern, start, forward, wrap: false, options, op, token) is { } here)
                {
                    return (current, here);
                }

                foreach (int i in order)
                {
                    DocumentSnapshot s = snapshots[i];
                    if (SearchEngine.Find(s, pattern, forward ? 0 : s.Length, forward, wrap: false, options, op, token) is { } there)
                    {
                        return (i, there);
                    }
                }

                // 一周して今のドキュメントの反対側から開始位置まで (開始位置より後 (前) に一致がないことは分かっている)。
                DocumentSnapshot c = snapshots[current];
                return wrap && SearchEngine.Find(c, pattern, forward ? 0 : c.Length, forward, wrap: false, options, op, token) is { } wrapped
                    ? (current, wrapped with { Wrapped = true })
                    : null;
            }

            (int Doc, SearchHit Hit)? found = Operations is null
                ? await Task.Run(() => Work(null), cts.Token)
                : await Operations.RunAsync(Loc.Get("Operation_Find"), OperationKind.ReadOnly, editor.Document, snapshots.Sum(s => s.Length), op =>
                {
                    cts.Token.Register(op.Cancel);
                    _activeOperation = op;
                    return Task.FromResult(Work(op));
                });
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (found is (int doc, SearchHit hit))
            {
                EditorState target = docs[doc].Editor;
                if (target != editor)
                {
                    // 一致のあるタブに切り替える (検索バーの対象もそのビューに変わる)。
                    ActivateDocument?.Invoke(target);
                    Editor = target;
                }

                target.SelectMatch(hit.Offset, hit.Length);
                _navigator.Remember(hit);
                Status.Text = hit.Wrapped ? Loc.Get(forward ? "Find_WrappedToStart" : "Find_WrappedToEnd") : string.Empty;
                MarkQuery(QueryState.Normal);
                UpdateCountText();
                Announce(hit.Wrapped ? Status.Text : Loc.Format("Find_FoundAt", StatusFormat.Hex(hit.Offset)));
                ReportResult(hit.Wrapped ? Status.Text : Loc.Format("Find_FoundAt", StatusFormat.Hex(hit.Offset)), hit.Wrapped);
            }
            else
            {
                Status.Text = wrap ? Loc.Get("Find_NotFound") : Loc.Get(forward ? "Find_NotFoundToEnd" : "Find_NotFoundToStart");
                MarkQuery(QueryState.NotFound);
                Announce(Status.Text);
                ReportResult(Status.Text, important: false);
            }
        }
        catch (OperationCanceledException)
        {
            Status.Text = Loc.Get("Find_Cancelled");
        }
        catch (SearchAbortedException ex)
        {
            ShowAborted(ex);
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
                UpdateProgress();
            }
        }
    }

    /// <summary>すべて検索の対象 (すべてのドキュメントなら、タブの順にすべて)。</summary>
    private IReadOnlyList<SearchTarget> FindAllTargets(EditorState editor, SearchPattern pattern, SearchOptions options)
    {
        if (!SearchesAllDocuments || OpenDocuments!() is not { Count: > 0 } docs)
        {
            return [new SearchTarget(editor, string.Empty, new SearchResults(editor.Document.Current, pattern, options))];
        }

        return [.. docs.Select(d => new SearchTarget(d.Editor, d.Name, new SearchResults(d.Editor.Document.Current, pattern, options)))];
    }
}
