using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// テスト用の exFAT のイメージを作る小さなフォーマッタ (TD-FOR-EXFAT)。exFAT の仕様から直接書く: メインとバックアップのブート領域
/// (チェックサム付き)、FAT、割り当てビットマップ、大文字変換表、ボリュームラベル、ファイルのエントリセット。連続したファイルは NoFatChain にする。
/// </summary>
public sealed class ExFatImageBuilder
{
    private readonly SparseImage _image;
    private readonly int _bps = 512;
    private readonly int _spc;
    private readonly long _totalSectors;
    private readonly long _fatOffset = 128;
    private readonly long _fatLength;
    private readonly long _heapOffset;
    private readonly uint[] _fat;
    private readonly bool[] _bitmap;
    private readonly List<byte[]> _entries = [];
    private readonly Dictionary<string, (int Index, int Count)> _sets = [];
    private long _next = 2;
    private readonly string _label;

    public ExFatImageBuilder(long volumeBytes, int clusterSize, string label)
    {
        _image = new SparseImage(volumeBytes);
        _spc = clusterSize / _bps;
        _totalSectors = volumeBytes / _bps;
        _label = label;
        long clusters = (_totalSectors - 512) / _spc;
        _fatLength = ((clusters + 2) * 4 + _bps - 1) / _bps;
        _heapOffset = (_fatOffset + _fatLength + _spc - 1) / _spc * _spc;
        ClusterCount = (_totalSectors - _heapOffset) / _spc;
        _fat = new uint[ClusterCount + 2];
        _fat[0] = 0xFFFFFFF8;
        _fat[1] = 0xFFFFFFFF;
        _bitmap = new bool[ClusterCount];
        BitmapCluster = Allocate(1, chain: true)[0];
        UpcaseCluster = Allocate(1, chain: true)[0];
        RootCluster = Allocate(1, chain: true)[0];
    }

    public long ClusterCount { get; }
    public long BitmapCluster { get; }
    public long UpcaseCluster { get; }
    public long RootCluster { get; }
    public int ClusterSize => _spc * _bps;
    public long HeapOffset => _heapOffset * _bps;
    public Dictionary<string, BuiltFile> Files { get; } = [];

    public long ClusterOffset(long cluster) => HeapOffset + (cluster - 2) * ClusterSize;

    private List<long> Allocate(long count, bool chain)
    {
        var list = new List<long>();
        for (long i = 0; i < count; i++)
        {
            list.Add(_next);
            _bitmap[_next - 2] = true;
            _next++;
        }

        if (chain)
        {
            Chain(list);
        }

        return list;
    }

    private void Chain(List<long> clusters)
    {
        for (int i = 0; i < clusters.Count; i++)
        {
            _fat[clusters[i]] = i + 1 < clusters.Count ? (uint)clusters[i + 1] : 0xFFFFFFFF;
        }
    }

    /// <summary>ファイルを書く。断片が 1 つなら NoFatChain (FAT を書かない)、2 つ以上なら FAT のチェーン。</summary>
    public BuiltFile AddFile(string name, byte[] content, DateTime modifiedUtc, TimeSpan zone, int fragments = 1, long gapBytes = 0)
    {
        long clusters = Math.Max(1, (content.Length + ClusterSize - 1) / ClusterSize);
        long perFragment = (clusters + fragments - 1) / fragments;
        var all = new List<long>();
        var pieces = new List<(long, long)>();
        long logical = 0;
        for (int f = 0; f < fragments && all.Count < clusters; f++)
        {
            if (f > 0)
            {
                _next += (gapBytes + ClusterSize - 1) / ClusterSize;
            }

            long n = Math.Min(perFragment, clusters - all.Count);
            List<long> part = Allocate(n, chain: false);
            all.AddRange(part);
            pieces.Add((ClusterOffset(part[0]), Math.Min(n * ClusterSize, content.Length - logical)));
            logical += n * ClusterSize;
        }

        bool noFatChain = fragments == 1;
        if (!noFatChain)
        {
            Chain(all);
        }

        for (int i = 0; i < all.Count; i++)
        {
            int at = i * ClusterSize;
            int n = Math.Min(ClusterSize, content.Length - at);
            if (n > 0)
            {
                _image.Write(ClusterOffset(all[i]), content.AsSpan(at, n));
            }
        }

        long entryOffset = AddSet(name, 0x20, all[0], content.Length, noFatChain, modifiedUtc + zone);
        var built = new BuiltFile(name, content.Length, all, entryOffset, pieces);
        Files[name] = built;
        return built;
    }

