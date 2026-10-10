using Microsoft.Win32.SafeHandles;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Processes;

/// <summary>
/// 保存した `.hexsnap` を開いたドキュメント (ENG-35 の仕様 4): アドレス空間全体の読み取り専用で、メモリマップのパネル (ENG-33) も使える。
/// オフセット = 仮想アドレス。書き戻す先がないため読み取り専用 (<see cref="ReadOnlyReason.NoWriteTarget"/>)。
/// </summary>
public sealed class SnapshotByteSource : ByteSourceBase, IRegionMapSource
{
    private readonly SafeFileHandle _handle;
    private readonly SnapshotMetadata _metadata;
    private readonly List<SourceRegion> _map;
    private readonly string _identity;

    public SnapshotByteSource(string path, SnapshotMetadata metadata, string displayName)
    {
        Path = path;
        _metadata = metadata;
        DisplayName = displayName;
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        _identity = "snapshot:" + System.IO.Path.GetFullPath(path).ToUpperInvariant();
        Length = metadata.AddressLimit;
        _map = BuildMap(metadata);
    }

    /// <summary>`.hexsnap` を開く。</summary>
    public static SnapshotByteSource Open(string path, string? displayName = null)
    {
        SnapshotMetadata metadata;
        using (var stream = File.OpenRead(path))
        {
            metadata = HexSnapshot.ReadMetadata(stream);
        }

        return new SnapshotByteSource(path, metadata, displayName ?? $"{metadata.ProcessName} {metadata.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    public string Path { get; }

    public SnapshotMetadata Metadata => _metadata;

    public override string DisplayName { get; }

    public override string Identity => _identity;

    public override long Length { get; }

    public override SourceCapabilities Capabilities => SourceCapabilities.HasGaps;

    public IReadOnlyList<SourceRegion> Regions => _map;

    public IReadOnlyList<ProcessModule> Modules => _metadata.Modules;

    public event EventHandler? RegionsChanged
    {
        add { }
        remove { }
    }

    private static List<SourceRegion> BuildMap(SnapshotMetadata metadata)
    {
        var map = new List<SourceRegion>();
        long cursor = 0;
        foreach (SnapshotRegion region in metadata.Regions.OrderBy(r => r.BaseAddress))
        {
            if (region.BaseAddress > cursor)
            {
                map.Add(new SourceRegion(cursor, region.BaseAddress - cursor, RegionAccess.Unallocated));
            }

            map.Add(new SourceRegion(region.BaseAddress, region.Size, RegionAccess.Readable, region.ModuleName));
            cursor = region.BaseAddress + region.Size;
        }

        if (cursor < metadata.AddressLimit)
        {
            map.Add(new SourceRegion(cursor, metadata.AddressLimit - cursor, RegionAccess.Unallocated));
        }

        return map;
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int length = ClampToLength(offset, buffer.Length);
        if (length == 0)
        {
            return new ReadResult(0);
        }

        Span<byte> target = buffer[..length];
        List<UnreadableRange>? bad = null;
        long end = offset + length;
        for (long at = offset; at < end;)
        {
            SnapshotRegion? region = RegionAt(at);
            long regionEnd = region is null ? NextRegionStart(at, end) : Math.Min(region.BaseAddress + region.Size, end);
            int n = (int)(regionEnd - at);
            Span<byte> dest = target.Slice((int)(at - offset), n);
            if (region is null)
            {
                dest.Clear();
                (bad ??= []).Add(new UnreadableRange(at, n, UnreadableReason.Unallocated));
            }
            else
            {
                long fileAt = region.FileOffset + (at - region.BaseAddress);
                int read = RandomAccess.Read(_handle, dest, fileAt);
                if (read < n)
                {
                    // 読めなかったページ (スナップショットに含めなかった部分)。
                    dest[read..].Clear();
                    (bad ??= []).Add(new UnreadableRange(at + read, n - read, UnreadableReason.Unallocated));
                }

                // 作成時に読めなかったページ (メタデータに記録。データの位置は 0 のまま)。比較では「読み込み不可」になり、差分に数えない
                // (ANA-09 の仕様 8)。
                foreach (SnapshotGap gap in _metadata.UnreadablePages)
                {
                    long gs = Math.Max(gap.BaseAddress, at);
                    long ge = Math.Min(gap.BaseAddress + gap.Size, regionEnd);
                    if (gs < ge)
                    {
                        target.Slice((int)(gs - offset), (int)(ge - gs)).Clear();
                        (bad ??= []).Add(new UnreadableRange(gs, ge - gs, UnreadableReason.IoError));
                    }
                }
            }

            at = regionEnd;
        }

        return new ReadResult(length, bad);
    }

    private SnapshotRegion? RegionAt(long address)
    {
        foreach (SnapshotRegion region in _metadata.Regions)
        {
            if (address >= region.BaseAddress && address < region.BaseAddress + region.Size)
            {
                return region;
            }
        }

        return null;
    }

    private long NextRegionStart(long address, long end)
    {
        long next = end;
        foreach (SnapshotRegion region in _metadata.Regions)
        {
            if (region.BaseAddress > address && region.BaseAddress < next)
            {
                next = region.BaseAddress;
            }
        }

        return next;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }
    }
}
