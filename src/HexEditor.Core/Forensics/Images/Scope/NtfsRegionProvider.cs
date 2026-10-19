using System.Buffers.Binary;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Scope;

/// <summary>
/// NTFS の割り当て済みの領域 ($Bitmap) とメタデータ (ブートセクタ、$MFT、$MFTMirr、$LogFile、$Bitmap、$UpCase などのシステムファイルと、
/// すべての MFT レコードの $ATTRIBUTE_LIST・$INDEX_ALLOCATION・$BITMAP の非常駐のデータ) の範囲 (FOR-23)。
/// FOR-10 の詳しい解析とは別の、範囲を求めるだけの最小限の読み取り。MFT レコードのフィックスアップを確かめ、壊れたレコードは飛ばす。
/// </summary>
public sealed class NtfsRegionProvider : IFileSystemRegionProvider
{
    private const int MaxRuns = 1 << 16;

    public string Name => "NTFS";

    public bool Probe(ReadOnlySpan<byte> head) => head.Length >= 512 && head.Slice(3, 8).SequenceEqual("NTFS    "u8);

    public FileSystemRegions? Analyze(IByteSource volume, CancellationToken cancellationToken)
    {
        byte[] boot = new byte[512];
        volume.Read(0, boot);
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int spcRaw = boot[0x0D];
        long spc = spcRaw > 0x80 ? 1L << (256 - spcRaw) : spcRaw;
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x28));
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        sbyte perRecord = (sbyte)boot[0x40];
        if (bps is not (512 or 1024 or 2048 or 4096) || spc is < 1 or > 65536 || (spc & (spc - 1)) != 0 || totalSectors <= 0 || mftLcn <= 0)
        {
            return null;
        }

        long cluster = bps * spc;
        long recordSize = perRecord < 0 ? 1L << -perRecord : perRecord * cluster;
        long totalClusters = Math.Min(totalSectors * bps, volume.Length) / cluster;
        if (recordSize is < 256 or > 65536 || mftLcn >= totalClusters)
        {
            return null;
        }

        var reader = new Reader(volume, cluster, recordSize, bps, totalClusters);
        byte[]? mftRecord = reader.ReadAt(mftLcn * cluster);
        if (mftRecord is null)
        {
            return null;
        }

        List<(long Lcn, long Count)>? mftRuns = Attribute(mftRecord, 0x80, unnamedOnly: true)?.Runs;
        if (mftRuns is null || mftRuns.Count == 0)
        {
            return null;
        }

        reader.MftRuns = mftRuns;
        long records = mftRuns.Sum(r => r.Count) * cluster / recordSize;
        var metadata = new List<(long, long)>
        {
            (0, Math.Min(cluster * Math.Max(1, 8192 / cluster), volume.Length)),          // ブートセクタ ($Boot)
            (Math.Min(totalSectors * bps, volume.Length) - bps, bps),                      // 末尾のバックアップのブートセクタ
        };
        metadata.AddRange(reader.ToBytes(mftRuns));

        // $Bitmap (レコード 6) の内容から割り当て済みのクラスタ。
        List<(long Lcn, long Count)>? bitmapRuns = reader.Record(6) is { } bitmapRecord ? Attribute(bitmapRecord, 0x80, unnamedOnly: true)?.Runs : null;
        List<(long, long)>? allocated = null;
        if (bitmapRuns is not null)
        {
            var runs = new List<(long Cluster, long Count)>();
            long bit = 0;
            byte[] buffer = new byte[1 << 20];
            foreach ((long lcn, long count) in bitmapRuns)
            {
                for (long done = 0; done < count * cluster && bit < totalClusters; done += buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, Math.Min(count * cluster - done, (totalClusters - bit + 7) / 8));
                    if (n <= 0)
                    {
                        break;
                    }

                    volume.Read(lcn * cluster + done, buffer.AsSpan(0, n));
                    FileSystemRegionProviders.BitmapRuns(buffer.AsSpan(0, n), bit, totalClusters, runs);
                    bit += n * 8L;
                }
            }

            allocated = [.. runs.Select(r => (r.Cluster * cluster, r.Count * cluster))];
        }

        // MFT のすべてのレコード: システムファイル (0〜15。$BadClus を除く) の非常駐の属性と、ディレクトリなどの索引・属性リスト。
        byte[] chunk = new byte[(int)Math.Max(recordSize, 1 << 20) / recordSize * recordSize];
        for (long first = 0; first < records; first += chunk.Length / recordSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(chunk.Length / recordSize, records - first);
            reader.ReadRecords(first, count, chunk);
            for (int i = 0; i < count; i++)
            {
                long index = first + i;
                byte[] record = chunk.AsSpan((int)(i * recordSize), (int)recordSize).ToArray();
                if (!Fixup(record, bps) || BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x454C4946 /* FILE */
                    || (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x16)) & 1) == 0)
                {
                    continue;
                }

                foreach (AttrEntry a in Attributes(record))
                {
                    if (a.Runs is null)
                    {
                        continue;
                    }

                    bool system = index < 16 && index != 8;
                    if (system || a.Type is 0x20 or 0xA0 or 0xB0)
                    {
                        metadata.AddRange(reader.ToBytes(a.Runs));
                    }
                }
            }
        }

        List<(long, long)> meta = FileSystemRegionProviders.Merge(metadata.Where(r => r.Item1 >= 0 && r.Item1 < volume.Length)
            .Select(r => (r.Item1, Math.Min(r.Item2, volume.Length - r.Item1))));
        return new FileSystemRegions("NTFS", allocated is null ? meta : FileSystemRegionProviders.Merge(allocated.Concat(meta)), meta);
    }

    private sealed record AttrEntry(int Type, List<(long Lcn, long Count)>? Runs, bool Unnamed);

    private static AttrEntry? Attribute(byte[] record, int type, bool unnamedOnly) =>
        Attributes(record).FirstOrDefault(a => a.Type == type && (!unnamedOnly || a.Unnamed));

    private static IEnumerable<AttrEntry> Attributes(byte[] record)
    {
        int at = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x18)), (uint)record.Length);
        for (int n = 0; n < 256 && at + 16 <= used; n++)
        {
            int type = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(at));
            if (type == -1)
            {
                yield break;
            }

            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(at + 4)), int.MaxValue);
            if (length < 16 || at + length > used)
            {
                yield break;
            }

            bool nonResident = record[at + 8] != 0;
            bool unnamed = record[at + 9] == 0;
            List<(long, long)>? runs = null;
            if (nonResident && length >= 0x40)
            {
                int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(at + 0x20));
                if (runOffset < length)
                {
                    runs = DecodeRuns(record.AsSpan(at + runOffset, length - runOffset));
                }
            }

            yield return new AttrEntry(type, runs, unnamed);
            at += length;
        }
    }

    /// <summary>ランリスト (長さと相対 LCN の組)。スパースのラン (LCN なし) は除く。</summary>
    internal static List<(long Lcn, long Count)> DecodeRuns(ReadOnlySpan<byte> data)
    {
        var runs = new List<(long, long)>();
        long lcn = 0;
        int at = 0;
        while (at < data.Length && data[at] != 0 && runs.Count < MaxRuns)
        {
            int lengthBytes = data[at] & 0x0F;
            int offsetBytes = data[at] >> 4;
            at++;
            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || at + lengthBytes + offsetBytes > data.Length)
            {
                break;
            }

            long count = 0;
            for (int i = 0; i < lengthBytes; i++)
            {
                count |= (long)data[at + i] << (8 * i);
            }

            at += lengthBytes;
            if (offsetBytes > 0)
            {
                long delta = 0;
                for (int i = 0; i < offsetBytes; i++)
                {
                    delta |= (long)data[at + i] << (8 * i);
                }

                if ((data[at + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8)
                {
                    delta |= -1L << (8 * offsetBytes);
                }

                lcn += delta;
                at += offsetBytes;
                if (count > 0 && lcn >= 0)
                {
                    runs.Add((lcn, count));
                }
            }
        }

        return runs;
    }

    /// <summary>更新シーケンス配列 (フィックスアップ) を適用する。合わなければ false。</summary>
    internal static bool Fixup(byte[] record, int sectorSize)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
        if (count < 2 || offset + count * 2 > record.Length || (count - 1) * sectorSize > record.Length)
        {
            return false;
        }

        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset));
        for (int i = 1; i < count; i++)
        {
            int end = i * sectorSize - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(end)) != usn)
            {
                return false;
            }

            record[end] = record[offset + i * 2];
            record[end + 1] = record[offset + i * 2 + 1];
        }

        return true;
    }

    /// <summary>MFT のレコードの読み込み ($MFT のランリストで位置を求める)。</summary>
    private sealed class Reader(IByteSource volume, long cluster, long recordSize, int sectorSize, long totalClusters)
    {
        public List<(long Lcn, long Count)> MftRuns { get; set; } = [];

        public byte[]? ReadAt(long offset)
        {
            byte[] record = new byte[recordSize];
            if (offset < 0 || offset + recordSize > volume.Length || !volume.Read(offset, record).IsComplete || !Fixup(record, sectorSize))
            {
                return null;
            }

            return record;
        }

        public byte[]? Record(long index) => Offset(index) is long at ? ReadAt(at) : null;

        private long? Offset(long index)
        {
            long vcnBytes = index * recordSize;
            foreach ((long lcn, long count) in MftRuns)
            {
                long bytes = count * cluster;
                if (vcnBytes < bytes)
                {
                    return lcn * cluster + vcnBytes;
                }

                vcnBytes -= bytes;
            }

            return null;
        }

        /// <summary>連続したレコードを読む (ランの境界をまたぐ場合はレコードごとに読む)。読めないレコードは 0。</summary>
        public void ReadRecords(long first, int count, byte[] buffer)
        {
            Array.Clear(buffer);
            for (int i = 0; i < count;)
            {
                if (Offset(first + i) is not long at)
                {
                    return;
                }

                // 同じランの中で続けて読める数。
                int n = 1;
                while (i + n < count && Offset(first + i + n) == at + n * recordSize)
                {
                    n++;
                }

                volume.Read(at, buffer.AsSpan((int)(i * recordSize), (int)(n * recordSize)));
                i += n;
            }
        }

        public IEnumerable<(long, long)> ToBytes(IEnumerable<(long Lcn, long Count)> runs) =>
            runs.Where(r => r.Lcn < totalClusters).Select(r => (r.Lcn * cluster, Math.Min(r.Count, totalClusters - r.Lcn) * cluster));
    }
}
