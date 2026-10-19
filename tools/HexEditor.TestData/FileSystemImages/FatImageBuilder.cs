using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>FAT の種類 (テスト用イメージの作成)。</summary>
public enum FatType
{
    Fat12,
    Fat16,
    Fat32,
}

/// <summary>作ったファイルの位置 (テストの期待値)。</summary>
public sealed record BuiltFile(string Name, long Size, IReadOnlyList<long> Clusters, long EntryOffset, IReadOnlyList<(long Offset, long Length)> Fragments)
{
    public long FirstCluster => Clusters.Count > 0 ? Clusters[0] : 0;
}

/// <summary>
/// テスト用の FAT12 / FAT16 / FAT32 のイメージを作る小さなフォーマッタ (TD-FOR-FAT12・FAT16・FAT32 とその変形)。アプリの解析の実装とは別に、
/// 仕様 (Microsoft の FAT の仕様) から直接書く。ルートディレクトリにファイルを置き、すべてのファイルに LFN を付ける。
/// </summary>
public sealed class FatImageBuilder
{
    private readonly SparseImage _image;
    private readonly int _bps = 512;
    private readonly int _spc;
    private readonly int _reserved;
    private readonly int _rootEntries;
    private readonly long _fatSectors;
    private readonly long _totalSectors;
    private readonly uint[] _fat;
    private readonly List<byte[]> _rootEntriesList = [];
    private readonly string _label;
    private readonly uint _volumeId;
    private readonly long _hiddenSectors;
    private long _next = 2;

    public FatImageBuilder(FatType type, long volumeBytes, int clusterSize, string label, uint volumeId = 0x1234ABCD, long hiddenSectors = 0)
    {
        Type = type;
        _image = new SparseImage(volumeBytes);
        _spc = clusterSize / _bps;
        _totalSectors = volumeBytes / _bps;
        _reserved = type == FatType.Fat32 ? 32 : 1;
        _rootEntries = type == FatType.Fat32 ? 0 : 512;
        _label = label;
        _volumeId = volumeId;
        _hiddenSectors = hiddenSectors;
        long rootSectors = _rootEntries * 32 / _bps;

        // FAT のセクタ数をクラスタ数から決める (足りるまで増やす)。
        long fatSectors = 1;
        while (true)
        {
            long clusters = (_totalSectors - _reserved - rootSectors - 2 * fatSectors) / _spc;
            long bytes = type switch { FatType.Fat12 => (clusters + 2) * 3 / 2 + 1, FatType.Fat16 => (clusters + 2) * 2, _ => (clusters + 2) * 4 };
            long needed = (bytes + _bps - 1) / _bps;
            if (needed <= fatSectors)
            {
                ClusterCount = clusters;
                break;
            }

            fatSectors = needed;
        }

        _fatSectors = fatSectors;
        _fat = new uint[ClusterCount + 2];
        _fat[0] = type switch { FatType.Fat12 => 0xFF8, FatType.Fat16 => 0xFFF8, _ => 0x0FFFFFF8 };
        _fat[1] = type switch { FatType.Fat12 => 0xFFF, FatType.Fat16 => 0xFFFF, _ => 0x0FFFFFFF };
        if (type == FatType.Fat32)
        {
            // ルートディレクトリはクラスタ 2 から (1 クラスタ)。
            RootCluster = Allocate(1)[0];
        }

        CheckKind();
    }

    public FatType Type { get; }
    public long ClusterCount { get; }
    public long RootCluster { get; }
    public int ClusterSize => _spc * _bps;
    public int BytesPerSector => _bps;
    public long FirstDataSector => _reserved + 2 * _fatSectors + _rootEntries * 32 / _bps;
    public long FatSectors => _fatSectors;
    public int ReservedSectors => _reserved;
    public long DataOffset => FirstDataSector * _bps;

    /// <summary>作ったファイル (名前で引く)。</summary>
    public Dictionary<string, BuiltFile> Files { get; } = [];

    private void CheckKind()
    {
        FatType actual = ClusterCount < 4085 ? FatType.Fat12 : ClusterCount < 65525 ? FatType.Fat16 : FatType.Fat32;
        if (actual != Type)
        {
            throw new InvalidOperationException($"{ClusterCount} clusters make {actual}, not {Type}");
        }
    }

