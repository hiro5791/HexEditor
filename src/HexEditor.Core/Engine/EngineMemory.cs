using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Engine;

/// <summary>メモリの上限の確認の結果 (ENG-08 の仕様 2)。</summary>
public enum MemoryStatus
{
    /// <summary>上限以下。</summary>
    WithinLimit,

    /// <summary>上限を超えていたため、キャッシュを縮めるか追加バッファを退避して上限以下にした。</summary>
    Reduced,

    /// <summary>
    /// 縮めても上限を超えている (木のノードが多すぎる)。UI は InfoBar で「編集の数が多く、メモリの上限 (1 GiB) を超えています。
    /// 保存すると使用量が減ります」と示す。Undo 履歴は自動では削除しない。
    /// </summary>
    OverLimit,
}

/// <summary>
/// アプリ全体のエンジンのメモリ使用量を管理する (ENG-08)。上限はアプリ全体 (全ドキュメントの合計) に対するもの。
/// ブロックキャッシュの上限 (ENG-06 の仕様 2) も全ドキュメントで分け合う。
/// </summary>
public sealed class EngineMemory
{
    /// <summary>メモリ不足のときにブロックキャッシュを縮める大きさ (全ドキュメントの合計)。</summary>
    public const long LowMemoryCacheBytes = 16L * 1024 * 1024;

    /// <summary>エンジンのメモリ上限の既定値 (ENG-08 の仕様 1)。</summary>
    public const long DefaultLimit = 1024L * 1024 * 1024;

    /// <summary>上限の設定の最小値。</summary>
    public const long MinimumLimit = 256L * 1024 * 1024;

    /// <summary>ブロックキャッシュの上限の既定値 (ENG-06 の仕様 2。全ドキュメントの合計)。</summary>
    public const long DefaultCacheLimit = 256L * 1024 * 1024;

    /// <summary>1 つのドキュメントに割り当てるキャッシュの最小値 (表示に要る分)。</summary>
    public const long MinimumCachePerDocument = 4L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly List<Document> _documents = [];
    private long _limit = DefaultLimit;
    private long _cacheLimit = DefaultCacheLimit;

    /// <summary>エンジンのメモリ上限 (256 MiB〜搭載メモリの 75%。範囲の確認は設定画面で行う)。</summary>
    public long Limit
    {
        get => Interlocked.Read(ref _limit);
        set => Interlocked.Exchange(ref _limit, Math.Max(MinimumLimit, value));
    }

    /// <summary>全ドキュメントのブロックキャッシュの合計の上限。変えると各ドキュメントの割り当てを決め直す。</summary>
    public long CacheLimit
    {
        get => Interlocked.Read(ref _cacheLimit);
        set
        {
            Interlocked.Exchange(ref _cacheLimit, Math.Max(LowMemoryCacheBytes, value));
            Rebalance();
        }
    }

    public void Register(Document document)
    {
        lock (_lock)
        {
            _documents.Add(document);
        }

        Rebalance();
    }

    public void Unregister(Document document)
    {
        lock (_lock)
        {
            _documents.Remove(document);
        }

        Rebalance();
    }

    /// <summary>全ドキュメントのブロックキャッシュの合計。</summary>
    public long CacheBytes
    {
        get
        {
            lock (_lock)
            {
                return _documents.Sum(d => d.Cache.MemoryBytes);
            }
        }
    }

    /// <summary>全ドキュメントのメモリ使用量の合計 (ENG-08 の仕様 5)。</summary>
    public EngineMemoryUsage TotalUsage
    {
        get
        {
            lock (_lock)
            {
                long cache = 0, add = 0, spilled = 0, nodes = 0;
                foreach (EngineMemoryUsage u in _documents.Where(d => !d.IsDisposed).Select(d => d.MemoryUsage))
                {
                    cache += u.Cache;
                    add += u.AddBufferMemory;
                    spilled += u.AddBufferSpilled;
                    nodes += u.TreeNodes;
                }

                return new EngineMemoryUsage(cache, add, spilled, nodes);
            }
        }
    }

    /// <summary>
    /// 使用量を集計し、上限を超えていれば減らす (ENG-08 の仕様 2。1 秒ごとに呼ぶ)。減らす順は、ブロックキャッシュを 16 MiB まで縮める、
    /// 追加バッファを一時ファイルへ退避する。それでも超える場合は <see cref="MemoryStatus.OverLimit"/>。
    /// </summary>
    public MemoryStatus Enforce()
    {
        lock (_lock)
        {
            List<Document> documents = _documents.Where(d => !d.IsDisposed).ToList();
            long limit = Limit;
            if (Total(documents) <= limit)
            {
                return MemoryStatus.WithinLimit;
            }

            TrimCaches(documents, LowMemoryCacheBytes);
            if (Total(documents) <= limit)
            {
                return MemoryStatus.Reduced;
            }

            // 追加バッファ: キャッシュと木のノードを除いた残りを、メモリに置いている量に応じて分ける。
            long others = documents.Sum(d => d.Cache.MemoryBytes + d.MemoryUsage.TreeNodes);
            long budget = Math.Max(0, limit - others);
            long inMemory = documents.Sum(d => d.AddBuffer.MemoryBytes);
            foreach (Document document in documents)
            {
                long share = inMemory == 0 ? 0 : (long)((double)budget * document.AddBuffer.MemoryBytes / inMemory);
                document.AddBuffer.Trim(share);
            }

            return Total(documents) <= limit ? MemoryStatus.Reduced : MemoryStatus.OverLimit;
        }
    }

