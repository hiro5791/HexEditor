using HexEditor.Core.Devices;
using HexEditor.Core.Saving;

namespace HexEditor.Core.Processes;

/// <summary>スナップショット・ミニダンプの取得の指定 (ENG-35 の仕様 1・2)。</summary>
public sealed record ProcessCaptureOptions
{
    /// <summary>対象 (既定は読めるすべての領域)。</summary>
    public SnapshotScope Scope { get; init; } = SnapshotScope.AllReadable;

    /// <summary>「選んだ領域・モジュール」の範囲 (仮想アドレス)。範囲に重なる領域を含める。</summary>
    public IReadOnlyList<(long Start, long Length)> SelectedRanges { get; init; } = [];

    /// <summary>取得中は対象のプロセスを一時停止する (既定オフ。ENG-35 の仕様 2)。</summary>
    public bool PauseTarget { get; init; }

    /// <summary>保存先の空き容量の確認 (ENG-25)。null なら確認しない。</summary>
    public IVolumeInfoProvider? Volumes { get; init; }

    /// <summary>取得の前に領域の一覧を取り直す (開いた後に確保・解放された領域を反映する)。</summary>
    public bool RefreshRegions { get; init; } = true;
}

/// <summary>取得の結果のうち、完了時に利用者に示すもの (ENG-35 の「エラー」)。</summary>
public sealed record ProcessCaptureSummary
{
    /// <summary>一時停止を求めたが停止できなかった (警告して続けた。ENG-35 の仕様 2)。</summary>
    public bool PauseFailed { get; init; }

    /// <summary>一部を読めなかった領域の数。</summary>
    public int UnreadableRegions { get; init; }

    /// <summary>読めなかったバイト数の合計。</summary>
    public long UnreadableBytes { get; init; }

    /// <summary>書き出した領域の数。</summary>
    public int Regions { get; init; }

    /// <summary>書き出したデータのバイト数 (読めなかった部分を除く)。</summary>
    public long CapturedBytes { get; init; }
}

/// <summary>取得の途中で対象のプロセスが終了した (ENG-35 の「エラー」: 中止し、途中のファイルを削除する)。</summary>
public sealed class ProcessExitedDuringCaptureException() : IOException("The target process exited during the capture.");

/// <summary>
/// スナップショット (<see cref="HexSnapshot"/>) とミニダンプ (<see cref="MinidumpWriter"/>) に共通の取得処理: 対象の領域の選択、一時停止、
/// 空き容量の確認、ページ単位の読み込み (読めなかったページの記録)、途中のファイルの削除。
/// </summary>
public static class ProcessCapture
{
    /// <summary>1 回に読む大きさ。</summary>
    public const int ChunkSize = ProcessMemoryByteSource.MaxTransfer;

    private const int PageSize = ProcessMemoryByteSource.PageSize;

    /// <summary>対象の領域 (ENG-35 の仕様 1)。コミット済みで読める領域だけ。アドレスの昇順。</summary>
    public static IReadOnlyList<MemoryRegion> SelectRegions(IReadOnlyList<MemoryRegion> regions, ProcessCaptureOptions options) =>
    [
        .. regions.Where(r => r.State == RegionState.Commit && r.IsReadable && options.Scope switch
        {
            SnapshotScope.WritableOnly => r.IsWritable,
            SnapshotScope.Selected => options.SelectedRanges.Any(s => r.BaseAddress < s.Start + Math.Max(1, s.Length) && s.Start < r.End),
            _ => true,
        }).OrderBy(r => r.BaseAddress),
    ];

    /// <summary>
    /// 保存先の空き容量を確かめる (ENG-25)。足りなければ <see cref="InsufficientSpaceException"/>。容量を取得できない場合は確かめない。
    /// </summary>
    public static void CheckSpace(string path, long required, IVolumeInfoProvider? volumes)
    {
        if (volumes is null || Path.GetDirectoryName(Path.GetFullPath(path)) is not { } folder)
        {
            return;
        }

        VolumeInfo? volume = volumes.GetVolume(folder);
        if (volume?.AvailableFreeSpace is long available && available < required)
        {
            throw new InsufficientSpaceException(volume.Name, required, available);
        }
    }

