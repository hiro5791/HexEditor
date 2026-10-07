namespace HexEditor.Core.Engine;

/// <summary>
/// アプリ全体のエンジンのメモリ使用量を管理する (ENG-08)。上限はアプリ全体 (全ドキュメントの合計) に対するもの。
/// </summary>
public sealed class EngineMemory
{
    /// <summary>メモリ不足のときにブロックキャッシュを縮める大きさ (全ドキュメントの合計)。</summary>
    public const long LowMemoryCacheBytes = 16L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly List<Document> _documents = [];

    public void Register(Document document)
    {
        lock (_lock)
        {
            _documents.Add(document);
        }
    }

    public void Unregister(Document document)
    {
        lock (_lock)
        {
            _documents.Remove(document);
        }
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

    /// <summary>OS のメモリ不足の通知を受けたときに呼ぶ。ブロックキャッシュの合計を 16 MiB まで縮める (ENG-08 の仕様 3)。</summary>
    public void OnLowMemory()
    {
        lock (_lock)
        {
            if (_documents.Count == 0)
            {
                return;
            }

            long share = LowMemoryCacheBytes / _documents.Count;
            foreach (Document document in _documents)
            {
                document.Cache.Trim(share);
            }
        }
    }
}
