using System.Buffers;
using HexEditor.Core.Devices;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Wiping;

/// <summary>
/// 消去の書き込み先 (データエンジンを通さずに直接書き換える。FOR-30 の仕様 6)。位置は対象の先頭からのバイト位置。
/// 書き込みはセクタ境界にそろっていなくてもよい (デバイスでは端のセクタを読み込んで合成してから書く。共通の前提「ディスクへのアクセス」の 2)。
/// </summary>
public interface IWipeTarget : IDisposable
{
    /// <summary>表示名 (確認ダイアログ・監査ログ)。</summary>
    string Name { get; }

    long Length { get; }

    /// <summary>論理セクタサイズ (ファイルは 1)。</summary>
    int SectorSize { get; }

    /// <exception cref="IOException">書き込みに失敗した (デバイスは <see cref="DeviceException"/>)。</exception>
    void Write(long offset, ReadOnlySpan<byte> data);

    /// <summary>読み直す (検証)。すべて読めなければ <see cref="IOException"/>。</summary>
    void Read(long offset, Span<byte> buffer);

    /// <summary>ディスクへの反映 (<c>FlushFileBuffers</c>)。</summary>
    void Flush();
}

/// <summary>
/// ディスク・ボリュームの書き込み先 (<see cref="DeviceByteSource"/> の位置で書く。読み書きのハンドルで開いていること)。
/// セクタ境界にそろっていない範囲は、端のセクタを読み込んで合成してから書く。
/// </summary>
public sealed class DeviceWipeTarget(DeviceByteSource source, long baseOffset = 0, long? length = null) : IWipeTarget
{
    public DeviceByteSource Source => source;

    public string Name => source.DisplayName;

    public long Length { get; } = length ?? source.Length - baseOffset;

    public int SectorSize => source.LogicalSectorSize;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        Check(offset, data.Length);
        int sector = SectorSize;
        long at = baseOffset + offset;
        long from = at / sector * sector;
        long to = (at + data.Length + sector - 1) / sector * sector;
        if (from == at && to == at + data.Length)
        {
            source.Write(at, data);
            return;
        }

