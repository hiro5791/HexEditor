using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>イメージの書き込み先 (メモリ上のスパースイメージ、または大きなイメージ用のスパースファイル)。</summary>
public interface IImageTarget
{
    long Length { get; }

    void Write(long offset, ReadOnlySpan<byte> data);
}

/// <summary><see cref="SparseImage"/> を書き込み先にする。</summary>
public sealed class SparseImageTarget(SparseImage image) : IImageTarget
{
    public long Length => image.Length;

    public void Write(long offset, ReadOnlySpan<byte> data) => image.Write(offset, data);
}

/// <summary>スパースファイルに直接書く (TD-FOR-NTFS-1M など、メモリに置けない大きさのイメージ)。</summary>
public sealed class SparseFileTarget : IImageTarget, IDisposable
{
    private readonly SafeFileHandle _handle;

    public SparseFileTarget(string path, long length)
    {
        _handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        SparseImage.MakeSparse(_handle);
        RandomAccess.SetLength(_handle, length);
        Length = length;
    }

    public long Length { get; }

    public void Write(long offset, ReadOnlySpan<byte> data) => RandomAccess.Write(_handle, data, offset);

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// テスト用の NTFS のイメージを作る小さなフォーマッタ (TD-FOR-NTFS とその変形)。NTFS の公開されている構造 (ブートセクタ、MFT レコード、
/// 属性、データラン、INDX、$LogFile のページ) から直接書く。アプリの解析の実装は使わない。圧縮 (LZNT1) は Windows の RtlCompressBuffer で作る。
/// </summary>
public sealed class NtfsImageBuilder
{
    public const int SectorSize = 512;
    public const int RecordSize = 1024;
    public const int IndexBlockSize = 4096;
    public const int CompressionUnit = 4;

    public sealed class Times
    {
        public DateTime Created { get; init; }
        public DateTime Modified { get; init; }
        public DateTime MftModified { get; init; }
        public DateTime Accessed { get; init; }

        public static Times All(DateTime t) => new() { Created = t, Modified = t, MftModified = t, Accessed = t };

        /// <summary>FILETIME。<see cref="DateTime.MaxValue"/> は <c>FF</c> × 8 (範囲外の日時) の印。</summary>
        public static long FileTime(DateTime t) => t == DateTime.MaxValue ? -1 : t.ToFileTimeUtc();
    }

    /// <summary>ストリーム ($DATA 1 つ)。</summary>
    public sealed class Stream
    {
        public string Name { get; init; } = string.Empty;
        public byte[] Content { get; init; } = [];
        public bool Resident { get; set; }
        public bool Compressed { get; init; }

        /// <summary>データラン (VCN の順。LCN が null ならスパース)。</summary>
        public List<(long Length, long? Lcn)> Runs { get; } = [];

        public long AllocatedClusters => Runs.Sum(r => r.Length);
    }

    public sealed class Node
    {
        public long Number { get; set; }
        public ushort Sequence { get; set; } = 1;
        public bool InUse { get; set; } = true;
        public bool IsDirectory { get; init; }
        public Node? Parent { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? DosName { get; init; }
        public Times SiTimes { get; set; } = Times.All(DateTime.UnixEpoch);
        public Times FnTimes { get; set; } = Times.All(DateTime.UnixEpoch);
        public uint Attributes { get; set; } = 0x20;
        public List<Stream> Streams { get; } = [];
        public List<Node> Children { get; } = [];
        public List<(uint Type, byte[] Value, string Name)> ExtraResident { get; } = [];

        /// <summary>削除の後にインデックススラックに残す (親の最後の INDX の使用領域の後ろに書く)。</summary>
        public bool LeaveInIndexSlack { get; set; }

        /// <summary>インデックスに載せない (削除されたファイル)。</summary>
        public bool Unlinked { get; set; }

        public bool SystemFile { get; init; }

        public Stream? Data => Streams.FirstOrDefault(s => s.Name.Length == 0);

        public string Path => Parent is null ? "\\" : Parent.Parent is null ? "\\" + Name : Parent.Path + "\\" + Name;
    }

    private readonly long _volumeBytes;
    private readonly long _clusters;
    private readonly bool[] _bitmap;
    private readonly List<Node> _nodes = [];
    private readonly string _label;
    private readonly ulong _serial;
    private long _next;
    private readonly int _clusterSize = 4096;