    /// <summary>OS のメモリ不足の通知を受けたときに呼ぶ。上限に関係なくブロックキャッシュの合計を 16 MiB まで縮める (ENG-08 の仕様 3)。</summary>
    public void OnLowMemory()
    {
        lock (_lock)
        {
            TrimCaches(_documents.Where(d => !d.IsDisposed).ToList(), LowMemoryCacheBytes);
        }
    }

    /// <summary>キャッシュの上限を開いているドキュメントで等分する (ENG-06 の仕様 2 の上限を全体で守る)。</summary>
    private void Rebalance()
    {
        lock (_lock)
        {
            List<Document> documents = _documents.Where(d => !d.IsDisposed).ToList();
            if (documents.Count == 0)
            {
                return;
            }

            long share = Math.Max(MinimumCachePerDocument, CacheLimit / documents.Count);
            foreach (Document document in documents)
            {
                document.Cache.CapacityBytes = share;
            }
        }
    }

    private static void TrimCaches(List<Document> documents, long total)
    {
        if (documents.Count == 0)
        {
            return;
        }

        long share = total / documents.Count;
        foreach (Document document in documents)
        {
            document.Cache.Trim(share);
        }
    }

    private static long Total(List<Document> documents) => documents.Sum(d => d.MemoryUsage.TotalInMemory);
}

/// <summary>
/// OS のメモリ不足の通知 (<c>CreateMemoryResourceNotification</c>) の状態を 1 秒ごとに調べ、立ったら
/// <see cref="EngineMemory.OnLowMemory"/> を呼ぶ (ENG-08 の仕様 3。通知はメモリ不足の間ずっと立つため、待機ではなく状態を調べる)。
/// あわせて 1 秒ごとに <see cref="EngineMemory.Enforce"/> を呼び、上限を超えたままなら
/// <see cref="OverLimit"/> を通知する (スレッドプールから呼ばれる)。
/// </summary>
public sealed class MemoryMonitor : IDisposable
{
    private readonly EngineMemory _memory;
    private readonly ITimer _timer;
    private readonly SafeWaitHandle? _notification;
    private bool _wasLow;
    private MemoryStatus _lastStatus;

    public MemoryMonitor(EngineMemory memory, TimeSpan? interval = null, TimeProvider? time = null)
    {
        _memory = memory;
        TimeSpan period = interval ?? TimeSpan.FromSeconds(1);
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Tick(), null, period, period);
        if (OperatingSystem.IsWindows())
        {
            IntPtr handle = CreateMemoryResourceNotification(LowMemoryResourceNotification);
            if (handle != IntPtr.Zero)
            {
                _notification = new SafeWaitHandle(handle, ownsHandle: true);
            }
        }
    }

    /// <summary>上限を超えた状態になった (UI は InfoBar を出す)。</summary>
    public event EventHandler? OverLimit;

    /// <summary>OS のメモリ不足の通知を受けた (キャッシュは縮めた後)。</summary>
    public event EventHandler? LowMemory;

    /// <summary>メモリ不足の通知を受けたものとして扱う (テスト用のビルドの異常の再現。テスト方針 7.2)。</summary>
    public void SimulateLowMemory()
    {
        _memory.OnLowMemory();
        LowMemory?.Invoke(this, EventArgs.Empty);
    }

    private void CheckLowMemory()
    {
        // 立った時に 1 回だけ処理する。
        bool low = _notification is not null && QueryMemoryResourceNotification(_notification, out bool state) && state;
        if (low && !_wasLow)
        {
            SimulateLowMemory();
        }

        _wasLow = low;
    }

    private void Tick()
    {
        MemoryStatus status = _memory.Enforce();
        if (status == MemoryStatus.OverLimit && _lastStatus != MemoryStatus.OverLimit)
        {
            OverLimit?.Invoke(this, EventArgs.Empty);
        }

        _lastStatus = status;
        if (_notification is not null)
        {
            CheckLowMemory();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _notification?.Dispose();
    }

    private const int LowMemoryResourceNotification = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryMemoryResourceNotification(SafeWaitHandle handle, out bool resourceState);
}
