using System.Buffers.Binary;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Scope;

/// <summary>
/// FAT12 / FAT16 / FAT32 の割り当て済みの領域 (FAT のエントリが 0 でないクラスタ) とメタデータ (予約領域・FAT・ルートディレクトリと、
/// すべてのディレクトリのクラスタ) の範囲 (FOR-23)。ディレクトリは訪れたクラスタを覚えて循環を止める。
/// </summary>
public sealed class FatRegionProvider : IFileSystemRegionProvider
{
    private const int MaxDirectories = 1_000_000;

    public string Name => "FAT";

    public bool Probe(ReadOnlySpan<byte> head)
    {
        if (head.Length < 512 || head[510] != 0x55 || head[511] != 0xAA || head.Slice(3, 8).SequenceEqual("NTFS    "u8) || head.Slice(3, 8).SequenceEqual("EXFAT   "u8))
        {
            return false;
        }

        int bps = BinaryPrimitives.ReadUInt16LittleEndian(head[11..]);
        int spc = head[13];
        return bps is 512 or 1024 or 2048 or 4096 && spc > 0 && (spc & (spc - 1)) == 0 && head[16] is >= 1 and <= 4
            && BinaryPrimitives.ReadUInt16LittleEndian(head[14..]) > 0;
    }

    public FileSystemRegions? Analyze(IByteSource volume, CancellationToken cancellationToken)
    {
        byte[] boot = new byte[512];
        volume.Read(0, boot);
        var fat = Layout.Parse(boot, volume.Length);
        if (fat is null)
        {
            return null;
        }

        Layout l = fat.Value;
        var metadata = new List<(long, long)> { (0, l.DataStart) };
        var reader = new FatTable(volume, l);

        // ディレクトリをたどる (FAT32 のルートはクラスタの連鎖、FAT12/16 は固定の領域)。
        var queue = new Queue<long>();
        var visited = new HashSet<long>();
        if (l.Type == 32)
        {
            queue.Enqueue(l.RootCluster);
        }
        else
        {
            byte[] root = new byte[l.RootDirSize];
            volume.Read(l.RootDirStart, root);
            Entries(root, queue);
        }

        int directories = 0;
        while (queue.Count > 0 && directories++ < MaxDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long first = queue.Dequeue();
            foreach (long c in reader.Chain(first, visited))
            {
                long at = l.ClusterOffset(c);
                metadata.Add((at, l.ClusterSize));
                byte[] data = new byte[l.ClusterSize];
                if (at + l.ClusterSize <= volume.Length)
                {
                    volume.Read(at, data);
                    Entries(data, queue);
                }
            }
        }

        // 割り当て済み: FAT のエントリが 0 でないクラスタ。
        var runs = new List<(long Cluster, long Count)>();
        long runStart = -1;
        for (long c = 2; c < l.Clusters + 2; c++)
        {
            if ((c & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bool used = reader.Entry(c) != 0;
            if (used && runStart < 0)
            {
                runStart = c;
            }
            else if (!used && runStart >= 0)
            {
                runs.Add((runStart, c - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            runs.Add((runStart, l.Clusters + 2 - runStart));
        }

        List<(long, long)> meta = FileSystemRegionProviders.Merge(metadata);
        IEnumerable<(long, long)> allocated = runs.Select(r => (l.ClusterOffset(r.Cluster), r.Count * l.ClusterSize));
        return new FileSystemRegions("FAT" + l.Type, FileSystemRegionProviders.Merge(allocated.Concat(meta)), meta);
    }

    /// <summary>ディレクトリのエントリから、サブディレクトリの先頭クラスタを集める (「.」「..」、削除、長い名前のエントリは除く)。</summary>
    private static void Entries(ReadOnlySpan<byte> data, Queue<long> queue)
    {
        for (int at = 0; at + 32 <= data.Length; at += 32)
        {
            ReadOnlySpan<byte> e = data.Slice(at, 32);
            if (e[0] == 0)
            {
                return;
            }

            byte attr = e[11];
            if (e[0] == 0xE5 || e[0] == (byte)'.' || attr == 0x0F || (attr & 0x10) == 0 || (attr & 0x08) != 0)
            {
                continue;
            }

            long cluster = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]) | ((long)BinaryPrimitives.ReadUInt16LittleEndian(e[20..]) << 16);
            if (cluster >= 2)
            {
                queue.Enqueue(cluster);
            }
        }
    }

    internal readonly record struct Layout(int Type, int BytesPerSector, long ClusterSize, long FatStart, long FatSize, int Fats, long RootDirStart,
        int RootDirSize, long DataStart, long Clusters, long RootCluster)
    {
        public long ClusterOffset(long cluster) => DataStart + (cluster - 2) * ClusterSize;

        public static Layout? Parse(byte[] b, long volumeLength)
        {
            int bps = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(11));
            int spc = b[13];
            int reserved = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14));
            int fats = b[16];
            int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(17));
            long total = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(19));
            if (total == 0)
            {
                total = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(32));
            }

            long fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(22));
            if (fatSectors == 0)
            {
                fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));
            }

            if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || (spc & (spc - 1)) != 0 || reserved == 0 || fats is < 1 or > 4 || total == 0 || fatSectors == 0)
            {
                return null;
            }

            long rootSectors = (rootEntries * 32L + bps - 1) / bps;
            long dataSector = reserved + fats * fatSectors + rootSectors;
            if (dataSector >= total)
            {
                return null;
            }

            long clusters = (total - dataSector) / spc;
            int type = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
            long rootCluster = type == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(44)) : 0;
            long fatBytes = fatSectors * bps;
            long needed = type == 12 ? (clusters + 2) * 3 / 2 : (clusters + 2) * (type / 8);
            if (fatBytes < needed || (type == 32 && rootCluster < 2) || reserved * (long)bps > volumeLength)
            {
                return null;
            }

            return new Layout(type, bps, (long)spc * bps, reserved * (long)bps, fatBytes, fats, (reserved + fats * fatSectors) * bps,
                (int)(rootSectors * bps), dataSector * bps, clusters, rootCluster);
        }
    }

    /// <summary>FAT のエントリの読み込み (FAT のセクタを小さく読み込んでおく)。</summary>
    private sealed class FatTable(IByteSource volume, Layout layout)
    {
        private const int Page = 64 * 1024;
        private long _page = -1;
        private readonly byte[] _buffer = new byte[Page + 4];

        public long Entry(long cluster)
        {
            if (cluster < 0 || cluster >= layout.Clusters + 2)
            {
                return 0;
            }

            long byteOffset = layout.Type switch
            {
                12 => cluster * 3 / 2,
                16 => cluster * 2,
                _ => cluster * 4,
            };
            long page = byteOffset / Page;
            if (page != _page)
            {
                Array.Clear(_buffer);
                long at = layout.FatStart + page * Page;
                volume.Read(at, _buffer.AsSpan(0, (int)Math.Min(_buffer.Length, Math.Max(0, volume.Length - at))));
                _page = page;
            }

            int i = (int)(byteOffset - page * Page);
            return layout.Type switch
            {
                12 => (cluster & 1) == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(i)) & 0xFFF : BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(i)) >> 4,
                16 => BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(i)),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(i)) & 0x0FFFFFFF,
            };
        }

        /// <summary>クラスタの連鎖 (終端・不正な値・訪れたクラスタで止める)。</summary>
        public IEnumerable<long> Chain(long first, HashSet<long> visited)
        {
            long end = layout.Type == 12 ? 0xFF8 : layout.Type == 16 ? 0xFFF8 : 0x0FFFFFF8;
            long c = first;
            while (c >= 2 && c < layout.Clusters + 2 && visited.Add(c))
            {
                yield return c;
                long next = Entry(c);
                if (next >= end || next < 2)
                {
                    yield break;
                }

                c = next;
            }
        }
    }
}

