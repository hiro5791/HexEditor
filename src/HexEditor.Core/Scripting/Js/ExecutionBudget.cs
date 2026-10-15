using System.Diagnostics;
using Jint;

namespace HexEditor.Core.Scripting.Js;

/// <summary>
/// 実行時間とメモリの上限 (AUTO-02 の仕様 4・5)。Jint の制約として、文の実行の合間に調べる。
/// <list type="bullet">
/// <item>実行時間は JavaScript のコードを実行している時間だけを数える。イベントループの待ち (一括処理 API の完了待ち・利用者の入力待ち)
/// と <see cref="Pause"/> の間は数えない。</item>
/// <item>上限に達したら一時停止し、対話的な実行では「続ける」「停止」を尋ねる (<see cref="OnTimeLimit"/>)。</item>
/// <item>メモリは、実行を始めてからのマネージドヒープの増加で数える (Jint が管理するオブジェクトの使用量の近似)。上限を超えたら
/// 一度ガベージコレクションをして、まだ超えていれば中断する。</item>
/// </list>
/// </summary>
internal sealed class ExecutionBudget : Constraint, IExecutionClock
{
    private static readonly long MemoryCheckInterval = Stopwatch.Frequency / 20;

    private readonly Lock _lock = new();
    private readonly long _memoryLimit;
    private readonly long _baseline;
    private long _limitTicks;
    private long _usedTicks;
    private long _segmentStart;
    private bool _running;
    private int _pauseDepth;
    private long _lastMemoryCheck;
    private volatile bool _forceStop;
    private volatile bool _normalStop;

    public ExecutionBudget(TimeSpan? limit, long memoryLimit)
    {
        _limitTicks = limit is { } l ? (long)(l.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;
        Period = limit;
        _memoryLimit = memoryLimit;
        // 始める前のゴミを数えないよう、ガベージコレクションをしてから測る。
        _baseline = GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>
    /// 実行時間の上限に達したときに呼ぶ (スクリプトのスレッドで同期に)。true なら上限を同じだけ延ばして続ける。null なら止める。
    /// </summary>
    public Func<bool>? OnTimeLimit { get; set; }

    /// <summary>実行時間の上限 (設定の値。「続ける」で延ばす長さ)。null は無制限。</summary>
    public TimeSpan? Limit => Period;

    private TimeSpan? Period { get; }

    /// <summary>これまでに数えた実行時間。</summary>
    public TimeSpan Used
    {
        get
        {
            lock (_lock)
            {
                return TimeSpan.FromSeconds((double)UsedTicks(Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
            }
        }
    }

    private long UsedTicks(long now) => _usedTicks + (_segmentStart != 0 ? now - _segmentStart : 0);

    /// <summary>強制的に中断する (別のスレッドから呼べる)。次の制約の確認で中断する。</summary>
    public void RequestForceStop(bool normal = false)
    {
        _normalStop = normal;
        _forceStop = true;
    }

    public bool ForceStopRequested => _forceStop;

    public bool NormalStop => _normalStop;

    /// <summary>JavaScript の実行を始めた (イベントループの 1 回分の始まり)。</summary>
    public void Resume()
    {
        lock (_lock)
        {
            _running = true;
            if (_pauseDepth == 0 && _segmentStart == 0)
            {
                _segmentStart = Stopwatch.GetTimestamp();
            }
        }
    }

    /// <summary>JavaScript の実行が止まった (イベントループの待ち)。</summary>
    public void Suspend()
    {
        lock (_lock)
        {
            _running = false;
            Close();
        }
    }

    private void Close()
    {
        if (_segmentStart != 0)
        {
            _usedTicks += Stopwatch.GetTimestamp() - _segmentStart;
            _segmentStart = 0;
        }
    }

    public IDisposable Pause()
    {
        lock (_lock)
        {
            if (_pauseDepth++ == 0)
            {
                Close();
            }
        }

        return new PauseScope(this);
    }

    private void EndPause()
    {
        lock (_lock)
        {
            if (--_pauseDepth == 0 && _running)
            {
                _segmentStart = Stopwatch.GetTimestamp();
            }
        }
    }

    public override void Check()
    {
        if (_forceStop)
        {
            throw new ScriptTerminatedException(_normalStop);
        }

        long now = Stopwatch.GetTimestamp();
        long used;
        lock (_lock)
        {
            used = UsedTicks(now);
        }

        if (used > _limitTicks)
        {
            HandleTimeLimit();
        }

        if (now - _lastMemoryCheck > MemoryCheckInterval)
        {
            _lastMemoryCheck = now;
            CheckMemory();
        }
    }

    /// <summary>メモリの上限を調べる (大きな配列を作った直後などに呼び出し側からも呼ぶ)。</summary>
    public void CheckMemory()
    {
        long used = GC.GetTotalMemory(false) - _baseline;
        if (used <= _memoryLimit)
        {
            return;
        }

        used = GC.GetTotalMemory(true) - _baseline;
        if (used > _memoryLimit)
        {
            throw new ScriptLimitExceededException("memory", $"メモリの上限 {FormatBytes(_memoryLimit)} を超えました。");
        }
    }

    private void HandleTimeLimit()
    {
        TimeSpan limit = Limit ?? TimeSpan.Zero;
        bool resume = false;
        if (OnTimeLimit is { } ask)
        {
            using (Pause())
            {
                resume = ask();
            }
        }

        if (_forceStop)
        {
            throw new ScriptTerminatedException(_normalStop);
        }

        if (!resume)
        {
            throw new ScriptLimitExceededException("time", $"実行時間の上限 {limit.TotalSeconds:0.#} 秒を超えました。");
        }

        lock (_lock)
        {
            // 「続ける (さらに N 秒)」(AUTO-02 の仕様 5)。
            _limitTicks = UsedTicks(Stopwatch.GetTimestamp()) + (long)(limit.TotalSeconds * Stopwatch.Frequency);
        }
    }

    public override void Reset()
    {
        // 実行時間は実行全体で数える (Jint の Execute ごとに戻さない)。
    }

    internal static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 && bytes % (1024L * 1024 * 1024) == 0 ? $"{bytes / (1024L * 1024 * 1024)} GB" : $"{bytes / (1024 * 1024)} MB";

    private sealed class PauseScope(ExecutionBudget budget) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                budget.EndPause();
            }
        }
    }
}