    public NtfsImageBuilder(long volumeBytes, string label, ulong serial = 0x1A2B3C4D5E6F7081, long reservedMftRecords = 256)
    {
        _volumeBytes = volumeBytes;
        _clusters = (volumeBytes - SectorSize) / _clusterSize;
        _bitmap = new bool[_clusters];
        _label = label;
        _serial = serial;
        MftRecords = Math.Max(64, reservedMftRecords);

        // 配置: $Boot (0-1)、$MFTMirr、$LogFile、$UpCase、$AttrDef、$Bitmap、$MFT、以降はファイルのデータ。
        BootRun = (AllocateAt(0, 2), 2);
        MirrorLcn = Allocate(1);
        LogLcn = Allocate(64);
        UpcaseLcn = Allocate(32);
        AttrDefLcn = Allocate(1);
        BitmapClusters = (_clusters / 8 + _clusterSize - 1) / _clusterSize;
        BitmapLcn = Allocate(BitmapClusters);
        long mftClusters = (MftRecords * RecordSize + _clusterSize - 1) / _clusterSize;
        MftLcn = Allocate(mftClusters);

        Root = new Node { Number = 5, Sequence = 5, IsDirectory = true, Name = ".", SystemFile = true, Attributes = 0x06 };
        string[] names = ["$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", ".", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend"];
        for (int i = 0; i < 12; i++)
        {
            Node n = i == 5 ? Root : new Node
            {
                Number = i, Sequence = (ushort)(i == 0 ? 1 : i), IsDirectory = i == 11, Parent = Root, Name = names[i], SystemFile = true, Attributes = 0x06,
            };
            _nodes.Add(n);
            if (i != 5)
            {
                Root.Children.Add(n);
            }
        }

        Extend = _nodes[11];
        _nodes[0].Streams.Add(new Stream { Runs = { (mftClusters, MftLcn) }, Content = [] });
        _nodes[1].Streams.Add(new Stream { Runs = { (1, MirrorLcn) } });
        _nodes[2].Streams.Add(new Stream { Runs = { (64, LogLcn) } });
        _nodes[3].Streams.Add(new Stream { Resident = true });
        _nodes[3].ExtraResident.Add((0x60, Encoding.Unicode.GetBytes(label), string.Empty));
        _nodes[3].ExtraResident.Add((0x70, [0, 0, 0, 0, 0, 0, 0, 0, 3, 1, 0, 0], string.Empty));
        _nodes[4].Streams.Add(new Stream { Runs = { (1, AttrDefLcn) } });
        _nodes[6].Streams.Add(new Stream { Runs = { (BitmapClusters, BitmapLcn) } });
        _nodes[7].Streams.Add(new Stream { Runs = { (2, 0L) } });
        _nodes[8].Streams.Add(new Stream { Resident = true });
        _nodes[8].Streams.Add(new Stream { Name = "$Bad", Runs = { (_clusters, null) } });
        _nodes[10].Streams.Add(new Stream { Runs = { (32, UpcaseLcn) } });
        foreach (Node n in _nodes)
        {
            n.SiTimes = Times.All(BaseTime);
            n.FnTimes = Times.All(BaseTime);
        }

        _nextRecord = 24;
    }

    /// <summary>システムファイルの日時 (フォーマットした日時の想定)。</summary>
    public static readonly DateTime BaseTime = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc).AddTicks(5555555);

    public Node Root { get; }
    public Node Extend { get; }
    public long MftRecords { get; }
    public long MftLcn { get; }
    public long MirrorLcn { get; }
    public long LogLcn { get; }
    public long UpcaseLcn { get; }
    public long AttrDefLcn { get; }
    public long BitmapLcn { get; }
    public long BitmapClusters { get; }
    public (long Lcn, long Count) BootRun { get; }
    public int ClusterSize => _clusterSize;
    public long ClusterCount => _clusters;
    public long TotalSectors => _volumeBytes / SectorSize - 1;

    private long _nextRecord;

    public long RecordOffset(long number) => MftLcn * _clusterSize + number * RecordSize;

    private long AllocateAt(long lcn, long count)
    {
        for (long i = 0; i < count; i++)
        {
            _bitmap[lcn + i] = true;
        }

        _next = Math.Max(_next, lcn + count);
        return lcn;
    }

    /// <summary>連続したクラスタを割り当てる (次の空きから)。</summary>
    public long Allocate(long count) => AllocateAt(_next, count);

    /// <summary>クラスタを飛ばす (断片の間の隙間)。</summary>
    public void Skip(long clusters) => _next += clusters;

    /// <summary>割り当てを解除する (削除。データは残す)。</summary>
    public void Free(Stream stream)
    {
        foreach ((long length, long? lcn) in stream.Runs)
        {
            if (lcn is long l)
            {
                for (long i = 0; i < length; i++)
                {
                    _bitmap[l + i] = false;
                }
            }
        }
    }

    /// <summary>指定した LCN に割り当てる (別のファイルのクラスタを使う。TD-FOR-NTFS-OVERWRITE)。</summary>
    public void Claim(long lcn, long count) => AllocateAt(lcn, count);

    private readonly Dictionary<long, byte[]> _clusterData = [];
    private readonly List<(long Offset, byte[] Data)> _raw = [];

    private Node NewNode(Node parent, string name, bool directory, string? dosName = null)
    {
        if (_nextRecord >= MftRecords)
        {
            throw new InvalidOperationException("the MFT is full");
        }

        var node = new Node { Number = _nextRecord++, IsDirectory = directory, Parent = parent, Name = name, DosName = dosName };
        _nodes.Add(node);
        parent.Children.Add(node);
        return node;
    }

    public Node AddDirectory(Node parent, string name, DateTime time)
    {
        Node node = NewNode(parent, name, true);
        node.SiTimes = Times.All(time);
        node.FnTimes = Times.All(time);
        node.Attributes = 0x10;
        return node;
    }

    /// <summary>
    /// ファイルを加える。<paramref name="fragments"/> 個の断片に分け、断片の間を <paramref name="gapClusters"/> 空ける。<paramref name="resident"/> が
    /// null なら 600 バイト以下を常駐にする。<paramref name="allocatedContent"/> を指定すると、クラスタにはその内容を書く (書いてから切り詰めたファイル)。
    /// </summary>
    public Node AddFile(Node parent, string name, byte[] content, DateTime time, int fragments = 1, long gapClusters = 0, bool? resident = null,
        bool compressed = false, string? dosName = null, byte[]? allocatedContent = null, uint attributes = 0x20)
    {
        Node node = NewNode(parent, name, false, dosName);
        node.SiTimes = Times.All(time);
        node.FnTimes = Times.All(time);
        node.Attributes = attributes | (compressed ? 0x800u : 0);
        node.Streams.Add(MakeStream(string.Empty, content, fragments, gapClusters, resident, compressed, allocatedContent));
        return node;
    }

