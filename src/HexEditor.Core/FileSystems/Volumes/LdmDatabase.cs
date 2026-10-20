using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Volumes;

/// <summary>LDM のディスクのレコード (VBLK の DSK3 / DSK4)。</summary>
public sealed record LdmDiskRecord(ulong ObjectId, string Name, Guid DiskGuid);

/// <summary>LDM のボリュームのレコード (VOL5)。<see cref="VolumeType"/> は <c>gen</c> または <c>raid5</c>。大きさはセクタ数。</summary>
public sealed record LdmVolumeRecord(ulong ObjectId, string Name, string VolumeType, ulong Children, ulong Sectors, byte PartitionType, Guid VolumeGuid, string State);

/// <summary>LDM のコンポーネントのレコード (CMP3)。種類は 1 = ストライプ、2 = 連結 (シンプル・スパン)、3 = RAID-5。チャンクはセクタ数。</summary>
public sealed record LdmComponentRecord(ulong ObjectId, string Name, int ComponentType, ulong Children, ulong ParentId, ulong ChunkSectors, ulong Columns);

/// <summary>LDM のパーティションのレコード (PRT3)。開始は論理ディスクの先頭から、ボリューム内の位置・大きさはセクタ数。</summary>
public sealed record LdmPartitionRecord(ulong ObjectId, string Name, ulong Start, ulong VolumeOffset, ulong Sectors, ulong ParentId, ulong DiskId, int? Index);

/// <summary>
/// LDM (Windows の動的ディスク) のデータベース (FOR-07 の仕様 1)。PRIVHEAD (MBR では LBA 6、GPT では LDM メタデータパーティションの最後のセクタ)、
/// TOCBLOCK、VMDB、VBLK (ディスク・ディスクグループ・ボリューム・コンポーネント・パーティション) を読む。値はビッグエンディアン、可変長の数値と文字列は
/// 先頭の 1 バイトが長さ (Linux の ldm.c と同じ読み方)。読むのはデータベースの 1 MiB だけ。壊れたレコードは飛ばす。
/// </summary>
public sealed class LdmDatabase
{
    public const int MaxRecords = 65536;
    private const int OffVmdb = 17;

    private LdmDatabase()
    {
    }

    public Guid DiskGuid { get; private init; }
    public Guid HostGuid { get; private init; }
    public Guid DiskGroupGuid { get; private init; }
    public string DiskGroupName { get; private init; } = string.Empty;
    public long LogicalDiskStart { get; private init; }
    public long LogicalDiskSize { get; private init; }
    public long ConfigStart { get; private init; }
    public long ConfigSize { get; private init; }
    public uint Sequence { get; private init; }
    public int VersionMajor { get; private init; }
    public int VersionMinor { get; private init; }
    public long PrivheadOffset { get; private init; }

    public IReadOnlyList<LdmDiskRecord> Disks { get; private init; } = [];
    public IReadOnlyList<LdmVolumeRecord> Volumes { get; private init; } = [];
    public IReadOnlyList<LdmComponentRecord> Components { get; private init; } = [];
    public IReadOnlyList<LdmPartitionRecord> Partitions { get; private init; } = [];

