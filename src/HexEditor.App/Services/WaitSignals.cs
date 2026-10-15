using HexEditor.App.ViewModels;
using HexEditor.Platform;

namespace HexEditor.App.Services;

/// <summary>
/// <c>hexed open --wait</c> (08 の AUTO-37) の合図を送る。<c>hexed</c> が作った名前付きのイベント (<see cref="WaitSignal"/>) を、その起動で開いた
/// ドキュメントがすべて閉じられたとき (または、アプリが終了するとき) に Set する。外部ツールのエディタとして使われる (<c>git commit</c> など)。
/// 閉じたかどうかは 250 ms ごとに調べる (タブを別のウィンドウに移しても閉じたことにならない。ドキュメントの破棄で判断する)。
/// </summary>
internal static class WaitSignals
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly List<Pending> s_pending = [];
    private static readonly object s_lock = new();
    private static Timer? s_timer;

    private sealed record Pending(string Id, IReadOnlyList<DocumentViewModel> Documents);

    /// <summary>この起動で開いたドキュメントを登録する。ドキュメントがない (開けなかった) 場合は、すぐに合図を送る。</summary>
    public static void Register(string id, IReadOnlyList<DocumentViewModel> documents)
    {
        if (documents.Count == 0)
        {
            Signal(id);
            return;
        }

        lock (s_lock)
        {
            s_pending.Add(new Pending(id, [.. documents]));
            s_timer ??= new Timer(_ => Poll(), null, PollInterval, PollInterval);
        }
    }

    /// <summary>登録されている合図をすべて送る (アプリの終了時)。</summary>
    public static void SignalAll()
    {
        List<Pending> all;
        lock (s_lock)
        {
            all = [.. s_pending];
            s_pending.Clear();
            s_timer?.Dispose();
            s_timer = null;
        }

        foreach (Pending pending in all)
        {
            Signal(pending.Id);
        }
    }

    private static void Poll()
    {
        List<Pending> done;
        lock (s_lock)
        {
            done = [.. s_pending.Where(p => p.Documents.All(IsClosed))];
            foreach (Pending pending in done)
            {
                s_pending.Remove(pending);
            }

            if (s_pending.Count == 0)
            {
                s_timer?.Dispose();
                s_timer = null;
            }
        }

        foreach (Pending pending in done)
        {
            Signal(pending.Id);
        }
    }

    private static bool IsClosed(DocumentViewModel document) => document.Document.IsDisposed;

    private static void Signal(string id)
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(WaitSignal.EventName(id));
            handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            // hexed が Ctrl+C などで先に終わった。合図は要らない。
            AppLog.Debug("The wait signal was not found (hexed has ended).");
        }
    }
}