    public long ClusterOffset(long cluster) => DataOffset + (cluster - 2) * ClusterSize;

    private uint End => Type switch { FatType.Fat12 => 0xFFFu, FatType.Fat16 => 0xFFFFu, _ => 0x0FFFFFFFu };

    /// <summary>連続したクラスタを割り当てる (チェーンを FAT に書く)。</summary>
    public List<long> Allocate(long count)
    {
        var list = new List<long>();
        for (long i = 0; i < count; i++)
        {
            list.Add(_next++);
        }

        Chain(list);
        return list;
    }

    /// <summary>クラスタを飛ばす (断片の間の隙間。割り当てない)。</summary>
    public void Skip(long clusters) => _next += clusters;

    private void Chain(List<long> clusters)
    {
        for (int i = 0; i < clusters.Count; i++)
        {
            _fat[clusters[i]] = i + 1 < clusters.Count ? (uint)clusters[i + 1] : End;
        }
    }

    /// <summary>ファイルを書く。<paramref name="fragments"/> 個の断片に分け、断片の間を <paramref name="gapBytes"/> だけ空ける。</summary>
    public BuiltFile AddFile(string name, byte[] content, DateTime modifiedUtc, TimeSpan zone, int fragments = 1, long gapBytes = 0)
    {
        long clusters = Math.Max(content.Length == 0 ? 0 : 1, (content.Length + ClusterSize - 1) / ClusterSize);
        var all = new List<long>();
        var pieces = new List<(long, long)>();
        long perFragment = (clusters + fragments - 1) / fragments;
        long logical = 0;
        for (int f = 0; f < fragments && all.Count < clusters; f++)
        {
            if (f > 0 && gapBytes > 0)
            {
                Skip((gapBytes + ClusterSize - 1) / ClusterSize);
            }

            long n = Math.Min(perFragment, clusters - all.Count);
            long first = _next;
            for (long i = 0; i < n; i++)
            {
                all.Add(_next++);
            }

            long bytes = Math.Min(n * ClusterSize, content.Length - logical);
            pieces.Add((ClusterOffset(first), bytes));
            logical += n * ClusterSize;
        }

        Chain(all);
        for (int i = 0; i < all.Count; i++)
        {
            int at = i * ClusterSize;
            int n = Math.Min(ClusterSize, content.Length - at);
            _image.Write(ClusterOffset(all[i]), content.AsSpan(at, n));
        }

        DateTime local = modifiedUtc + zone;
        long entryOffset = AddEntries(name, 0x20, all.Count > 0 ? all[0] : 0, content.Length, local);
        var built = new BuiltFile(name, content.Length, all, entryOffset, pieces);
        Files[name] = built;
        return built;
    }

    /// <summary>ファイルを削除する: エントリ (LFN を含む) の先頭を E5 にし、FAT のチェーンを 0 にする。データは残す。</summary>
    public void Delete(string name)
    {
        BuiltFile f = Files[name];
        int index = _entryIndex[name];
        int count = _entryCount[name];
        for (int i = index - count + 1; i <= index; i++)
        {
            _rootEntriesList[i][0] = 0xE5;
        }

        foreach (long c in f.Clusters)
        {
            _fat[c] = 0;
        }
    }

    private readonly Dictionary<string, int> _entryIndex = [];
    private readonly Dictionary<string, int> _entryCount = [];

    /// <summary>LFN と短い名前のエントリを書く。短い名前のエントリのボリューム上の位置を返す。</summary>
    private long AddEntries(string name, byte attributes, long firstCluster, long size, DateTime local)
    {
        byte[] shortName = ShortName(name, _rootEntriesList.Count);
        byte checksum = 0;
        for (int i = 0; i < 11; i++)
        {
            checksum = (byte)((((checksum & 1) << 7) | (checksum >> 1)) + shortName[i]);
        }

        char[] chars = [.. name];
        int parts = (chars.Length + 12) / 13;
        for (int p = parts; p >= 1; p--)
        {
            byte[] e = new byte[32];
            e[0] = (byte)(p | (p == parts ? 0x40 : 0));
            e[11] = 0x0F;
            e[13] = checksum;
            int[] slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
            for (int k = 0; k < 13; k++)
            {
                int ci = (p - 1) * 13 + k;
                ushort value = ci < chars.Length ? chars[ci] : ci == chars.Length ? (ushort)0 : (ushort)0xFFFF;
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(slots[k]), value);
            }

            _rootEntriesList.Add(e);
        }

