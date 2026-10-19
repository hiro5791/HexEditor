using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData;

// cases/12-forensics.md の表のうち、イメージ・RAID の領域 (FOR-20〜26) のテストデータ。構造は生成ツールが直接書く (毎回同じ内容)。
public static partial class TestDataCatalog
{
    /// <summary>TD-FOR-RAID-SRC の大きさ (128 MiB)、パーティション (LBA 2,048〜末尾 − 34)、乱数の種。</summary>
    public const long RaidSourceLength = 128 * MiB;

    public const long RaidPartitionFirstLba = 2048;

    public const ulong RaidRandomSeed = 0x5A1D;

    /// <summary>RAID のストライプサイズ (64 KiB)。</summary>
    public const long RaidStripe = 64 * KiB;

    private static long RaidLastLba => RaidSourceLength / 512 - 34;

    private static IEnumerable<TestDataItem> ImageItems() =>
    [
        new("TD-FOR-RAID-SRC", RaidSourceLength, "GPT で Windows 基本データのパーティション 1 つ。パーティションの中は種 0x5A1D の乱数",
            path => WriteGenerated(path, RaidSourceLength, RaidSource)),
        new("TD-FOR-RAID5-M0", RaidSourceLength / 2, "TD-FOR-RAID-SRC の RAID 5 (3 台、左対称、64 KiB) のメンバー 0", path => WriteGenerated(path, RaidSourceLength / 2, (o, s) => Raid5Member(0, o, s))),
        new("TD-FOR-RAID5-M1", RaidSourceLength / 2, "同じくメンバー 1", path => WriteGenerated(path, RaidSourceLength / 2, (o, s) => Raid5Member(1, o, s))),
        new("TD-FOR-RAID5-M2", RaidSourceLength / 2, "同じくメンバー 2", path => WriteGenerated(path, RaidSourceLength / 2, (o, s) => Raid5Member(2, o, s))),
        new("TD-FOR-RAID0-M0", RaidSourceLength / 2, "TD-FOR-RAID-SRC の RAID 0 (2 台、64 KiB) のメンバー 0", path => WriteGenerated(path, RaidSourceLength / 2, (o, s) => Raid0Member(0, o, s))),
        new("TD-FOR-RAID0-M1", RaidSourceLength / 2, "同じくメンバー 1", path => WriteGenerated(path, RaidSourceLength / 2, (o, s) => Raid0Member(1, o, s))),
        new("TD-FOR-E01-1T", 0, "1 TiB の E01 (00 と 1 GiB ごとの目印。同じ内容のチャンクは 1 つのデータを共有する)", WriteE01Terabyte,
            dir => Path.Combine(dir, "TD-FOR-E01-1T", "big.E01")) { ComputeLength = () => E01TerabyteLength.Value },
    ];

    /// <summary>TD-FOR-E01-1T の論理サイズ。</summary>
    public const long E01TerabyteMediaSize = TiB;

    /// <summary>TD-FOR-E01-1T の目印の位置 (1 GiB ごと。2^31・2^32 を含む)。末尾の 4 KiB は 00。</summary>
    public static IEnumerable<long> E01TerabyteMarkers()
    {
        for (long p = 0; p < TiB; p += GiB)
        {
            yield return p;
        }
    }

    private static readonly Lazy<long> E01TerabyteLength = new(() =>
    {
        // ファイルの長さは内容から決まる (生成と同じ計算を、書かずに行う)。
        using var counter = new CountingStream();
        WriteE01Terabyte(counter);
        return counter.Length;
    });