        // 端のセクタを読み込んで合成する (読み込み → 合成 → 書き込み)。
        int span = (int)(to - from);
        byte[] rented = ArrayPool<byte>.Shared.Rent(span);
        try
        {
            Span<byte> buffer = rented.AsSpan(0, span);
            ReadResult read = source.Read(from, buffer);
            if (!read.IsComplete || read.BytesReturned != span)
            {
                throw new IOException($"The sectors around offset 0x{offset:X} could not be read before writing.");
            }

            data.CopyTo(buffer[(int)(at - from)..]);
            source.Write(from, buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public void Read(long offset, Span<byte> buffer)
    {
        Check(offset, buffer.Length);
        ReadResult read = source.Read(baseOffset + offset, buffer);
        if (!read.IsComplete || read.BytesReturned != buffer.Length)
        {
            throw new IOException($"The range at offset 0x{offset:X} could not be read.");
        }
    }

    public void Flush() => source.Flush();

    private void Check(long offset, int count)
    {
        if (offset < 0 || offset + count > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    /// <summary>ドキュメントのデータソースを閉じない (ドキュメントが持ち続ける)。</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// ファイル (ディスクイメージ・通常のファイル) の書き込み先。ドキュメントのハンドルとは別に、読み書きの共有を許して開く
/// (ドキュメントは読み込みと書き込みを共有するハンドルで開いている)。<paramref name="baseOffset"/> は範囲を指定して開いたときの開始位置。
/// </summary>
public sealed class FileWipeTarget : IWipeTarget
{
    private readonly FileStream _stream;
    private readonly long _base;

    public FileWipeTarget(string path, long baseOffset = 0, long? length = null)
    {
        Path = path;
        _base = baseOffset;
        _stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.WriteThrough);
        Length = length ?? _stream.Length - baseOffset;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    public long Length { get; }

    public int SectorSize => 1;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        RandomAccess.Write(_stream.SafeFileHandle, data, _base + offset);
    }

    public void Read(long offset, Span<byte> buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = RandomAccess.Read(_stream.SafeFileHandle, buffer[done..], _base + offset + done);
            if (n <= 0)
            {
                throw new IOException($"The range at offset 0x{offset:X} could not be read.");
            }

            done += n;
        }
    }

    public void Flush() => _stream.Flush(flushToDisk: true);

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// 消去の前に、書き込む範囲に重なるボリュームをロックする (共通の前提「ディスクへのアクセス」の 3、ENG-30 の仕様 2・3)。
/// ボリュームのドキュメントでは開いているハンドルそのものでロックし (ロックしたハンドルからしか書けない)、物理ディスクでは重なるボリュームを
/// 別のハンドルでロックする。ロックできなければ書き込まない (<see cref="VolumeLockException"/>)。
/// </summary>
public sealed class WipeVolumeLocks : IDisposable
{
    private readonly List<(IDeviceHandle Handle, bool Owned)> _locks = [];

    private WipeVolumeLocks()
    {
    }

    /// <summary>ロックしたボリュームのパス。</summary>
    public IReadOnlyList<string> Locked => [.. _locks.Select(l => l.Handle.Path)];

    /// <summary>
    /// 書き込む範囲 (デバイスのドキュメント上の位置) に重なるボリュームを求める。<paramref name="blocked"/> は Windows が使用中でロックできないもの。
    /// </summary>
    public static IReadOnlyList<VolumeDeviceInfo> Overlapping(DeviceByteSource source, DeviceCatalog catalog, IEnumerable<(long Offset, long Length)> ranges,
        out IReadOnlyList<VolumeDeviceInfo> blocked)
    {
        var result = new List<VolumeDeviceInfo>();
        var inUse = new List<VolumeDeviceInfo>();
        if (source.Info.Disk is { } disk)
        {
            foreach ((long offset, long length) in ranges)
            {
                foreach (VolumeDeviceInfo volume in catalog.VolumesOverlapping(disk.Number, source.Info.RangeStart + offset, length))
                {
                    (volume.IsInUseByWindows ? inUse : result).Add(volume);
                }
            }
        }
        else if (source.Info.Volume is { } own)
        {
            (own.IsInUseByWindows ? inUse : result).Add(own);
        }

        blocked = [.. inUse.DistinctBy(v => v.Path, StringComparer.OrdinalIgnoreCase)];
        return [.. result.DistinctBy(v => v.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// ロックする。開いているファイルがあってロックできないときは <paramref name="confirmDismount"/> に確かめ、承認されたらディスマウントしてから
    /// ロックし直す。失敗したら取ったロックを解除して <see cref="VolumeLockException"/>。
    /// </summary>
    public static WipeVolumeLocks Acquire(DeviceByteSource source, IReadOnlyList<VolumeDeviceInfo> volumes, Func<VolumeDeviceInfo, bool>? confirmDismount = null)
    {
        var locks = new WipeVolumeLocks();
        try
        {
            foreach (VolumeDeviceInfo volume in volumes)
            {
                bool own = string.Equals(volume.Path, source.Path, StringComparison.OrdinalIgnoreCase);
                IDeviceHandle handle = own ? source.Handle : source.Access.Open(volume.Path, writable: true);
                bool ok = handle.LockVolume() == 0
                    || (confirmDismount?.Invoke(volume) == true && handle.DismountVolume() == 0 && handle.LockVolume() == 0);
                if (!ok)
                {
                    if (!own)
                    {
                        handle.Dispose();
                    }

                    throw new VolumeLockException(volume.Name);
                }

                locks._locks.Add((handle, !own));
            }
        }
        catch
        {
            locks.Dispose();
            throw;
        }

        return locks;
    }

    public void Dispose()
    {
        foreach ((IDeviceHandle handle, bool owned) in _locks)
        {
            try
            {
                handle.UnlockVolume();
            }
            catch (Exception ex) when (ex is IOException or DeviceException)
            {
            }

            if (owned)
            {
                handle.Dispose();
            }
        }

        _locks.Clear();
    }
}