    /// <summary>PRIVHEAD の写し・TOCBLOCK の不一致など (訳さない詳細)。</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    /// <summary>ディスクの LDM のデータベースを読む (<paramref name="privheadLba"/> は PRIVHEAD のセクタ)。PRIVHEAD がなければ null。</summary>
    public static LdmDatabase? Read(IVolumeReader disk, long privheadLba, int sectorSize = 512)
    {
        byte[] ph = disk.ReadBytes(privheadLba * sectorSize, 512, out bool complete);
        if (!complete || !ph.AsSpan(0, 8).SequenceEqual("PRIVHEAD"u8))
        {
            return null;
        }

        int major = BinaryPrimitives.ReadUInt16BigEndian(ph.AsSpan(0x0C));
        int minor = BinaryPrimitives.ReadUInt16BigEndian(ph.AsSpan(0x0E));
        long ldStart = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(ph.AsSpan(0x11B)), long.MaxValue / 4096);
        long ldSize = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(ph.AsSpan(0x123)), long.MaxValue / 4096);
        long configStart = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(ph.AsSpan(0x12B)), long.MaxValue / 4096);
        long configSize = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(ph.AsSpan(0x133)), 1 << 20);
        if (!TryGuid(ph.AsSpan(0x30, 64), out Guid diskGuid) || configStart <= 0 || configStart * sectorSize >= disk.Length)
        {
            return null;
        }

        var warnings = new List<string>();
        if (major != 2 || minor is not (11 or 12))
        {
            warnings.Add($"PRIVHEAD version {major}.{minor}");
        }

        // 予備の PRIVHEAD (データベースの 6・1856・2047 セクタ目)。
        foreach (int off in new[] { 6, 1856, 2047 })
        {
            byte[] copy = disk.ReadBytes((configStart + off) * sectorSize, 512);
            if (copy.AsSpan(0, 8).SequenceEqual("PRIVHEAD"u8) && !copy.AsSpan(0x11B, 0x28).SequenceEqual(ph.AsSpan(0x11B, 0x28)))
            {
                warnings.Add($"PRIVHEAD copy at {configStart + off} differs");
            }
        }

        // TOCBLOCK (データベースの 1・2 セクタ目) の config の位置。
        long vmdbSector = configStart + OffVmdb;
        byte[] toc = disk.ReadBytes((configStart + 1) * sectorSize, 512);
        if (toc.AsSpan(0, 8).SequenceEqual("TOCBLOCK"u8) && Encoding.ASCII.GetString(toc, 0x24, 6) == "config")
        {
            long start = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(toc.AsSpan(0x2E)), 4096);
            if (start > 0 && start != OffVmdb)
            {
                warnings.Add($"TOCBLOCK config start {start}");
            }
        }
        else
        {
            warnings.Add("TOCBLOCK missing");
        }

        byte[] vmdb = disk.ReadBytes(vmdbSector * sectorSize, 512, out complete);
        if (!complete || !vmdb.AsSpan(0, 4).SequenceEqual("VMDB"u8))
        {
            return null;
        }

        uint lastSeq = BinaryPrimitives.ReadUInt32BigEndian(vmdb.AsSpan(4));
        int vblkSize = (int)BinaryPrimitives.ReadUInt32BigEndian(vmdb.AsSpan(8));
        int vblkOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(vmdb.AsSpan(0x0C));
        string groupName = Encoding.ASCII.GetString(vmdb, 0x16, 31).TrimEnd('\0');
        uint sequence = (uint)BinaryPrimitives.ReadUInt64BigEndian(vmdb.AsSpan(0x75));
        if (vblkSize is < 64 or > 4096 || vblkOffset < 0 || vblkOffset > 1 << 16)
        {
            return null;
        }

        long total = Math.Min((long)vblkSize * lastSeq, Math.Max(0, configSize * sectorSize - (long)OffVmdb * sectorSize));
        total = Math.Min(total, (long)vblkSize * MaxRecords);
        byte[] area = disk.ReadBytes(vmdbSector * sectorSize, (int)Math.Max(0, total));
        var records = new List<byte[]>();
        var fragments = new Dictionary<uint, SortedDictionary<int, byte[]>>();
        var fragmentCounts = new Dictionary<uint, int>();
        for (long at = vblkOffset; at + vblkSize <= area.Length; at += vblkSize)
        {
            ReadOnlySpan<byte> v = area.AsSpan((int)at, vblkSize);
            if (!v[..4].SequenceEqual("VBLK"u8))
            {
                continue;
            }

            uint group = BinaryPrimitives.ReadUInt32BigEndian(v[8..]);
            int rec = BinaryPrimitives.ReadUInt16BigEndian(v[0x0C..]);
            int nrec = BinaryPrimitives.ReadUInt16BigEndian(v[0x0E..]);
            if (nrec == 1)
            {
                records.Add(v.ToArray());
            }
            else if (nrec > 1 && nrec <= 64 && rec < nrec)
            {
                // 断片の VBLK: 0x10 より後ろをつなぐ。
                if (!fragments.TryGetValue(group, out SortedDictionary<int, byte[]>? parts))
                {
                    fragments[group] = parts = [];
                    fragmentCounts[group] = nrec;
                }

                parts[rec] = rec == 0 ? v.ToArray() : v[0x10..].ToArray();
            }
        }

        foreach ((uint group, SortedDictionary<int, byte[]> parts) in fragments)
        {
            if (parts.Count == fragmentCounts[group])
            {
                records.Add([.. parts.Values.SelectMany(p => p)]);
            }
            else
            {
                warnings.Add($"VBLK group {group} is incomplete");
            }
        }

        var disks = new List<LdmDiskRecord>();
        var volumes = new List<LdmVolumeRecord>();
        var components = new List<LdmComponentRecord>();
        var partitions = new List<LdmPartitionRecord>();
        foreach (byte[] r in records)
        {
            try
            {
                switch (r[0x13])
                {
                    case 0x34 or 0x44 when ParseDisk(r) is { } d:
                        disks.Add(d);
                        break;
                    case 0x51 when ParseVolume(r) is { } vol:
                        volumes.Add(vol);
                        break;
                    case 0x32 when ParseComponent(r) is { } c:
                        components.Add(c);
                        break;
                    case 0x33 when ParsePartition(r) is { } p:
                        partitions.Add(p);
                        break;
                }
            }
            catch (IndexOutOfRangeException)
            {
                warnings.Add("VBLK record out of bounds");
            }
            catch (ArgumentOutOfRangeException)
            {
                warnings.Add("VBLK record out of bounds");
            }
        }

        TryGuid(ph.AsSpan(0x70, 64), out Guid host);
        TryGuid(ph.AsSpan(0xB0, 64), out Guid groupGuid);
        return new LdmDatabase
        {
            DiskGuid = diskGuid,
            HostGuid = host,
            DiskGroupGuid = groupGuid,
            DiskGroupName = groupName.Length > 0 ? groupName : Encoding.ASCII.GetString(ph, 0xF0, 31).TrimEnd('\0'),
            LogicalDiskStart = ldStart,
            LogicalDiskSize = ldSize,
            ConfigStart = configStart,
            ConfigSize = configSize,
            Sequence = sequence,
            VersionMajor = major,
            VersionMinor = minor,
            PrivheadOffset = privheadLba * sectorSize,
            Disks = disks,
            Volumes = volumes,
            Components = components,
            Partitions = partitions,
            Warnings = warnings,
        };
    }

    private static bool TryGuid(ReadOnlySpan<byte> field, out Guid guid)
    {
        int end = field.IndexOf((byte)0);
        string s = Encoding.ASCII.GetString(end >= 0 ? field[..end] : field);
        return Guid.TryParse(s, out guid);
    }

    /// <summary>Linux の <c>ldm_relative</c>: <paramref name="base"/> + <paramref name="offset"/> の長さのバイトの後ろの位置 (<paramref name="base"/> からの相対)。</summary>
    internal static int Relative(byte[] b, int @base, int offset)
    {
        int at = @base + offset;
        if (offset < 0 || at >= b.Length || at + b[at] >= b.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return b[at] + offset + 1;
    }

    /// <summary>可変長の数値 (先頭の 1 バイトが長さ、ビッグエンディアン)。</summary>
    internal static ulong Vnum(byte[] b, int at)
    {
        int length = b[at];
        if (length is 0 or > 8 || at + 1 + length > b.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(at));
        }

        ulong v = 0;
        for (int i = 0; i < length; i++)
        {
            v = v << 8 | b[at + 1 + i];
        }

        return v;
    }

    internal static string Vstr(byte[] b, int at)
    {
        int length = b[at];
        return at + 1 + length > b.Length ? throw new ArgumentOutOfRangeException(nameof(at)) : Encoding.ASCII.GetString(b, at + 1, length);
    }

    private static uint Size(byte[] b) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(0x14));

    private static LdmDiskRecord? ParseDisk(byte[] b)
    {
        int rObj = Relative(b, 0x18, 0);
        int rName = Relative(b, 0x18, rObj);
        string name = Vstr(b, 0x18 + rObj);
        ulong id = Vnum(b, 0x18);
        if (b[0x13] == 0x44)
        {
            if (rName + 45 != Size(b) || 0x18 + rName + 16 > b.Length)
            {
                return null;
            }

            return new LdmDiskRecord(id, name, new Guid(b.AsSpan(0x18 + rName, 16), bigEndian: true));
        }

        int rDiskId = Relative(b, 0x18, rName);
        int rAlt = Relative(b, 0x18, rDiskId);
        if (rAlt + 12 != Size(b) || !Guid.TryParse(Vstr(b, 0x18 + rName), out Guid guid))
        {
            return null;
        }

        return new LdmDiskRecord(id, name, guid);
    }

    private static LdmVolumeRecord? ParseVolume(byte[] b)
    {
        byte flags = b[0x12];
        int rObj = Relative(b, 0x18, 0);
        int rName = Relative(b, 0x18, rObj);
        int rType = Relative(b, 0x18, rName);
        int rDisable = Relative(b, 0x18, rType);
        int rChild = Relative(b, 0x2D, rDisable);
        int rSize = Relative(b, 0x3D, rChild);
        int r = rSize;
        if ((flags & 0x08) != 0) r = Relative(b, 0x52, r);
        if ((flags & 0x20) != 0) r = Relative(b, 0x52, r);
        if ((flags & 0x80) != 0) r = Relative(b, 0x52, r);
        if ((flags & 0x02) != 0) r = Relative(b, 0x52, r);
        if (r + 58 > Size(b) || 0x42 + rSize + 16 > b.Length)
        {
            return null;
        }

        string state = Encoding.ASCII.GetString(b, 0x18 + rDisable, 14).TrimEnd('\0', ' ');
        return new LdmVolumeRecord(Vnum(b, 0x18), Vstr(b, 0x18 + rObj), Vstr(b, 0x18 + rName), Vnum(b, 0x2D + rDisable), Vnum(b, 0x3D + rChild),
            b[0x41 + rSize], new Guid(b.AsSpan(0x42 + rSize, 16), bigEndian: true), state);
    }

    private static LdmComponentRecord? ParseComponent(byte[] b)
    {
        int rObj = Relative(b, 0x18, 0);
        int rName = Relative(b, 0x18, rObj);
        int rState = Relative(b, 0x18, rName);
        int rChild = Relative(b, 0x1D, rState);
        int rParent = Relative(b, 0x2D, rChild);
        bool stripe = (b[0x12] & 0x10) != 0;
        int rStripe = 0, rCols = 0;
        if (stripe)
        {
            rStripe = Relative(b, 0x2E, rParent);
            rCols = Relative(b, 0x2E, rStripe);
        }

        int len = (stripe ? rCols : rParent) + 22;
        if (len != Size(b))
        {
            return null;
        }

        return new LdmComponentRecord(Vnum(b, 0x18), Vstr(b, 0x18 + rObj), b[0x18 + rState], Vnum(b, 0x1D + rState), Vnum(b, 0x2D + rChild),
            stripe ? Vnum(b, 0x2E + rParent) : 0, stripe ? Vnum(b, 0x2E + rStripe) : 0);
    }

    private static LdmPartitionRecord? ParsePartition(byte[] b)
    {
        int rObj = Relative(b, 0x18, 0);
        int rName = Relative(b, 0x18, rObj);
        int rSize = Relative(b, 0x34, rName);
        int rParent = Relative(b, 0x34, rSize);
        int rDisk = Relative(b, 0x34, rParent);
        bool indexed = (b[0x12] & 0x08) != 0;
        int len = (indexed ? Relative(b, 0x34, rDisk) : rDisk) + 28;
        if (len > Size(b) || 0x2C + rName + 8 > b.Length)
        {
            return null;
        }

        return new LdmPartitionRecord(Vnum(b, 0x18), Vstr(b, 0x18 + rObj), BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(0x24 + rName)),
            BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(0x2C + rName)), Vnum(b, 0x34 + rName), Vnum(b, 0x34 + rSize), Vnum(b, 0x34 + rParent),
            indexed ? b[0x35 + rDisk] : null);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"LDM {DiskGuid} (group {DiskGroupName}, seq {Sequence})");
}