    /// <summary>名前付きのストリーム (代替データストリーム) を加える。</summary>
    public void AddStream(Node node, string name, byte[] content, bool? resident = null) =>
        node.Streams.Add(MakeStream(name, content, 1, 0, resident, false, null));

    private Stream MakeStream(string name, byte[] content, int fragments, long gapClusters, bool? resident, bool compressed, byte[]? allocatedContent)
    {
        bool isResident = resident ?? (content.Length <= 600 && !compressed);
        var stream = new Stream { Name = name, Content = content, Resident = isResident, Compressed = compressed };
        if (isResident)
        {
            return stream;
        }

        byte[] stored = allocatedContent ?? content;
        if (compressed)
        {
            int unit = (1 << CompressionUnit) * _clusterSize;
            for (int at = 0; at < content.Length; at += unit)
            {
                byte[] chunk = content.AsSpan(at, Math.Min(unit, content.Length - at)).ToArray();
                byte[] packed = Lznt1.Compress(chunk);
                long clusters = (packed.Length + _clusterSize - 1) / _clusterSize;
                if (clusters >= 1 << CompressionUnit || chunk.Length < unit)
                {
                    // 圧縮しても小さくならない単位はそのまま置く。
                    long n = (chunk.Length + _clusterSize - 1) / _clusterSize;
                    long lcn = Allocate(n);
                    WriteClusters(lcn, chunk);
                    stream.Runs.Add((n, lcn));
                    if (n < 1 << CompressionUnit)
                    {
                        stream.Runs.Add(((1 << CompressionUnit) - n, null));
                    }
                }
                else
                {
                    long lcn = Allocate(clusters);
                    WriteClusters(lcn, packed);
                    stream.Runs.Add((clusters, lcn));
                    stream.Runs.Add(((1 << CompressionUnit) - clusters, null));
                }
            }

            return stream;
        }

        long total = Math.Max(1, (stored.Length + _clusterSize - 1) / _clusterSize);
        long per = (total + fragments - 1) / fragments;
        long done = 0;
        for (int f = 0; f < fragments && done < total; f++)
        {
            if (f > 0)
            {
                Skip(gapClusters);
            }

            long n = Math.Min(per, total - done);
            long lcn = Allocate(n);
            int from = (int)(done * _clusterSize);
            WriteClusters(lcn, stored.AsSpan(from, (int)Math.Min(n * _clusterSize, stored.Length - from)).ToArray());
            stream.Runs.Add((n, lcn));
            done += n;
        }

        return stream;
    }

    /// <summary>指定した LCN のクラスタにデータを書く (既存のファイルのクラスタを上書きするファイル)。</summary>
    public Node AddFileAt(Node parent, string name, byte[] content, DateTime time, long lcn)
    {
        Node node = NewNode(parent, name, false);
        node.SiTimes = Times.All(time);
        node.FnTimes = Times.All(time);
        long n = (content.Length + _clusterSize - 1) / _clusterSize;
        Claim(lcn, n);
        WriteClusters(lcn, content);
        var stream = new Stream { Content = content };
        stream.Runs.Add((n, lcn));
        node.Streams.Add(stream);
        return node;
    }

    private void WriteClusters(long lcn, byte[] data) => _raw.Add((lcn * _clusterSize, data));

    /// <summary>削除: レコードの使用中フラグを 0 にし、シーケンス番号を増やし、クラスタを解放する。親のインデックスから外す。</summary>
    public void Delete(Node node, bool leaveInIndexSlack = false)
    {
        node.InUse = false;
        node.Sequence++;
        node.Unlinked = true;
        node.LeaveInIndexSlack = leaveInIndexSlack;
        foreach (Stream s in node.Streams)
        {
            Free(s);
        }
    }

    // ---- 書き出し ----

    private static void W16(Span<byte> b, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b[at..], (ushort)v);

    private static void W32(Span<byte> b, int at, long v) => BinaryPrimitives.WriteUInt32LittleEndian(b[at..], (uint)v);

    private static void W64(Span<byte> b, int at, long v) => BinaryPrimitives.WriteInt64LittleEndian(b[at..], v);

    private static long Ref(Node n, bool sequenceAfterDelete = false) => n.Number | ((long)(n.InUse || sequenceAfterDelete ? n.Sequence : n.Sequence - 1) << 48);

    /// <summary>更新シーケンス配列を書いてフィックスアップを当てる。</summary>
    public static void ApplyFixup(Span<byte> record, int usaOffset, ushort usn)
    {
        int sectors = record.Length / SectorSize;
        W16(record, 4, usaOffset);
        W16(record, 6, sectors + 1);
        W16(record, usaOffset, usn);
        for (int i = 1; i <= sectors; i++)
        {
            int end = i * SectorSize - 2;
            record[usaOffset + i * 2] = record[end];
            record[usaOffset + i * 2 + 1] = record[end + 1];
            W16(record, end, usn);
        }
    }