/// <summary>exFAT の割り当て済みの領域 (割り当てビットマップ) とメタデータ (ブート領域、FAT、ビットマップ、大文字表、すべてのディレクトリ) の範囲。</summary>
public sealed class ExFatRegionProvider : IFileSystemRegionProvider
{
    private const int MaxDirectories = 1_000_000;

    public string Name => "exFAT";

    public bool Probe(ReadOnlySpan<byte> head) => head.Length >= 512 && head.Slice(3, 8).SequenceEqual("EXFAT   "u8);

    public FileSystemRegions? Analyze(IByteSource volume, CancellationToken cancellationToken)
    {
        byte[] b = new byte[512];
        volume.Read(0, b);
        long fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(80));
        long fatLength = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(84));
        long heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(88));
        long clusters = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(92));
        long rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(96));
        int bpsShift = b[108];
        int spcShift = b[109];
        int fats = b[110];
        if (bpsShift is < 9 or > 12 || spcShift > 25 - bpsShift || fats is < 1 or > 2 || clusters == 0 || rootCluster < 2)
        {
            return null;
        }

        long bps = 1L << bpsShift;
        long cluster = bps << spcShift;
        long heap = heapOffset * bps;
        long Offset(long c) => heap + (c - 2) * cluster;
        var metadata = new List<(long, long)> { (0, 24 * bps), (fatOffset * bps, fatLength * fats * bps) };
        byte[] fatPage = new byte[(int)Math.Min(fatLength * bps, 64L * 1024 * 1024)];
        volume.Read(fatOffset * bps, fatPage);
        long Next(long c) => c * 4 + 4 <= fatPage.Length ? BinaryPrimitives.ReadUInt32LittleEndian(fatPage.AsSpan((int)(c * 4))) : 0xFFFFFFFF;

        IEnumerable<long> Clusters(long first, long length, bool contiguous, HashSet<long> visited)
        {
            long count = length > 0 ? (length + cluster - 1) / cluster : long.MaxValue;
            long c = first;
            for (long i = 0; i < count && c >= 2 && c < clusters + 2 && visited.Add(c); i++)
            {
                yield return c;
                c = contiguous ? c + 1 : Next(c);
            }
        }

        var bitmaps = new List<(long First, long Length)>();
        var queue = new Queue<(long First, long Length, bool Contiguous)>();
        queue.Enqueue((rootCluster, 0, false));
        var visited = new HashSet<long>();
        int directories = 0;
        while (queue.Count > 0 && directories++ < MaxDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (long first, long length, bool contiguous) = queue.Dequeue();
            byte[] data = new byte[cluster];
            bool pendingDirectory = false;
            foreach (long c in Clusters(first, length, contiguous, visited).ToList())
            {
                metadata.Add((Offset(c), cluster));
                if (Offset(c) + cluster > volume.Length)
                {
                    continue;
                }

                volume.Read(Offset(c), data);
                for (int at = 0; at + 32 <= data.Length; at += 32)
                {
                    byte type = data[at];
                    if (type == 0)
                    {
                        break;
                    }

                    ReadOnlySpan<byte> e = data.AsSpan(at, 32);
                    long fc = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
                    long dl = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(e[24..]), long.MaxValue / 2);
                    switch (type)
                    {
                        case 0x81: // 割り当てビットマップ
                            bitmaps.Add((fc, dl));
                            foreach (long bc in Clusters(fc, dl, true, [])) { metadata.Add((Offset(bc), cluster)); }
                            break;
                        case 0x82: // 大文字表
                            foreach (long uc in Clusters(fc, dl, true, [])) { metadata.Add((Offset(uc), cluster)); }
                            break;
                        case 0x85: // ファイル: 属性のディレクトリのビット
                            pendingDirectory = (BinaryPrimitives.ReadUInt16LittleEndian(e[4..]) & 0x10) != 0;
                            break;
                        case 0xC0 when pendingDirectory: // ストリーム拡張: サブディレクトリの位置
                            queue.Enqueue((fc, dl, (e[1] & 0x02) != 0));
                            pendingDirectory = false;
                            break;
                    }
                }
            }
        }

        var runs = new List<(long Cluster, long Count)>();
        foreach ((long first, long length) in bitmaps.Take(1))
        {
            byte[] bitmap = new byte[(int)Math.Min(length, (clusters + 7) / 8)];
            volume.Read(Offset(first), bitmap);
            FileSystemRegionProviders.BitmapRuns(bitmap, 2, clusters + 2, runs);
        }

        List<(long, long)> meta = FileSystemRegionProviders.Merge(metadata);
        IEnumerable<(long, long)> allocated = runs.Select(r => (Offset(r.Cluster), r.Count * cluster));
        return new FileSystemRegions("exFAT", FileSystemRegionProviders.Merge(allocated.Concat(meta)), meta);
    }
}
