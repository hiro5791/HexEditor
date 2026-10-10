using HexEditor.Core.Operations;

namespace HexEditor.Core.Processes;

/// <summary>
/// スナップショットの作成と一時ファイルの管理 (ENG-35 の仕様 1、ANA-09 の仕様 1・9)。「スナップショットを作成」の入口から作ったものは
/// 一時フォルダに置き、アプリの終了時に削除する。「名前を付けて保存」したものは管理の対象から外す (残す)。
/// </summary>
public sealed class SnapshotManager(string tempDirectory) : IDisposable
{
    private readonly object _lock = new();
    private readonly List<string> _temporary = [];

    /// <summary>一時フォルダに作ったスナップショットのパス (診断・テスト用)。</summary>
    public IReadOnlyList<string> TemporaryFiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _temporary];
            }
        }
    }

    /// <summary>
    /// プロセスメモリのスナップショットを一時フォルダに作る (ANA-09 の仕様 1)。<paramref name="pauseTarget"/> は未実装のため無視する
    /// (ENG-35 の仕様 2 の一時停止はフェーズ 4)。作ったファイルは <see cref="Dispose"/> で削除する。
    /// </summary>
    public SnapshotByteSource CreateTemporary(ProcessMemoryByteSource source, string? name = null, SnapshotScope scope = SnapshotScope.AllReadable,
        IReadOnlyCollection<long>? selected = null, LongRunningOperation? operation = null)
    {
        Directory.CreateDirectory(tempDirectory);
        string path = Path.Combine(tempDirectory, $"snap-{Guid.NewGuid():N}{HexSnapshot.Extension}");
        SnapshotMetadata metadata = HexSnapshot.Capture(source, path, scope, selected, operation);
        lock (_lock)
        {
            _temporary.Add(path);
        }

        return new SnapshotByteSource(path, metadata, name ?? $"{metadata.ProcessName} {metadata.CapturedUtc.ToLocalTime():HH:mm:ss}");
    }

    /// <summary>一時スナップショットを管理から外す (「名前を付けて保存」したとき。ANA-09 の仕様 9)。</summary>
    public void Keep(string path)
    {
        lock (_lock)
        {
            _temporary.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>一時スナップショットを削除する (アプリの終了時。ANA-09 の仕様 9)。</summary>
    public void Dispose()
    {
        string[] files;
        lock (_lock)
        {
            files = [.. _temporary];
            _temporary.Clear();
        }

        foreach (string path in files)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