    /// <summary>$FILE_NAME の値。</summary>
    private byte[] FileNameValue(Node node, string name, byte nameSpace, bool deletedParentRef = false)
    {
        byte[] v = new byte[66 + name.Length * 2];
        Node parent = node.Parent ?? node;
        W64(v, 0, parent.Number | ((long)parent.Sequence << 48));
        W64(v, 8, Times.FileTime(node.FnTimes.Created));
        W64(v, 16, Times.FileTime(node.FnTimes.Modified));
        W64(v, 24, Times.FileTime(node.FnTimes.MftModified));
        W64(v, 32, Times.FileTime(node.FnTimes.Accessed));
        Stream? data = node.Data;
        long size = node.IsDirectory || data is null ? 0 : data.Content.Length;
        long allocated = node.IsDirectory || data is null || data.Resident ? (size + 7) & ~7 : data.AllocatedClusters * _clusterSize;
        W64(v, 40, allocated);
        W64(v, 48, size);
        W32(v, 56, node.Attributes | (node.IsDirectory ? 0x10000000u : 0));
        v[64] = (byte)name.Length;
        v[65] = nameSpace;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 66);
        return v;
    }

    private static byte[] StandardInformation(Node node)
    {
        byte[] v = new byte[72];
        W64(v, 0, Times.FileTime(node.SiTimes.Created));
        W64(v, 8, Times.FileTime(node.SiTimes.Modified));
        W64(v, 16, Times.FileTime(node.SiTimes.MftModified));
        W64(v, 24, Times.FileTime(node.SiTimes.Accessed));
        W32(v, 32, node.Attributes);
        W32(v, 52, 0x100);
        return v;
    }

    private static byte[] ResidentAttribute(uint type, byte[] value, string name, ushort id, ushort flags = 0)
    {
        int nameBytes = name.Length * 2;
        int valueOffset = (0x18 + nameBytes + 7) & ~7;
        int length = (valueOffset + value.Length + 7) & ~7;
        byte[] a = new byte[length];
        W32(a, 0, type);
        W32(a, 4, length);
        a[8] = 0;
        a[9] = (byte)name.Length;
        W16(a, 10, 0x18);
        W16(a, 12, flags);
        W16(a, 14, id);
        W32(a, 16, value.Length);
        W16(a, 20, valueOffset);
        a[22] = type == 0x30 ? (byte)1 : (byte)0;
        Encoding.Unicode.GetBytes(name).CopyTo(a, 0x18);
        value.CopyTo(a, valueOffset);
        return a;
    }

    /// <summary>データランを符号化する。</summary>
    public static byte[] EncodeRuns(IEnumerable<(long Length, long? Lcn)> runs)
    {
        var b = new List<byte>();
        long previous = 0;
        foreach ((long length, long? lcn) in runs)
        {
            byte[] len = Signed(length);
            if (lcn is long l)
            {
                byte[] off = Signed(l - previous);
                previous = l;
                b.Add((byte)((off.Length << 4) | len.Length));
                b.AddRange(len);
                b.AddRange(off);
            }
            else
            {
                b.Add((byte)len.Length);
                b.AddRange(len);
            }
        }

        b.Add(0);
        return [.. b];
    }

    private static byte[] Signed(long v)
    {
        for (int n = 1; n <= 8; n++)
        {
            long min = -(1L << (n * 8 - 1)), max = (1L << (n * 8 - 1)) - 1;
            if (n == 8 || v >= min && v <= max)
            {
                byte[] r = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    r[i] = (byte)(v >> (i * 8));
                }

                return r;
            }
        }

        throw new InvalidOperationException();
    }

    private byte[] NonResidentAttribute(uint type, string name, Stream s, ushort id, long realSize)
    {
        bool compressed = s.Compressed;
        int header = compressed ? 0x48 : 0x40;
        int nameBytes = name.Length * 2;
        int runOffset = (header + nameBytes + 7) & ~7;
        byte[] runs = EncodeRuns(s.Runs);
        int length = (runOffset + runs.Length + 7) & ~7;
        byte[] a = new byte[length];
        long clusters = s.AllocatedClusters;
        W32(a, 0, type);
        W32(a, 4, length);
        a[8] = 1;
        a[9] = (byte)name.Length;
        W16(a, 10, header);
        W16(a, 12, compressed ? 0x0001 : 0);
        W16(a, 14, id);
        W64(a, 0x10, 0);
        W64(a, 0x18, clusters - 1);
        W16(a, 0x20, runOffset);
        W16(a, 0x22, compressed ? CompressionUnit : 0);
        W64(a, 0x28, clusters * _clusterSize);
        W64(a, 0x30, realSize);
        W64(a, 0x38, realSize);
        if (compressed)
        {
            W64(a, 0x40, s.Runs.Where(r => r.Lcn is not null).Sum(r => r.Length) * _clusterSize);
        }

        Encoding.Unicode.GetBytes(name).CopyTo(a, header);
        runs.CopyTo(a, runOffset);
        return a;
    }

    // ---- インデックス ----

    private sealed record IndexKey(string Name, byte[] Entry);

    private byte[] IndexEntry(Node child, string name, byte nameSpace)
    {
        byte[] key = FileNameValue(child, name, nameSpace);
        int length = (16 + key.Length + 7) & ~7;
        byte[] e = new byte[length];
        W64(e, 0, Ref(child));
        W16(e, 8, length);
        W16(e, 10, key.Length);
        key.CopyTo(e, 16);
        return e;
    }