    private static void WriteE01Terabyte(string path)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteE01Terabyte(file);
    }

    /// <summary>
    /// TD-FOR-E01-1T: 1 TiB の E01 を 1 つのセグメントに書く (EnCase 6 の形。header・volume・sectors・table・hash・done)。
    /// チャンク (32 KiB) は 3,355 万個あり、0 だけのチャンクは各 sectors セクションの中の 1 つの圧縮データを指す (table のエントリが同じ位置を指す)。
    /// 目印のあるチャンクは別に圧縮して置く。開く処理の時間は table の数で決まり、実際の 1 TiB の E01 と同じ。
    /// </summary>
    private static void WriteE01Terabyte(Stream s)
    {
        const int Chunk = 32 * 1024;
        const int PerTable = 16375;
        long chunks = TiB / Chunk;
        byte[] zero = ZlibChunk(new byte[Chunk]);

        static byte[] Descriptor(string type, long next, long size)
        {
            byte[] d = new byte[76];
            Encoding.ASCII.GetBytes(type).CopyTo(d, 0);
            BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(16), next);
            BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(24), size);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(72), Adler32(d.AsSpan(0, 72)));
            return d;
        }

        void Section(string type, byte[] body)
        {
            long at = s.Position;
            s.Write(Descriptor(type, at + 76 + body.Length, 76 + body.Length));
            s.Write(body);
        }

        s.Write([0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00]);
        Section("header", ZlibChunk(Encoding.ASCII.GetBytes("1\nmain\nc\tn\ta\te\tt\tav\tov\tm\tu\tp\tr\nC-1T\tE-1T\t1 TiB test\ttester\t\tTestDataGen\tWindows\t2026 1 1 0 0 0\t2026 1 1 0 0 0\t0\tb\n\n")));
        byte[] volume = new byte[1052];
        volume[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(4), (uint)chunks);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(8), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(12), 512);
        BinaryPrimitives.WriteInt64LittleEndian(volume.AsSpan(16), TiB / 512);
        volume[52] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(1048), Adler32(volume.AsSpan(0, 1048)));
        Section("volume", volume);

        var markerChunks = E01TerabyteMarkers().Select(p => p / Chunk).ToHashSet();
        byte[] chunkData = new byte[Chunk];
        for (long first = 0; first < chunks; first += PerTable)
        {
            int count = (int)Math.Min(PerTable, chunks - first);
            long sectorsAt = s.Position;
            s.Write(new byte[76]);
            long dataStart = s.Position;
            s.Write(zero);
            uint[] entries = new uint[count];
            for (int i = 0; i < count; i++)
            {
                long c = first + i;
                if (markerChunks.Contains(c))
                {
                    Array.Clear(chunkData);
                    Marker(c * Chunk).CopyTo(chunkData, 0);
                    entries[i] = 0x80000000u | (uint)(s.Position - dataStart);
                    s.Write(ZlibChunk(chunkData));
                }
                else
                {
                    entries[i] = 0x80000000u;
                }
            }

            long end = s.Position;
            s.Position = sectorsAt;
            s.Write(Descriptor("sectors", end, end - sectorsAt));
            s.Position = end;
            byte[] table = new byte[24 + count * 4 + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)count);
            BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(8), dataStart);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(20), Adler32(table.AsSpan(0, 20)));
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24 + i * 4), entries[i]);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(24 + count * 4), Adler32(table.AsSpan(24, count * 4)));
            Section("table", table);
        }

        long done = s.Position;
        s.Write(Descriptor("done", done, 0));
    }

    private static byte[] ZlibChunk(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>書かずに長さだけを数えるストリーム (TD-FOR-E01-1T の長さの計算)。</summary>
    private sealed class CountingStream : Stream
    {
        private long _position;
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };

        public override void SetLength(long value) => _length = value;

        public override void Write(byte[] buffer, int offset, int count)
        {
            _position += count;
            _length = Math.Max(_length, _position);
        }
    }

    private static readonly Lazy<(byte[] Head, byte[] Tail)> RaidGpt = new(() =>
    {
        long sectors = RaidSourceLength / 512;
        byte[] head = new byte[34 * 512];
        byte[] tail = new byte[33 * 512];
        head[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(446 + 12), (uint)(sectors - 1));
        head[510] = 0x55;
        head[511] = 0xAA;
        byte[] entries = new byte[128 * 128];
        new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").TryWriteBytes(entries);
        new Guid("00000000-0000-0000-0000-000000000001").TryWriteBytes(entries.AsSpan(16));
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(32), RaidPartitionFirstLba);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(40), RaidLastLba);
        Encoding.Unicode.GetBytes("RAID").CopyTo(entries, 56);
        uint entriesCrc = Crc32(entries);
        byte[] Header(long self, long backup, long entriesLba)
        {
            byte[] h = new byte[512];
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), self);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), backup);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), sectors - 34);
            new Guid("00000000-0000-0000-0000-0000000000D0").TryWriteBytes(h.AsSpan(56));
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), entriesCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32(h.AsSpan(0, 92).ToArray()));
            return h;
        }

        Header(1, sectors - 1, 2).CopyTo(head, 512);
        entries.CopyTo(head, 1024);
        entries.CopyTo(tail, 0);
        Header(sectors - 1, 1, sectors - 33).CopyTo(tail, 32 * 512);
        return (head, tail);
    });

    /// <summary>TD-FOR-RAID-SRC の内容。</summary>
    public static void RaidSource(long offset, Span<byte> destination)
    {
        destination.Clear();
        (byte[] head, byte[] tail) = RaidGpt.Value;
        long partStart = RaidPartitionFirstLba * 512;
        long partEnd = (RaidLastLba + 1) * 512;
        long tailStart = RaidSourceLength - tail.Length;
        Copy(head, 0, offset, destination);
        Copy(tail, tailStart, offset, destination);
        long from = Math.Max(offset, partStart);
        long to = Math.Min(offset + destination.Length, partEnd);
        if (from < to)
        {
            Random(RaidRandomSeed, from - partStart, destination.Slice((int)(from - offset), (int)(to - from)));
        }
    }

    private static void Copy(byte[] data, long at, long offset, Span<byte> destination)
    {
        long from = Math.Max(offset, at);
        long to = Math.Min(offset + destination.Length, at + data.Length);
        if (from < to)
        {
            data.AsSpan((int)(from - at), (int)(to - from)).CopyTo(destination[(int)(from - offset)..]);
        }
    }

    /// <summary>RAID 0 (2 台): チャンク c はメンバー c mod 2 の行 c / 2。</summary>
    private static void Raid0Member(int member, long offset, Span<byte> destination)
    {
        for (int done = 0; done < destination.Length;)
        {
            long pos = offset + done;
            long row = pos / RaidStripe;
            long within = pos % RaidStripe;
            int n = (int)Math.Min(destination.Length - done, RaidStripe - within);
            RaidSource((row * 2 + member) * RaidStripe + within, destination.Slice(done, n));
            done += n;
        }
    }

    /// <summary>RAID 5 (3 台、左対称): 行 r のパリティは 2 − (r mod 3)、データ d はメンバー (パリティ + 1 + d) mod 3。パリティは XOR。</summary>
    private static void Raid5Member(int member, long offset, Span<byte> destination)
    {
        byte[] other = new byte[RaidStripe];
        for (int done = 0; done < destination.Length;)
        {
            long pos = offset + done;
            long row = pos / RaidStripe;
            long within = pos % RaidStripe;
            int n = (int)Math.Min(destination.Length - done, RaidStripe - within);
            Span<byte> part = destination.Slice(done, n);
            int parity = 2 - (int)(row % 3);
            if (member == parity)
            {
                part.Clear();
                for (int d = 0; d < 2; d++)
                {
                    RaidSource((row * 2 + d) * RaidStripe + within, other.AsSpan(0, n));
                    for (int i = 0; i < n; i++)
                    {
                        part[i] ^= other[i];
                    }
                }
            }
            else
            {
                int d = (member - parity - 1 + 3) % 3;
                RaidSource((row * 2 + d) * RaidStripe + within, part);
            }

            done += n;
        }
    }
}
