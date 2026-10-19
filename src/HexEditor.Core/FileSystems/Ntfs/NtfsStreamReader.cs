using HexEditor.Core.Sources;

namespace HexEditor.Core.FileSystems.Ntfs;

/// <summary>
/// 非常駐の属性 (ストリーム) の論理的な内容 (FOR-09 の仕様 6、FOR-10 の仕様 7)。データランをつなぎ、スパースの区間と初期化済みサイズより後ろは 0、
/// 圧縮 (LZNT1) された圧縮単位は展開して返す。$ATTRIBUTE_LIST で複数のレコードに分かれた属性は、ランを VCN の順につないで 1 つにする。
/// </summary>
public sealed class NtfsStreamReader : IVolumeReader
{
    private readonly IVolumeReader _volume;
    private readonly NtfsRun[] _runs;
    private readonly long _clusterSize;
    private readonly long _initialized;
    private readonly int _unitClusters;
    private readonly object _gate = new();
    private long _cachedUnit = -1;
    private byte[] _unitData = [];

    public NtfsStreamReader(IVolumeReader volume, IReadOnlyList<NtfsRun> runs, long clusterSize, long length, long initializedSize, int compressionUnit)
    {
        _volume = volume;
        _runs = [.. runs.OrderBy(r => r.Vcn)];
        _clusterSize = clusterSize;
        Length = Math.Max(0, length);
        _initialized = Math.Clamp(initializedSize, 0, Length);
        _unitClusters = compressionUnit > 0 ? 1 << compressionUnit : 0;
    }

    public long Length { get; }

    public IReadOnlyList<NtfsRun> Runs => _runs;

    public bool IsCompressed => _unitClusters > 0;

    public ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
        {
            return new ReadResult(0);
        }

        int total = (int)Math.Min(buffer.Length, Length - offset);
        Span<byte> dest = buffer[..total];
        dest.Clear();
        List<UnreadableRange>? bad = null;
        if (_unitClusters > 0)
        {
            long unitBytes = _unitClusters * _clusterSize;
            int done = 0;
            while (done < total)
            {
                long at = offset + done;
                long unit = at / unitBytes;
                byte[] data = Unit(unit);
                int within = (int)(at - unit * unitBytes);
                int n = (int)Math.Min(total - done, unitBytes - within);
                if (within < data.Length)
                {
                    data.AsSpan(within, Math.Min(n, data.Length - within)).CopyTo(dest.Slice(done, n));
                }

                done += n;
            }
        }
        else
        {
            long readable = Math.Min(offset + total, _initialized);
            foreach (NtfsRun run in RunsIn(offset, readable))
            {
                long runStart = run.Vcn * _clusterSize;
                long runEnd = runStart + run.Length * _clusterSize;
                long from = Math.Max(offset, runStart), to = Math.Min(readable, runEnd);
                if (to <= from || run.Lcn is not long lcn)
                {
                    continue;
                }

                long volumeOffset = lcn * _clusterSize + (from - runStart);
                ReadResult r = _volume.Read(volumeOffset, dest.Slice((int)(from - offset), (int)(to - from)));
                foreach (UnreadableRange u in r.Unreadable)
                {
                    (bad ??= []).Add(u with { Offset = u.Offset - volumeOffset + from });
                }
            }
        }

        return new ReadResult(total, bad);
    }

    /// <summary>[from, to) と重なるラン。</summary>
    private IEnumerable<NtfsRun> RunsIn(long from, long to)
    {
        long firstVcn = from / _clusterSize;
        int lo = 0, hi = _runs.Length - 1, start = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_runs[mid].Vcn <= firstVcn)
            {
                start = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (int i = start; i < _runs.Length; i++)
        {
            if (_runs[i].Vcn * _clusterSize >= to)
            {
                break;
            }

            yield return _runs[i];
        }
    }

    /// <summary>論理オフセットのボリューム上の位置 (圧縮・スパースなら null)。</summary>
    public long? VolumeOffsetOf(long logical)
    {
        if (_unitClusters > 0)
        {
            return null;
        }

        foreach (NtfsRun run in RunsIn(logical, logical + 1))
        {
            long runStart = run.Vcn * _clusterSize;
            if (logical >= runStart && logical < runStart + run.Length * _clusterSize && run.Lcn is long lcn)
            {
                return lcn * _clusterSize + (logical - runStart);
            }
        }

        return null;
    }

    /// <summary>圧縮単位 1 つの展開した内容 (データのクラスタが単位の全体なら圧縮されていない)。</summary>
    private byte[] Unit(long unit)
    {
        lock (_gate)
        {
            if (_cachedUnit == unit)
            {
                return _unitData;
            }
        }

        long unitBytes = _unitClusters * _clusterSize;
        long firstVcn = unit * _unitClusters, endVcn = firstVcn + _unitClusters;
        var pieces = new List<(long Lcn, long Count)>();
        long dataClusters = 0;
        foreach (NtfsRun run in RunsIn(firstVcn * _clusterSize, endVcn * _clusterSize))
        {
            long from = Math.Max(firstVcn, run.Vcn), to = Math.Min(endVcn, run.Vcn + run.Length);
            if (to <= from || run.Lcn is not long lcn)
            {
                continue;
            }

            pieces.Add((lcn + (from - run.Vcn), to - from));
            dataClusters += to - from;
        }

        byte[] result = new byte[unitBytes];
        if (dataClusters > 0)
        {
            byte[] stored = new byte[dataClusters * _clusterSize];
            long at = 0;
            foreach ((long lcn, long count) in pieces)
            {
                _volume.Read(lcn * _clusterSize, stored.AsSpan((int)at, (int)(count * _clusterSize)));
                at += count * _clusterSize;
            }

            if (dataClusters >= _unitClusters)
            {
                stored.AsSpan(0, (int)unitBytes).CopyTo(result);
            }
            else
            {
                NtfsFormat.DecompressLznt1(stored, result);
            }
        }

        lock (_gate)
        {
            _cachedUnit = unit;
            _unitData = result;
        }

        return result;
    }
}
