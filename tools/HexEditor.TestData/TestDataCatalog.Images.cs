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
    ];

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