    private static byte[] WithSubnode(byte[] entry, long vcn)
    {
        byte[] e = new byte[entry.Length + 8];
        entry.CopyTo(e, 0);
        W16(e, 8, e.Length);
        W16(e, 12, BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(12)) | 1);
        W64(e, e.Length - 8, vcn);
        return e;
    }

    private static byte[] EndEntry(long? subnode)
    {
        byte[] e = new byte[subnode is null ? 16 : 24];
        W16(e, 8, e.Length);
        W16(e, 12, subnode is null ? 2 : 3);
        if (subnode is long v)
        {
            W64(e, 16, v);
        }

        return e;
    }

    private static int CompareNames(string a, string b) => string.CompareOrdinal(a.ToUpperInvariant(), b.ToUpperInvariant());

    private sealed class IndexBuild
    {
        public List<byte[]> Blocks { get; } = [];
        public List<byte[]> RootEntries { get; } = [];
        public bool Large { get; set; }
    }

    /// <summary>
    /// ディレクトリのインデックスを B 木として作る (葉を詰めて作り、区切りのキーを上の段へ上げる)。小さければ常駐の $INDEX_ROOT だけにする。
    /// </summary>
    private IndexBuild BuildIndex(Node dir, long firstBlockLcn, out List<long> blockLcns)
    {
        var keys = new List<IndexKey>();
        foreach (Node c in dir.Children.Where(c => !c.Unlinked))
        {
            keys.Add(new IndexKey(c.Name, IndexEntry(c, c.Name, c.DosName is null ? (c.SystemFile ? (byte)3 : (byte)1) : (byte)1)));
            if (c.DosName is { } dos)
            {
                keys.Add(new IndexKey(dos, IndexEntry(c, dos, 2)));
            }
        }

        if (dir == Root)
        {
            keys.Add(new IndexKey(".", IndexEntry(dir, ".", 3)));
        }

        keys.Sort((a, b) => CompareNames(a.Name, b.Name));
        var build = new IndexBuild();
        blockLcns = [];
        if (keys.Sum(k => k.Entry.Length) <= 256)
        {
            build.RootEntries.AddRange(keys.Select(k => k.Entry));
            build.RootEntries.Add(EndEntry(null));
            return build;
        }

        build.Large = true;
        const int Capacity = IndexBlockSize - 0x40 - 24;

        // 下の段から詰める。各項目は (エントリ、左の子のブロック)。段の終わりのエントリの子は lastChild。
        var level = keys.Select(k => (Entry: k.Entry, Child: (long?)null)).ToList();
        long? lastChild = null;
        long vcn = 0;
        var blocks = new List<(long Vcn, List<byte[]> Entries, long? EndChild)>();
        while (true)
        {
            var parents = new List<(byte[] Entry, long? Child)>();
            var current = new List<byte[]>();
            int used = 0;
            foreach ((byte[] entry, long? child) in level)
            {
                byte[] e = child is long ch ? WithSubnode(entry, ch) : entry;
                if (used + e.Length > Capacity && current.Count > 0)
                {
                    // この項目を区切りとして上の段へ上げる。今のブロックの終わりのエントリはこの項目の左の子を指す。
                    blocks.Add((vcn, current, child));
                    parents.Add((entry, vcn));
                    vcn++;
                    current = [];
                    used = 0;
                    continue;
                }

                current.Add(e);
                used += e.Length;
            }

            blocks.Add((vcn, current, lastChild));
            long last = vcn++;
            if (parents.Count == 0)
            {
                build.RootEntries.Add(EndEntry(last));
                break;
            }

            if (parents.Sum(p => p.Entry.Length + 8) <= 256)
            {
                foreach ((byte[] entry, long? child) in parents)
                {
                    build.RootEntries.Add(WithSubnode(entry, child!.Value));
                }

                build.RootEntries.Add(EndEntry(last));
                break;
            }

            level = parents;
            lastChild = last;
        }

        foreach ((long blockVcn, List<byte[]> entries, long? endChild) in blocks.OrderBy(b => b.Vcn))
        {
            byte[] block = new byte[IndexBlockSize];
            "INDX"u8.CopyTo(block);
            W64(block, 0x10, blockVcn);
            int at = 0x40;
            bool hasChildren = entries.Any(e => (BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(12)) & 1) != 0) || endChild is not null;
            foreach (byte[] e in entries)
            {
                e.CopyTo(block, at);
                at += e.Length;
            }

            byte[] end = EndEntry(endChild);
            end.CopyTo(block, at);
            at += end.Length;
            W32(block, 0x18, 0x28);
            W32(block, 0x1C, at - 0x18);
            W32(block, 0x20, IndexBlockSize - 0x18);
            W32(block, 0x24, hasChildren ? 1 : 0);
            build.Blocks.Add(block);
        }

        for (int i = 0; i < build.Blocks.Count; i++)
        {
            blockLcns.Add(firstBlockLcn + i);
        }

        return build;
    }

    private static byte[] IndexRootValue(IndexBuild build)
    {
        int entries = build.RootEntries.Sum(e => e.Length);
        byte[] v = new byte[0x20 + entries];
        W32(v, 0, 0x30);
        W32(v, 4, 1);
        W32(v, 8, IndexBlockSize);
        v[12] = 1;
        W32(v, 0x10, 0x10);
        W32(v, 0x14, 0x10 + entries);
        W32(v, 0x18, 0x10 + entries);
        v[0x1C] = build.Large ? (byte)1 : (byte)0;
        int at = 0x20;
        foreach (byte[] e in build.RootEntries)
        {
            e.CopyTo(v, at);
            at += e.Length;
        }

        return v;
    }

    // ---- レコード ----

    private byte[] Record(Node node, IndexBuild? index, List<long>? indexLcns)
    {
        byte[] r = new byte[RecordSize];
        "FILE"u8.CopyTo(r);
        W64(r, 8, 0x100000 + node.Number * 0x100);
        W16(r, 16, node.Sequence);
        W16(r, 18, node.DosName is null ? 1 : 2);
        W16(r, 20, 0x38);
        W16(r, 22, (node.InUse ? 1 : 0) | (node.IsDirectory ? 2 : 0));
        W32(r, 28, RecordSize);
        W32(r, 0x2C, node.Number);
        ushort id = 0;
        var attributes = new List<byte[]> { ResidentAttribute(0x10, StandardInformation(node), string.Empty, id++) };
        if (node.Number != 5 || true)
        {
            string name = node.Number == 5 ? "." : node.Name;
            attributes.Add(ResidentAttribute(0x30, FileNameValue(node, name, node.DosName is null ? (node.SystemFile ? (byte)3 : (byte)1) : (byte)1),
                string.Empty, id++));
            if (node.DosName is { } dos)
            {
                attributes.Add(ResidentAttribute(0x30, FileNameValue(node, dos, 2), string.Empty, id++));
            }
        }

        foreach ((uint type, byte[] value, string name) in node.ExtraResident.Where(x => x.Type < 0x80))
        {
            attributes.Add(ResidentAttribute(type, value, name, id++));
        }

        foreach (Stream s in node.Streams)
        {
            attributes.Add(s.Resident
                ? ResidentAttribute(0x80, s.Content, s.Name, id++)
                : NonResidentAttribute(0x80, s.Name, s, id++, s.Name == "$Bad" ? 0 : s.Content.Length == 0 && s.Runs.Count > 0 ? s.AllocatedClusters * _clusterSize : s.Content.Length));
        }

        if (node.IsDirectory && index is not null)
        {
            attributes.Add(ResidentAttribute(0x90, IndexRootValue(index), "$I30", id++));
            if (index.Large && indexLcns is { Count: > 0 })
            {
                var stream = new Stream();
                foreach (long lcn in indexLcns)
                {
                    if (stream.Runs.Count > 0 && stream.Runs[^1].Lcn + stream.Runs[^1].Length == lcn)
                    {
                        stream.Runs[^1] = (stream.Runs[^1].Length + 1, stream.Runs[^1].Lcn);
                    }
                    else
                    {
                        stream.Runs.Add((1, lcn));
                    }
                }

                attributes.Add(NonResidentAttribute(0xA0, "$I30", stream, id++, indexLcns.Count * (long)IndexBlockSize));
                byte[] bitmap = new byte[Math.Max(8, (indexLcns.Count + 63) / 64 * 8)];
                for (int i = 0; i < indexLcns.Count; i++)
                {
                    bitmap[i / 8] |= (byte)(1 << (i % 8));
                }

                if (bitmap.Length <= 256)
                {
                    attributes.Add(ResidentAttribute(0xB0, bitmap, "$I30", id++));
                }
                else
                {
                    long lcn = Allocate((bitmap.Length + _clusterSize - 1) / _clusterSize);
                    WriteClusters(lcn, bitmap);
                    var bs = new Stream { Content = bitmap };
                    bs.Runs.Add(((bitmap.Length + _clusterSize - 1) / _clusterSize, lcn));
                    attributes.Add(NonResidentAttribute(0xB0, "$I30", bs, id++, bitmap.Length));
                }
            }
        }

        if (node.Number == 0)
        {
            // $MFT の $BITMAP (使用中のレコード)。
            byte[] bitmap = new byte[(MftRecords + 63) / 64 * 8];
            foreach (Node n in _nodes.Where(n => n.InUse))
            {
                bitmap[n.Number / 8] |= (byte)(1 << (int)(n.Number % 8));
            }

            for (int i = 12; i < 16; i++)
            {
                bitmap[i / 8] |= (byte)(1 << (i % 8));
            }

            if (bitmap.Length <= 256)
            {
                attributes.Add(ResidentAttribute(0xB0, bitmap, string.Empty, id++));
            }
            else
            {
                long clusters = (bitmap.Length + _clusterSize - 1) / _clusterSize;
                long lcn = _mftBitmapLcn ??= Allocate(clusters);
                WriteClusters(lcn, bitmap);
                var bs = new Stream { Content = bitmap };
                bs.Runs.Add((clusters, lcn));
                attributes.Add(NonResidentAttribute(0xB0, string.Empty, bs, id++, bitmap.Length));
            }
        }

        int at = 0x38;
        foreach (byte[] a in attributes)
        {
            if (at + a.Length + 8 > RecordSize)
            {
                throw new InvalidOperationException($"record {node.Number} ({node.Name}) does not fit");
            }

            a.CopyTo(r, at);
            at += a.Length;
        }

        W32(r, at, 0xFFFFFFFF);
        at += 8;
        W32(r, 24, at);
        W16(r, 40, id);
        ApplyFixup(r, 0x30, (ushort)(node.Number % 0xFFFE + 1));
        return r;
    }

    private long? _mftBitmapLcn;

    /// <summary>書き込み先にすべてを書く。</summary>
    public void Build(IImageTarget target, Action<int, byte[]>? editRecord = null, Action<Node, byte[]>? editIndexBlock = null)
    {
        // ディレクトリのインデックスの INDX ブロックを割り当てる (深い順でなく作った順)。
        var indexes = new Dictionary<Node, (IndexBuild Build, List<long> Lcns)>();
        foreach (Node dir in _nodes.Where(n => n.IsDirectory && n.InUse).ToList())
        {
            IndexBuild probe = BuildIndex(dir, 0, out _);
            long first = probe.Blocks.Count > 0 ? Allocate(probe.Blocks.Count) : 0;
            IndexBuild build = BuildIndex(dir, first, out List<long> lcns);

            // 削除してインデックススラックに残すエントリ: 最後のブロックの使用領域の後ろに書く。
            foreach (Node gone in dir.Children.Where(c => c.Unlinked && c.LeaveInIndexSlack))
            {
                if (build.Blocks.Count == 0)
                {
                    continue;
                }

                byte[] block = build.Blocks[^1];
                int used = (int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x1C)) + 0x18;
                byte[] entry = IndexEntry(gone, gone.Name, 1);
                W64(entry, 0, gone.Number | ((long)(gone.Sequence - 1) << 48));
                entry.CopyTo(block, used);
            }

            indexes[dir] = (build, lcns);
        }

        foreach (Node n in _nodes)
        {
            if (n.Data is { Resident: false } s && s.Runs.Count == 0 && n.Number >= 24)
            {
                throw new InvalidOperationException($"{n.Name} has no clusters");
            }
        }

        // MFT レコード (システムファイルの 0〜15 と 24 以降。16〜23 は空)。
        byte[] empty = new byte[RecordSize];
        "FILE"u8.CopyTo(empty);
        W16(empty, 20, 0x38);
        W32(empty, 24, 0x40);
        W32(empty, 28, RecordSize);
        W32(empty, 0x38, 0xFFFFFFFF);
        var mirror = new byte[4 * RecordSize];
        foreach (Node n in _nodes.OrderBy(n => n.Number))
        {
            (IndexBuild Build, List<long> Lcns)? index = indexes.TryGetValue(n, out var ix) ? ix : null;
            byte[] r = Record(n, index?.Build, index?.Lcns);
            editRecord?.Invoke((int)n.Number, r);
            target.Write(RecordOffset(n.Number), r);
            if (n.Number < 4)
            {
                r.CopyTo(mirror, (int)n.Number * RecordSize);
            }
        }

        for (int i = 12; i < 16; i++)
        {
            byte[] r = (byte[])empty.Clone();
            W32(r, 0x2C, i);
            W16(r, 22, 1);
            ApplyFixup(r, 0x30, 1);
            target.Write(RecordOffset(i), r);
        }

        target.Write(MirrorLcn * _clusterSize, mirror);

        foreach ((Node dir, (IndexBuild build, List<long> lcns)) in indexes)
        {
            for (int i = 0; i < build.Blocks.Count; i++)
            {
                byte[] block = build.Blocks[i];
                editIndexBlock?.Invoke(dir, block);
                ApplyFixup(block, 0x28, 1);
                target.Write(lcns[i] * _clusterSize, block);
            }
        }

        foreach ((long offset, byte[] data) in _raw)
        {
            target.Write(offset, data);
        }

        // $UpCase (65,536 文字の大文字の表)。
        byte[] upcase = new byte[65536 * 2];
        for (int c = 0; c < 65536; c++)
        {
            W16(upcase, c * 2, char.ToUpperInvariant((char)c));
        }

        target.Write(UpcaseLcn * _clusterSize, upcase);
        target.Write(LogLcn * _clusterSize, LogFile());

        // $Bitmap。
        byte[] bitmap = new byte[BitmapClusters * _clusterSize];
        for (long i = 0; i < _clusters; i++)
        {
            if (_bitmap[i])
            {
                bitmap[i / 8] |= (byte)(1 << (int)(i % 8));
            }
        }

        // ボリュームの終わりより後ろのビット (存在しないクラスタ) は使用中にする。
        for (long i = _clusters; i < bitmap.Length * 8L; i++)
        {
            bitmap[i / 8] |= (byte)(1 << (int)(i % 8));
        }

        target.Write(BitmapLcn * _clusterSize, bitmap);

        byte[] boot = BootSector();
        target.Write(0, boot);
        target.Write(TotalSectors * SectorSize, boot);
    }

    /// <summary>空きクラスタの数 (TC-FOR-16-04 の期待値)。</summary>
    public long FreeClusters => _bitmap.LongCount(b => !b);

    public bool IsAllocated(long lcn) => _bitmap[lcn];

    private byte[] BootSector()
    {
        byte[] b = new byte[512];
        b[0] = 0xEB; b[1] = 0x52; b[2] = 0x90;
        "NTFS    "u8.CopyTo(b.AsSpan(3));
        W16(b, 11, SectorSize);
        b[13] = (byte)(_clusterSize / SectorSize);
        b[21] = 0xF8;
        W16(b, 24, 63);
        W16(b, 26, 255);
        W32(b, 28, 0);
        W32(b, 36, 0x00800080);
        W64(b, 0x28, TotalSectors);
        W64(b, 0x30, MftLcn);
        W64(b, 0x38, MirrorLcn);
        b[0x40] = 0xF6; // 2^10 = 1024 バイトのレコード
        b[0x44] = 0x01; // 1 クラスタの INDX
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0x48), _serial);
        b[510] = 0x55;
        b[511] = 0xAA;
        return b;
    }

    /// <summary>$LogFile の記録 (LSN、操作) の定義 (TC-FOR-10-06 の期待値)。</summary>
    public static readonly (ushort Redo, ushort Undo)[] LogOperations =
    [
        (0x02, 0x00), (0x07, 0x07), (0x15, 0x16), (0x0E, 0x0F), (0x00, 0x00), (0x0B, 0x0B), (0x02, 0x03), (0x1B, 0x00),
        (0x07, 0x07), (0x15, 0x16), (0x0C, 0x0D), (0x00, 0x00),
    ];

    public const long FirstLsn = 0x100000;
    public const long CurrentLsn = 0x100400;

    /// <summary>$LogFile: リスタートページ 2 枚と、ログレコードを入れたレコードページ。</summary>
    private byte[] LogFile()
    {
        byte[] log = new byte[64 * _clusterSize];
        for (int page = 0; page < 2; page++)
        {
            byte[] p = new byte[4096];
            "RSTR"u8.CopyTo(p);
            W32(p, 16, 4096);
            W32(p, 20, 4096);
            W16(p, 24, 0x30);
            W16(p, 26, 1);
            W16(p, 28, 1);
            int a = 0x30;
            W64(p, a, CurrentLsn);
            W16(p, a + 8, 1);
            W16(p, a + 10, 0xFFFF);
            W16(p, a + 12, 0);
            W32(p, a + 16, 0x2C);
            W16(p, a + 20, 0xD0);
            W16(p, a + 22, 0x30);
            W64(p, a + 24, log.Length);
            W16(p, a + 36, 0x30);
            W16(p, a + 38, 0x40);
            int c = a + 0x30;
            W64(p, c, FirstLsn);
            W64(p, c + 8, CurrentLsn);
            W16(p, c + 16, 0xFFFF);
            W16(p, c + 18, 0xFFFF);
            W32(p, c + 0x20, 8);
            Encoding.Unicode.GetBytes("NTFS").CopyTo(p, c + 0x24);
            ApplyFixup(p, 0x1E, 1);
            p.CopyTo(log, page * 4096);
        }

        // レコードページ (0x2000 から)。1 ページに 4 件。
        long lsn = FirstLsn;
        int index = 0;
        for (int page = 0; index < LogOperations.Length; page++)
        {
            byte[] p = new byte[4096];
            "RCRD"u8.CopyTo(p);
            int at = 0x40;
            long lastLsn = 0;
            for (int k = 0; k < 4 && index < LogOperations.Length; k++, index++)
            {
                (ushort redo, ushort undo) = LogOperations[index];
                const int ClientLength = 0x28;
                W64(p, at, lsn);
                W64(p, at + 8, index == 0 ? 0 : lsn - 0x40);
                W64(p, at + 16, index == 0 ? 0 : lsn - 0x40);
                W32(p, at + 24, ClientLength);
                W32(p, at + 28, 0);
                W32(p, at + 32, 1);
                W32(p, at + 36, 0x18 + index % 3 * 0x18);
                int d = at + 0x30;
                W16(p, d, redo);
                W16(p, d + 2, undo);
                W16(p, d + 4, 0x28);
                W16(p, d + 6, 0);
                W16(p, d + 8, 0x28);
                W16(p, d + 10, 0);
                W16(p, d + 12, 0);
                W16(p, d + 14, 1);
                W16(p, d + 16, 0x38);
                W16(p, d + 18, 0);
                W16(p, d + 20, (index % 4) * 2);
                W64(p, d + 24, 0);
                W64(p, d + 32, MftLcn + index % 4);
                lastLsn = lsn;
                at += (0x30 + ClientLength + 7) & ~7;
                lsn += 0x40;
            }

            W64(p, 8, lastLsn);
            W16(p, 20, 1);
            W16(p, 22, 1);
            W16(p, 24, at);
            W64(p, 32, lastLsn);
            ApplyFixup(p, 0x28, 1);
            p.CopyTo(log, 0x2000 + page * 4096);
        }

        return log;
    }
}