    /// <summary>
    /// 取得の間だけ対象のプロセスを停止する (ENG-35 の仕様 2)。停止できない場合 (権限・補助プロセスの経路で未対応) は
    /// <see cref="PauseScope.Failed"/> を立てて続ける。破棄で再開する。
    /// </summary>
    public static PauseScope Pause(IProcessMemory memory, bool pause)
    {
        if (!pause)
        {
            return new PauseScope(null, failed: false);
        }

        if (memory is not IProcessSuspend suspend)
        {
            return new PauseScope(null, failed: true);
        }

        int error = suspend.Suspend();
        return error == 0 ? new PauseScope(suspend, failed: false) : new PauseScope(null, failed: true);
    }

    /// <summary>一時停止の範囲 (破棄で再開する)。</summary>
    public sealed class PauseScope : IDisposable
    {
        private IProcessSuspend? _suspended;

        internal PauseScope(IProcessSuspend? suspended, bool failed)
        {
            _suspended = suspended;
            Failed = failed;
        }

        /// <summary>停止を求めたが、停止できなかった。</summary>
        public bool Failed { get; }

        /// <summary>停止している。</summary>
        public bool IsPaused => _suspended is not null;

        public void Dispose()
        {
            _suspended?.Resume();
            _suspended = null;
        }
    }

    /// <summary>
    /// [address, address + buffer.Length) を読む。読めなかったページは 0 にして <paramref name="gaps"/> に加える (隣と続く場合はまとめる)。
    /// 対象のプロセスが終了した場合は <see cref="ProcessExitedDuringCaptureException"/>。権限不足で読めなかったページがあれば
    /// <paramref name="denied"/> を立てる。
    /// </summary>
    public static void ReadChunk(IProcessMemory memory, long address, Span<byte> buffer, List<SnapshotGap> gaps, ref bool denied)
    {
        for (int pos = 0; pos < buffer.Length;)
        {
            int error = memory.Read(address + pos, buffer[pos..], out int read);
            read = Math.Clamp(read, 0, buffer.Length - pos);
            if (error == 0 && read == buffer.Length - pos)
            {
                return;
            }

            pos += read;
            if (memory.HasExited || Win32Errors.IsDisconnect(error))
            {
                throw new ProcessExitedDuringCaptureException();
            }

            denied |= error == Win32Errors.AccessDenied;

            // 読めなかったページを飛ばし、次のページから続ける。
            long failAt = address + pos;
            int skip = (int)Math.Min(buffer.Length - pos, PageSize - (failAt % PageSize));
            buffer.Slice(pos, skip).Clear();
            AddGap(gaps, failAt, skip);
            pos += skip;
        }
    }

    private static void AddGap(List<SnapshotGap> gaps, long address, long size)
    {
        if (gaps.Count > 0 && gaps[^1].BaseAddress + gaps[^1].Size == address)
        {
            gaps[^1] = gaps[^1] with { Size = gaps[^1].Size + size };
        }
        else
        {
            gaps.Add(new SnapshotGap(address, size));
        }
    }

    /// <summary>読めなかった範囲を、領域ごとの件数と合計にまとめる (ENG-35 の「エラー」の「N 個の領域 (合計 M バイト)」)。</summary>
    public static (int Regions, long Bytes) Summarize(IReadOnlyList<MemoryRegion> regions, IReadOnlyList<SnapshotGap> gaps)
    {
        int count = regions.Count(r => gaps.Any(g => g.BaseAddress < r.End && r.BaseAddress < g.BaseAddress + g.Size));
        return (count, gaps.Sum(g => g.Size));
    }

    /// <summary>保存先と同じフォルダの一時ファイルのパス (隠し属性は付けない。完了したら名前を変えて確定する)。</summary>
    public static string TempPathFor(string path)
    {
        string full = Path.GetFullPath(path);
        return Path.Combine(Path.GetDirectoryName(full)!, $"~{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
    }

    /// <summary>一時ファイルを確定する (既存のファイルは置き換える)。</summary>
    public static void Commit(string temp, string path) => File.Move(temp, Path.GetFullPath(path), overwrite: true);

    /// <summary>途中のファイルを削除する (キャンセル・失敗の後。ENG-35 の「巨大ファイル・長時間処理」)。</summary>
    public static void TryDelete(string path)
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