    /// <summary>削除: エントリセットの使用中ビットを 0 にし、ビットマップを空きにする。</summary>
    public void Delete(string name)
    {
        (int index, int count) = _sets[name];
        for (int i = index; i < index + count; i++)
        {
            _entries[i][0] &= 0x7F;
        }

        foreach (long c in Files[name].Clusters)
        {
            _bitmap[c - 2] = false;
            _fat[c] = 0;
        }
    }

    private static uint Timestamp(DateTime t) =>
        (uint)(((t.Year - 1980) << 25) | (t.Month << 21) | (t.Day << 16) | (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));

    private long AddSet(string name, ushort attributes, long firstCluster, long size, bool noFatChain, DateTime local)
    {
        int nameEntries = (name.Length + 14) / 15;
        byte[] file = new byte[32];
        file[0] = 0x85;
        file[1] = (byte)(1 + nameEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), attributes);
        uint ts = Timestamp(local);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), ts);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), ts);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), ts);
        file[20] = (byte)((local.Second % 2) * 100 + local.Millisecond / 10);
        file[21] = file[20];

        // UTC オフセットは記録しない (タイムゾーンなしの日時。TD-FOR-FILESET の定義)。
        byte[] stream = new byte[32];
        stream[0] = 0xC0;
        stream[1] = (byte)(0x01 | (noFatChain ? 0x02 : 0));
        stream[3] = (byte)name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(4), NameHash(name));
        BinaryPrimitives.WriteUInt64LittleEndian(stream.AsSpan(8), (ulong)size);
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(20), (uint)firstCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(stream.AsSpan(24), (ulong)size);
        var set = new List<byte[]> { file, stream };
        for (int i = 0; i < nameEntries; i++)
        {
            byte[] n = new byte[32];
            n[0] = 0xC1;
            for (int c = 0; c < 15; c++)
            {
                int ci = i * 15 + c;
                if (ci < name.Length)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(n.AsSpan(2 + c * 2), name[ci]);
                }
            }

            set.Add(n);
        }

        ushort sum = 0;
        for (int k = 0; k < set.Count; k++)
        {
            for (int b = 0; b < 32; b++)
            {
                if (k == 0 && b is 2 or 3)
                {
                    continue;
                }

                sum = (ushort)(((sum & 1) != 0 ? 0x8000 : 0) + (sum >> 1) + set[k][b]);
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), sum);
        int index = _entries.Count + 3;
        _sets[name] = (_entries.Count, set.Count);
        _entries.AddRange(set);
        return ClusterOffset(RootCluster) + index * 32L;
    }

    private static ushort NameHash(string name)
    {
        ushort hash = 0;
        foreach (char ch in name.ToUpperInvariant())
        {
            foreach (byte b in BitConverter.GetBytes(ch))
            {
                hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + b);
            }
        }

        return hash;
    }

    public SparseImage Build()
    {
        // 大文字変換表 (先頭の 128 文字。表にない文字はそのまま)。
        byte[] upcase = new byte[256];
        for (int c = 0; c < 128; c++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(upcase.AsSpan(c * 2), char.ToUpperInvariant((char)c));
        }

        uint upcaseSum = 0;
        foreach (byte b in upcase)
        {
            upcaseSum = ((upcaseSum & 1) != 0 ? 0x80000000u : 0) + (upcaseSum >> 1) + b;
        }

        _image.Write(ClusterOffset(UpcaseCluster), upcase);

        byte[] bitmap = new byte[(ClusterCount + 7) / 8];
        for (long i = 0; i < ClusterCount; i++)
        {
            if (_bitmap[i])
            {
                bitmap[i / 8] |= (byte)(1 << (int)(i % 8));
            }
        }

        _image.Write(ClusterOffset(BitmapCluster), bitmap);

        var root = new List<byte>();
        byte[] labelEntry = new byte[32];
        labelEntry[0] = 0x83;
        labelEntry[1] = (byte)_label.Length;
        Encoding.Unicode.GetBytes(_label).CopyTo(labelEntry, 2);
        root.AddRange(labelEntry);
        byte[] bitmapEntry = new byte[32];
        bitmapEntry[0] = 0x81;
        BinaryPrimitives.WriteUInt32LittleEndian(bitmapEntry.AsSpan(20), (uint)BitmapCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(bitmapEntry.AsSpan(24), (ulong)bitmap.Length);
        root.AddRange(bitmapEntry);
        byte[] upcaseEntry = new byte[32];
        upcaseEntry[0] = 0x82;
        BinaryPrimitives.WriteUInt32LittleEndian(upcaseEntry.AsSpan(4), upcaseSum);
        BinaryPrimitives.WriteUInt32LittleEndian(upcaseEntry.AsSpan(20), (uint)UpcaseCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(upcaseEntry.AsSpan(24), (ulong)upcase.Length);
        root.AddRange(upcaseEntry);
        foreach (byte[] e in _entries)
        {
            root.AddRange(e);
        }

        if (root.Count > ClusterSize)
        {
            throw new InvalidOperationException("the root directory does not fit in one cluster");
        }

        _image.Write(ClusterOffset(RootCluster), root.ToArray());

        byte[] fat = new byte[_fatLength * _bps];
        for (long c = 0; c < _fat.Length; c++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan((int)(c * 4)), _fat[c]);
        }

        _image.Write(_fatOffset * _bps, fat);

        // ブート領域 (12 セクタ): ブートセクタ、拡張ブートセクタ 8 つ、OEM パラメータ、予約、チェックサム。
        byte[] region = new byte[12 * _bps];
        byte[] boot = region.AsSpan(0, _bps).ToArray();
        boot[0] = 0xEB; boot[1] = 0x76; boot[2] = 0x90;
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot, 3);
        BinaryPrimitives.WriteUInt64LittleEndian(boot.AsSpan(72), (ulong)_totalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(80), (uint)_fatOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(84), (uint)_fatLength);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(88), (uint)_heapOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(92), (uint)ClusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(96), (uint)RootCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(100), 0xE3FA7000);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(104), 0x0100);
        boot[108] = 9;
        boot[109] = (byte)System.Numerics.BitOperations.Log2((uint)_spc);
        boot[110] = 1;
        boot[111] = 0x80;
        boot[112] = (byte)(_bitmap.Count(b => b) * 100 / Math.Max(1, ClusterCount));
        boot[510] = 0x55;
        boot[511] = 0xAA;
        boot.CopyTo(region, 0);
        for (int s = 1; s <= 8; s++)
        {
            region[s * _bps + _bps - 2] = 0x55;
            region[s * _bps + _bps - 1] = 0xAA;
        }

        uint sum = 0;
        for (int i = 0; i < 11 * _bps; i++)
        {
            if (i is 106 or 107 or 112)
            {
                continue;
            }

            sum = ((sum & 1) != 0 ? 0x80000000u : 0) + (sum >> 1) + region[i];
        }

        for (int i = 0; i < _bps; i += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(11 * _bps + i), sum);
        }

        _image.Write(0, region);
        _image.Write(12L * _bps, region);
        return _image;
    }

    /// <summary>TD-FOR-FILESET を入れた exFAT のイメージ (TD-FOR-EXFAT)。</summary>
    public static ExFatImageBuilder WithFileSet()
    {
        var b = new ExFatImageBuilder(64 * 1024 * 1024, 4096, "EXFAT");
        foreach (FileSetItem item in FileSet.Common)
        {
            b.AddFile(item.Name, item.Content, item.ModifiedUtc, FileSet.FatZone, item.Name == "frag.bin" ? 3 : 1, 64 * 1024);
        }

        b.Delete("deleted.txt");
        return b;
    }
}
