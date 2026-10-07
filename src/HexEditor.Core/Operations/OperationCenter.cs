namespace HexEditor.Core.Operations;

/// <summary>
/// 長時間処理を実行・管理する (ENG-09)。ドキュメントを変更する処理と保存は、1 つのドキュメントにつき同時に 1 つだけ
/// 実行し、後から開始したものは待機する。読み取りのみの処理は同時に実行できる。
/// </summary>
public sealed class OperationCenter
{
    /// <summary>履歴に残す件数 (ENG-09 の仕様 11)。</summary>
    public const int HistoryLimit = 50;

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly List<LongRunningOperation> _active = [];
    private readonly LinkedList<LongRunningOperation> _history = new();
    private readonly Dictionary<object, SemaphoreSlim> _exclusive = new(ReferenceEqualityComparer.Instance);

    public OperationCenter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>処理が始まった・終わった・進捗が変わった。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<LongRunningOperation> Active
    {
        get
        {
            lock (_lock)
            {
                return [.. _active];
            }
        }
    }

    public IReadOnlyList<LongRunningOperation> History
    {
        get
        {
            lock (_lock)
            {
                return [.. _history];
            }
        }
    }

    /// <summary>
    /// 処理をバックグラウンドで実行する。<paramref name="work"/> の例外は処理の失敗として記録し、呼び出し元にも伝える。
    /// </summary>
    /// <param name="onLockChanged">ドキュメントの編集の受け付けを止める・再開するときに呼ぶ (変更する処理と保存のみ)。</param>
    public async Task<T> RunAsync<T>(
        string name,
        OperationKind kind,
        object? target,
        long? totalBytes,
        Func<LongRunningOperation, Task<T>> work,
        Action<bool>? onLockChanged = null)
    {
        var op = new LongRunningOperation(name, kind, target, totalBytes, _time);
        op.ProgressChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        lock (_lock)
        {
            _active.Add(op);
        }

        Changed?.Invoke(this, EventArgs.Empty);

        SemaphoreSlim? gate = kind != OperationKind.ReadOnly && target is not null ? GateFor(target) : null;
        bool entered = false;
        try
        {
            if (gate is not null)
            {
                await gate.WaitAsync(op.CancellationToken).ConfigureAwait(false);
                entered = true;
                onLockChanged?.Invoke(true);
            }

            op.State = OperationState.Running;
            op.RaiseProgressChanged();
            T result = await Task.Run(() => work(op), op.CancellationToken).ConfigureAwait(false);
            op.State = OperationState.Completed;
            return result;
        }
        catch (OperationCanceledException) when (op.CancellationToken.IsCancellationRequested)
        {
            op.State = OperationState.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            op.State = OperationState.Failed;
            op.Error = ex;
            throw;
        }
        finally
        {
            if (entered)
            {
                onLockChanged?.Invoke(false);
                gate!.Release();
            }

            lock (_lock)
            {
                _active.Remove(op);
                _history.AddFirst(op);
                while (_history.Count > HistoryLimit)
                {
                    _history.RemoveLast();
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task RunAsync(
        string name,
        OperationKind kind,
        object? target,
        long? totalBytes,
        Func<LongRunningOperation, Task> work,
        Action<bool>? onLockChanged = null) =>
        RunAsync<bool>(name, kind, target, totalBytes, async op => { await work(op).ConfigureAwait(false); return true; }, onLockChanged);

    /// <summary>対象のドキュメントで実行中の処理 (閉じるときの確認に使う。ENG-09 の仕様 12)。</summary>
    public IReadOnlyList<LongRunningOperation> ActiveFor(object target)
    {
        lock (_lock)
        {
            return _active.Where(o => ReferenceEquals(o.Target, target)).ToList();
        }
    }

    /// <summary>
    /// 対象のドキュメントで実行中・待機中の処理をすべてキャンセルし、止まるまで待つ (ENG-09 の仕様 12 の「処理をキャンセルして閉じる」、
    /// ENG-17 の仕様 5)。<paramref name="exceptSaves"/> が真なら保存 (外部に書き出す処理) はキャンセルせずに完了を待つ
    /// (「保存の完了を待って閉じる」)。
    /// </summary>
    public async Task CancelAndWaitAsync(object target, bool exceptSaves = false, CancellationToken cancellationToken = default)
    {
        foreach (LongRunningOperation op in ActiveFor(target))
        {
            if (!exceptSaves || op.Kind != OperationKind.WritesExternal)
            {
                op.Cancel();
            }
        }

        await WhenIdleAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>対象のドキュメントの処理がすべて終わるまで待つ。</summary>
    public async Task WhenIdleAsync(object target, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e)
            {
                if (ActiveFor(target).Count == 0)
                {
                    idle.TrySetResult();
                }
            }

            Changed += OnChanged;
            try
            {
                if (ActiveFor(target).Count == 0)
                {
                    return;
                }

                await idle.Task.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // Changed を取りこぼした場合に備えて、定期的に確かめ直す。
            }
            finally
            {
                Changed -= OnChanged;
            }
        }
    }

    /// <summary>
    /// 閉じるときの確認ダイアログに出す、対象のドキュメントで実行中の処理の一覧と、保存中かどうか (ENG-09 の仕様 12)。
    /// 保存中なら「保存の完了を待って閉じる」も選べる。
    /// </summary>
    public (IReadOnlyList<LongRunningOperation> Operations, bool IncludesSave) DescribeActive(object target)
    {
        IReadOnlyList<LongRunningOperation> active = ActiveFor(target);
        return (active, active.Any(o => o.Kind == OperationKind.WritesExternal));
    }

    private SemaphoreSlim GateFor(object target)
    {
        lock (_lock)
        {
            if (!_exclusive.TryGetValue(target, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _exclusive[target] = gate;
            }

            return gate;
        }
    }
}