/// <summary>LZNT1 の圧縮 (Windows の RtlCompressBuffer)。テストデータの作成にだけ使う。</summary>
public static class Lznt1
{
    private const ushort FormatLznt1 = 2;

    [DllImport("ntdll.dll")]
    private static extern uint RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkSpace, out uint fragmentWorkSpace);

    [DllImport("ntdll.dll")]
    private static extern uint RtlCompressBuffer(ushort format, byte[] source, uint sourceSize, byte[] destination, uint destinationSize, uint chunkSize,
        out uint finalSize, IntPtr workSpace);

    [DllImport("ntdll.dll")]
    private static extern uint RtlDecompressBuffer(ushort format, byte[] destination, uint destinationSize, byte[] source, uint sourceSize,
        out uint finalSize);

    public static byte[] Compress(byte[] data)
    {
        if (RtlGetCompressionWorkSpaceSize(FormatLznt1, out uint workspace, out _) != 0)
        {
            throw new InvalidOperationException("RtlGetCompressionWorkSpaceSize failed");
        }

        IntPtr ws = Marshal.AllocHGlobal((int)workspace);
        try
        {
            byte[] output = new byte[data.Length + data.Length / 8 + 4096];
            uint status = RtlCompressBuffer(FormatLznt1, data, (uint)data.Length, output, (uint)output.Length, 4096, out uint size, ws);
            if (status != 0)
            {
                throw new InvalidOperationException($"RtlCompressBuffer failed: 0x{status:X8}");
            }

            return output.AsSpan(0, (int)size).ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(ws);
        }
    }

    /// <summary>Windows の展開 (アプリの展開の結果と比べるため)。</summary>
    public static byte[] Decompress(byte[] data, int outputLength)
    {
        byte[] output = new byte[outputLength];
        uint status = RtlDecompressBuffer(FormatLznt1, output, (uint)outputLength, data, (uint)data.Length, out uint size);
        if (status != 0)
        {
            throw new InvalidOperationException($"RtlDecompressBuffer failed: 0x{status:X8}");
        }

        return output.AsSpan(0, (int)size).ToArray();
    }
}
