using System.Collections.Concurrent;

namespace HexEditor.Core.Templates.Runtime;

/// <summary>
/// 評価器のスレッド (TPL-37 の仕様 6)。評価は UI スレッドで行わず、スタックの大きい専用のスレッドで行う。評価器は再帰の深さを
/// <c>limit.recursion</c> で制限し、さらに .NET のスタックの残りを確認するため、スタックオーバーフローでプロセスを落とさない。
/// </summary>
internal static class TemplateThreads
{
    /// <summary>評価器のスレッドのスタックの大きさ (予約のみ。使った分だけ確保される)。</summary>
    public const int StackSize = 256 * 1024 * 1024;

    private const int MaxWorkers = 16;

    [ThreadStatic]
    private static bool _isTemplateThread;

    private static readonly BlockingCollection<Action> Queue = new();
    private static int _workers;
    private static int _idle;

    public static bool IsCurrent => _isTemplateThread;

    /// <summary>長く動く評価 (適用の評価) を専用のスレッドで始める。</summary>
    public static Thread StartDedicated(Action body, string name)
    {
        var thread = new Thread(() =>
        {
            _isTemplateThread = true;
            body();
        }, StackSize)
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return thread;
    }

    /// <summary>評価器のスレッドで実行して結果を待つ (すでに評価器のスレッドならそのまま実行する)。</summary>
    public static T Run<T>(Func<T> body)
    {
        if (_isTemplateThread)
        {
            return body();
        }

        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        Post(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }

    public static Task<T> RunAsync<T>(Func<T> body)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(body());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static void Post(Action work)
    {
        Queue.Add(work);
        if (Volatile.Read(ref _idle) == 0 && Volatile.Read(ref _workers) < MaxWorkers)
        {
            Interlocked.Increment(ref _workers);
            var thread = new Thread(Worker, StackSize) { IsBackground = true, Name = "HexEditor template worker" };
            thread.Start();
        }
    }

    private static void Worker()
    {
        _isTemplateThread = true;
        while (true)
        {
            Interlocked.Increment(ref _idle);
            Action work = Queue.Take();
            Interlocked.Decrement(ref _idle);
            work();
        }
    }
}