        byte[] s = new byte[32];
        shortName.CopyTo(s, 0);
        s[11] = attributes;
        (ushort date, ushort time, byte tenMs) = DosTime(local);
        s[13] = tenMs;
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(14), time);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(16), date);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(18), date);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(20), (ushort)(firstCluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(22), time);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(24), date);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(26), (ushort)firstCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(28), (uint)size);
        _rootEntriesList.Add(s);
        _entryIndex[name] = _rootEntriesList.Count - 1;
        _entryCount[name] = parts + 1;
        return RootEntryOffset(_rootEntriesList.Count - 1) + 32;
    }

    private long RootEntryOffset(int index) => Type == FatType.Fat32
        ? ClusterOffset(RootCluster) + index * 32L
        : (_reserved + 2 * _fatSectors) * _bps + index * 32L;

    public static (ushort Date, ushort Time, byte TenMs) DosTime(DateTime t)
    {
        ushort date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        ushort time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
        byte tenMs = (byte)((t.Second % 2) * 100 + t.Millisecond / 10);
        return (date, time, tenMs);
    }

    /// <summary>短い名前: 大文字の 8.3。使えない文字は <c>_</c>、長い名前は <c>~N</c> を付ける。</summary>
    private static byte[] ShortName(string name, int serial)
    {
        string upper = name.ToUpperInvariant();
        int dot = upper.LastIndexOf('.');
        string stem = dot > 0 ? upper[..dot] : upper;
        string ext = dot > 0 ? upper[(dot + 1)..] : string.Empty;
        static string Clean(string s) => new([.. s.Select(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? c : '_')]);
        stem = Clean(stem);
        ext = Clean(ext);
        if (stem.Length > 8 || ext.Length > 3 || stem != name[..(dot > 0 ? dot : name.Length)].ToUpperInvariant())
        {
            stem = (stem.Length > 6 ? stem[..6] : stem) + "~" + (serial % 9 + 1);
        }

        byte[] b = Encoding.ASCII.GetBytes(stem.PadRight(8)[..8] + ext.PadRight(3)[..3]);
        return b;
    }

    /// <summary>FAT のエントリを書き換える (壊したイメージ。<paramref name="fatIndex"/> は 0 か 1、null なら両方)。</summary>
    public void SetFatEntry(long cluster, uint value, int? fatIndex = null)
    {
        if (fatIndex is null)
        {
            _fat[cluster] = value;
        }
        else
        {
            (_overrides[fatIndex.Value] ??= [])[cluster] = value;
        }
    }

    private readonly Dictionary<long, uint>?[] _overrides = new Dictionary<long, uint>?[2];

    public uint EndOfChain => End;

    /// <summary>ブートセクタ・FAT・ルートディレクトリを書いてイメージを返す。</summary>
    public SparseImage Build()
    {
        byte[] boot = new byte[512];
        boot[0] = 0xEB; boot[1] = Type == FatType.Fat32 ? (byte)0x58 : (byte)0x3C; boot[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(boot, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11), (ushort)_bps);
        boot[13] = (byte)_spc;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14), (ushort)_reserved);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(17), (ushort)_rootEntries);
        if (_totalSectors < 65536 && Type != FatType.Fat32)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19), (ushort)_totalSectors);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), (uint)_totalSectors);
        }

        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(24), 63);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(26), 255);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(28), (uint)_hiddenSectors);
        byte[] label = Encoding.ASCII.GetBytes(_label.PadRight(11)[..11]);
        if (Type == FatType.Fat32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36), (uint)_fatSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44), (uint)RootCluster);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(48), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(50), 6);
            boot[64] = 0x80;
            boot[66] = 0x29;
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(67), _volumeId);
            label.CopyTo(boot, 71);
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(22), (ushort)_fatSectors);
            boot[36] = 0x80;
            boot[38] = 0x29;
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(39), _volumeId);
            label.CopyTo(boot, 43);
            Encoding.ASCII.GetBytes(Type == FatType.Fat12 ? "FAT12   " : "FAT16   ").CopyTo(boot, 54);
        }

        boot[510] = 0x55;
        boot[511] = 0xAA;
        _image.Write(0, boot);

        if (Type == FatType.Fat32)
        {
            long free = _fat.Skip(2).LongCount(v => v == 0);
            byte[] info = new byte[512];
            BinaryPrimitives.WriteUInt32LittleEndian(info, 0x41615252);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(484), 0x61417272);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488), (uint)free);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492), (uint)_next);
            info[510] = 0x55;
            info[511] = 0xAA;
            _image.Write(512, info);
            _image.Write(6 * 512, boot);
            _image.Write(7 * 512, info);
        }

        for (int f = 0; f < 2; f++)
        {
            byte[] fat = new byte[_fatSectors * _bps];
            for (long c = 0; c < _fat.Length; c++)
            {
                uint v = _overrides[f]?.TryGetValue(c, out uint o) == true ? o : _fat[c];
                switch (Type)
                {
                    case FatType.Fat12:
                        {
                            int at = (int)(c * 3 / 2);
                            ushort old = BinaryPrimitives.ReadUInt16LittleEndian(fat.AsSpan(at));
                            ushort nv = (c & 1) == 0 ? (ushort)((old & 0xF000u) | (v & 0xFFFu)) : (ushort)((old & 0x000Fu) | ((v & 0xFFFu) << 4));
                            BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(at), nv);
                            break;
                        }

                    case FatType.Fat16:
                        BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan((int)(c * 2)), (ushort)v);
                        break;
                    default:
                        BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan((int)(c * 4)), v);
                        break;
                }
            }

            _image.Write((_reserved + f * _fatSectors) * _bps, fat);
        }

        // ルートディレクトリ: ボリュームラベル、ファイルのエントリ。
        var root = new List<byte> { };
        byte[] volume = new byte[32];
        label.CopyTo(volume, 0);
        volume[11] = 0x08;
        root.AddRange(volume);
        foreach (byte[] e in _rootEntriesList)
        {
            root.AddRange(e);
        }

        // ルートディレクトリの先頭にボリュームラベル、その後にエントリ (EntryOffsetOf の +32)。FAT12/16 のルートディレクトリの領域も先頭から書く
        // (以前は 32 バイト前から書いていて、ラベルが 2 つ目の FAT の末尾に入り、FAT の写しが食い違っていた)。
        long rootOffset = RootEntryOffset(0);
        if (Type == FatType.Fat32)
        {
            if (root.Count > ClusterSize)
            {
                throw new InvalidOperationException("the root directory does not fit in one cluster");
            }

            rootOffset = ClusterOffset(RootCluster);
        }

        _image.Write(rootOffset, root.ToArray());
        return _image;
    }

    /// <summary>ルートのエントリの位置は、ボリュームラベルの分 (32 バイト) だけ後ろにずれる。</summary>
    public long EntryOffsetOf(string name) => RootEntryOffset(_entryIndex[name]) + 32;

    /// <summary>TD-FOR-FILESET を入れた FAT のイメージ。</summary>
    public static FatImageBuilder WithFileSet(FatType type, long volumeBytes, int clusterSize, string label, long hiddenSectors = 0)
    {
        var b = new FatImageBuilder(type, volumeBytes, clusterSize, label, hiddenSectors: hiddenSectors);
        foreach (FileSetItem item in FileSet.Common)
        {
            if (item.Name == "frag.bin")
            {
                // 256 KiB ずつ 3 つの断片。断片の間に 64 KiB 以上の隙間。
                b.AddFile(item.Name, item.Content, item.ModifiedUtc, FileSet.FatZone, fragments: 3, gapBytes: 64 * 1024);
            }
            else
            {
                b.AddFile(item.Name, item.Content, item.ModifiedUtc, FileSet.FatZone);
            }
        }

        // 最後の操作として deleted.txt を削除する (クラスタは再利用しない)。
        b.Delete("deleted.txt");
        return b;
    }
}
