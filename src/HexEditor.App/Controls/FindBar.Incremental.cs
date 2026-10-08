using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// インクリメンタルサーチ (FIND-27)。入力が 150 ms 止まったら、検索バーを開いたときのカーソル位置 (起点) から前方 256 MB を探す。
/// 新しい検索を始める前に前の検索を取り消して終わるのを待つので、同時に動く検索は 1 つだけ。IME の変換中と不正な入力の間は探さない。
/// </summary>
public sealed partial class FindBar
{
    /// <summary>入力が止まってから探すまでの時間 (FIND-27 の仕様 2)。</summary>
    private static readonly TimeSpan IncrementalDelay = TimeSpan.FromMilliseconds(150);

    private DispatcherQueueTimer? _incrementalTimer;
    private CancellationTokenSource? _incremental;
    private Task _incrementalTask = Task.CompletedTask;
    private long _origin;
    private bool _composing;
    private bool _incrementalMoved;
    private int _incrementalId;
    private static int s_incrementalRunning;

    /// <summary>「Enter で続きを検索」を出しているか (起点から 256 MB の範囲で見つからなかった。FIND-27 の仕様 6)。</summary>
    internal bool IncrementalTruncated { get; private set; }

    private void InitializeIncremental()
    {
        _incrementalTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _incrementalTimer.Interval = IncrementalDelay;
        _incrementalTimer.IsRepeating = false;
        _incrementalTimer.Tick += async (_, _) => await RunIncrementalAsync();
    }

    /// <summary>入力が変わったら、150 ms 後に探す (待っている間にまた入力があれば待ち直す)。</summary>
    private void ScheduleIncremental()
    {
        if (!IsOpen || IncrementalChoice.IsChecked != true
            || Kind is not (SearchKind.Hex or SearchKind.Text) || _composing || _incrementalTimer is null)
        {
            return;
        }

        IncrementalTruncated = false;
        _incrementalTimer.Stop();
        _incrementalTimer.Start();
    }

    private async Task RunIncrementalAsync()
    {
        // 不正な入力 (Hex の奇数桁など) の間は探さない (仕様 7)。IME の変換中も探さない (仕様 8)。
        if (_composing || Editor is not { } editor || _pattern is not { } pattern || !ScopeIsValid)
        {
            return;
        }

        // 前の検索を取り消し、終わるのを待ってから始める (仕様 2)。
        _incremental?.Cancel();
        try
        {
            await _incrementalTask;
        }
        catch (OperationCanceledException)
        {
        }

        var cts = new CancellationTokenSource();
        _incremental = cts;
        int id = ++_incrementalId;
        DocumentSnapshot snapshot = editor.Document.Current;
        long origin = Math.Clamp(_origin, 0, snapshot.Length);
        SearchScope window = CurrentScope.Clip(origin, SearchScope.IncrementalWindow, snapshot.Length, out bool truncated);
        Task<SearchHit?> search = Task.Run(() =>
        {
            // 実行中の検索の数を記録する (テストで「同時に 1 つだけ」を確かめる。仕様 2)。
            int running = Interlocked.Increment(ref s_incrementalRunning);
            AppLog.Info($"Incremental search: start #{id} (running {running})");
            try
            {
                return SearchEngine.Find(snapshot, pattern, origin, forward: true, wrap: false, new SearchOptions { Scope = window }, null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                AppLog.Info($"Incremental search: cancel #{id}");
                throw;
            }
            finally
            {
                running = Interlocked.Decrement(ref s_incrementalRunning);
                AppLog.Info($"Incremental search: end #{id} (running {running})");
            }
        }, cts.Token);
        _incrementalTask = search;
        SearchHit? hit;
        try
        {
            hit = await search;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_incremental == cts)
            {
                _incremental = null;
            }
        }

        if (cts.IsCancellationRequested || !IsOpen || !ReferenceEquals(pattern, _pattern))
        {
            return;
        }

        if (hit is { } h)
        {
            editor.SelectMatch(h.Offset, h.Length);
            _navigator.Remember(hit);
            _incrementalMoved = true;
            MarkQuery(QueryState.Normal);
            UpdateCountText();
        }
        else
        {
            // 見つからなければカーソルを起点に戻す (仕様 4)。256 MB で止めた場合は「Enter で続きを検索」(仕様 6)。
            IncrementalTruncated = truncated;
            Status.Text = Loc.Get(truncated ? "Find_ContinueWithEnter" : "Find_NotFound");
            MarkQuery(QueryState.NotFound);
            if (editor.Cursor != origin || editor.HasSelection)
            {
                editor.GoTo(origin);
            }
        }
    }

    /// <summary>入力を待っているインクリメンタルサーチと実行中のものを止める (Enter などの通常の検索が優先する)。</summary>
    private void StopIncremental()
    {
        _incrementalTimer?.Stop();
        _incremental?.Cancel();
    }

    private void Query_CompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _composing = true;
        _incrementalTimer?.Stop();
    }

    private void Query_CompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _composing = false;
        ScheduleIncremental();
    }
}
