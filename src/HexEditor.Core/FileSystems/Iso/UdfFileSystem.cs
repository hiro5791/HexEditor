using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HexEditor.Core.FileSystems.Iso;

/// <summary>項目の実装用の情報 (ファイルエントリの位置と、断片)。</summary>
internal sealed record UdfTag(int PartitionRef, long Lbn, long FeOffset, IReadOnlyList<FsExtent> Extents, byte[]? Embedded, int FileType, bool Extended);

/// <summary>
/// UDF の解析 (FOR-14 の仕様 3)。ボリューム認識シーケンス (BEA01・NSR02・NSR03・TEA01) を確かめ、アンカー (セクタ 256、末尾 − 256、末尾) から
/// ボリューム記述子シーケンスを読む。パーティション記述子・論理ボリューム記述子 (タイプ 1 のパーティションと、2.50 以降のメタデータパーティション)・
/// ファイルセット記述子・ファイルエントリ (FE / EFE)・ファイル識別記述子を読む。タグのチェックサムと記述子の CRC が合わないものは警告にして続ける。
/// 仮想パーティション (VAT) とスペアリングテーブルは未決定 (VAT は読めない、スペアリングは物理パーティションとして読む)。削除された項目は扱わない
/// (FOR-17 の対象外)。未割り当て領域のビットマップがあれば割り当ての情報にする。
/// </summary>
public sealed class UdfFileSystem : FileSystemBase, IFsChoices
{
    public const int SectorBytes = 2048;
    public const int MaxDepth = 64;
    public const int MaxDirectoryBytes = 256 * 1024 * 1024;
    public const int MaxExtents = 100_000;
    private const int MaxIssues = 64;

    private sealed record Partition(int Number, long Start, long Length, byte[] ContentsUse);

    private abstract record PartitionMap(int PartitionNumber);

    private sealed record PhysicalMap(int PartitionNumber, bool Sparable) : PartitionMap(PartitionNumber);

    private sealed record MetadataMap(int PartitionNumber, long FileLocation, long MirrorLocation, long BitmapLocation) : PartitionMap(PartitionNumber);

    private sealed record VirtualMap(int PartitionNumber) : PartitionMap(PartitionNumber);

    private readonly CachedVolumeReader _meta;
    private readonly FileSystemOpenOptions _options;
    private readonly Dictionary<int, Partition> _partitions = [];
    private readonly List<PartitionMap> _maps = [];
    private readonly FsEntry _root;
    private readonly int _blockSize;
    private readonly long _sectors;
    private readonly string _label;
    private readonly string _volumeId;
    private readonly ushort _revision;
    private readonly bool _hasIso;
    private readonly long _avdp;
    private readonly (long Location, long Length) _mainVds;
    private readonly (long Location, long Length) _reserveVds;
    private readonly HashSet<string> _issueKeys = [];
    private readonly object _gate = new();
    private IReadOnlyList<FsExtent>? _metadataExtents;
    private IAllocationMap? _allocation;
    private bool _allocationRead;

    public UdfFileSystem(IVolumeReader volume, FileSystemOpenOptions options) : base(volume)
    {
        _options = options;
        _meta = volume as CachedVolumeReader ?? new CachedVolumeReader(volume, 64 * 1024, 64);
        _sectors = volume.Length / SectorBytes;
        if (!HasNsr())
        {
            throw new FileSystemOpenException("No UDF volume recognition sequence (NSR02 / NSR03) was found.", FileSystemKind.Udf);
        }

        _hasIso = _meta.ReadBytes(16L * SectorBytes + 1, 5).AsSpan().SequenceEqual("CD001"u8);
        long? avdp = null;
        foreach (long candidate in new[] { 256L, _sectors - 256, _sectors - 1, 512 })
        {
            if (candidate > 16 && candidate < _sectors && ReadTag(candidate, out byte[] d, expectedLocation: candidate) == 2)
            {
                avdp = candidate;
                _mainVds = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(20)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16)));
                _reserveVds = (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(28)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(24)));
                break;
            }
        }

        _avdp = avdp ?? throw new FileSystemOpenException("No UDF anchor volume descriptor pointer was found.", FileSystemKind.Udf);
        byte[]? lvd = null, pvd = null;
        foreach ((long location, long length) in new[] { _mainVds, _reserveVds })
        {
            (lvd, pvd) = ReadVds(location, length);
            if (lvd is not null && _partitions.Count > 0)
            {
                break;
            }
        }

        if (lvd is null)
        {
            throw new FileSystemOpenException("No UDF logical volume descriptor was found.", FileSystemKind.Udf);
        }

        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(212));
        _blockSize = blockSize is 512 or 1024 or 2048 or 4096 ? blockSize : SectorBytes;
        _label = DString(lvd.AsSpan(84, 128));
        _volumeId = pvd is null ? string.Empty : DString(pvd.AsSpan(24, 32));
        _revision = BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(216 + 24));
        ReadPartitionMaps(lvd);
        LvdFsd = (BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(248)), BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(252)),
            BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(256)));

        // ファイルセット記述子とルートディレクトリ。
        if (Translate(LvdFsd.PartitionRef, LvdFsd.Lbn) is not long fsdSector || ReadTag(fsdSector, out byte[] fsd, expectedLocation: LvdFsd.Lbn) != 256)
        {
            throw new FileSystemOpenException("The UDF file set descriptor could not be read.", FileSystemKind.Udf);
        }

        FsdRevision = BinaryPrimitives.ReadUInt16LittleEndian(fsd.AsSpan(416 + 24));
        (long rootLbn, int rootRef) = (BinaryPrimitives.ReadUInt32LittleEndian(fsd.AsSpan(404)), BinaryPrimitives.ReadUInt16LittleEndian(fsd.AsSpan(408)));
        _root = MakeEntry(rootRef, rootLbn, string.Empty, "\\", null, isDirectory: true, hidden: false)
            ?? throw new FileSystemOpenException("The UDF root directory could not be read.", FileSystemKind.Udf);
    }

    private (long Length, long Lbn, int PartitionRef) LvdFsd { get; }

    public ushort FsdRevision { get; }

    /// <summary>UDF の版 (論理ボリューム記述子のドメイン識別子の接尾辞。0x0250 は 2.50)。</summary>
    public ushort Revision => _revision;

    public string RevisionText => $"{_revision >> 8:X}.{_revision & 0xFF:X2}";

    public override FileSystemKind Kind => FileSystemKind.Udf;
    public override long ClusterSize => _blockSize;
    public override long ClusterCount => Volume.Length / _blockSize;
    public override int SectorSize => SectorBytes;
    public override long FileSystemLength => Volume.Length / _blockSize * _blockSize;
    protected override long ClusterAreaOffset => 0;
    public override FsEntry Root => _root;

    /// <summary>メタデータパーティションを使っているか (2.50 以降)。</summary>
    public bool HasMetadataPartition => _maps.OfType<MetadataMap>().Any();

    public IReadOnlyList<FsChoice> Choices => _hasIso ? [new FsChoice("Format", ["Udf", "Iso9660"], 0)] : [];

    public FileSystemKind? Select(string key, int index) => key == "Format" && index == 1 && _hasIso ? FileSystemKind.Iso9660 : null;

    // ---- 記述子 ----

    private void Warn(FsIssueKind kind, string detail)
    {
        lock (_gate)
        {
            if (IssueList.Count < MaxIssues && _issueKeys.Add($"{kind}:{detail}"))
            {
                IssueList.Add(new FsIssue(kind, detail));
            }
        }
    }

    /// <summary>セクタの記述子のタグを読む (チェックサム・CRC・位置が合わなければ警告)。戻り値はタグの識別子 (読めなければ -1)。</summary>
    private int ReadTag(long sector, out byte[] data, long? expectedLocation = null, int length = SectorBytes)
    {
        data = _meta.ReadBytes(sector * SectorBytes, length, out bool complete);
        if (!complete)
        {
            return -1;
        }

        return CheckTag(data, sector, expectedLocation);
    }

    private int CheckTag(ReadOnlySpan<byte> d, long sector, long? expectedLocation)
    {
        if (d.Length < 16 || d.IndexOfAnyExcept((byte)0) < 0)
        {
            return -1;
        }

        int id = BinaryPrimitives.ReadUInt16LittleEndian(d);
        int sum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i != 4)
            {
                sum += d[i];
            }
        }

        if ((byte)sum != d[4])
        {
            // チェックサムが合わないものは記述子とみなさない (ほかのデータを記述子と誤らない)。
            if (id is > 0 and < 300)
            {
                Warn(FsIssueKind.UdfTagChecksum, sector.ToString(CultureInfo.InvariantCulture));
            }

            return -1;
        }

        int crcLength = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
        if (16 + crcLength <= d.Length && FsChecksums.CrcCcitt(d.Slice(16, crcLength)) != BinaryPrimitives.ReadUInt16LittleEndian(d[8..]))
        {
            Warn(FsIssueKind.UdfDescriptorCrc, sector.ToString(CultureInfo.InvariantCulture));
        }

        _ = expectedLocation;
        return id;
    }

    private bool HasNsr()
    {
        for (int s = 16; s < 16 + 64; s++)
        {
            byte[] head = _meta.ReadBytes((long)s * SectorBytes, 6);
            ReadOnlySpan<byte> id = head.AsSpan(1, 5);
            if (id.SequenceEqual("NSR02"u8) || id.SequenceEqual("NSR03"u8))
            {
                return true;
            }

            if (id.SequenceEqual("TEA01"u8))
            {
                break;
            }
        }

        return false;
    }

    private (byte[]? Lvd, byte[]? Pvd) ReadVds(long location, long length)
    {
        byte[]? lvd = null, pvd = null;
        uint lvdSeq = 0, pvdSeq = 0;
        var pdSeq = new Dictionary<int, uint>();
        long sector = location;
        long end = location + Math.Min(length / SectorBytes, 256);
        for (int guard = 0; guard < 512 && sector < end && sector < _sectors; guard++, sector++)
        {
            int id = ReadTag(sector, out byte[] d, expectedLocation: sector);
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16));
            switch (id)
            {
                case 1 when pvd is null || seq >= pvdSeq:
                    (pvd, pvdSeq) = (d, seq);
                    break;
                case 3:
                    // 続きの記述子の位置 (Volume Descriptor Pointer)。
                    long next = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(24));
                    long nextLength = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(20));
                    sector = next - 1;
                    end = next + Math.Min(nextLength / SectorBytes, 256);
                    break;
                case 5:
                    int number = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(22));
                    if (!pdSeq.TryGetValue(number, out uint old) || seq >= old)
                    {
                        pdSeq[number] = seq;
                        _partitions[number] = new Partition(number, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(188)),
                            BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(192)), d.AsSpan(56, 128).ToArray());
                    }

                    break;
                case 6 when lvd is null || seq >= lvdSeq:
                    (lvd, lvdSeq) = (d, seq);
                    break;
                case 8:
                    return (lvd, pvd);
                case -1 when lvd is null && pvd is null && _partitions.Count == 0:
                    return (null, null);
            }
        }

        return (lvd, pvd);
    }

    private void ReadPartitionMaps(byte[] lvd)
    {
        int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(268)), 64);
        int tableLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(264)), lvd.Length - 440);
        int pos = 440;
        for (int i = 0; i < count && pos + 2 <= 440 + tableLength; i++)
        {
            int type = lvd[pos];
            int len = lvd[pos + 1];
            if (len < 6 || pos + len > lvd.Length)
            {
                break;
            }

            if (type == 1)
            {
                _maps.Add(new PhysicalMap(BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(pos + 4)), false));
            }
            else if (type == 2 && len >= 64)
            {
                string ident = Encoding.ASCII.GetString(lvd, pos + 5, 23).TrimEnd('\0', ' ');
                int number = BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(pos + 38));
                if (ident.StartsWith("*UDF Metadata Partition", StringComparison.Ordinal))
                {
                    _maps.Add(new MetadataMap(number, BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(pos + 40)),
                        BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(pos + 44)), BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(pos + 48))));
                }
                else if (ident.StartsWith("*UDF Sparable Partition", StringComparison.Ordinal))
                {
                    Warn(FsIssueKind.UdfUnsupportedPartition, "sparable");
                    _maps.Add(new PhysicalMap(number, true));
                }
                else
                {
                    Warn(FsIssueKind.UdfUnsupportedPartition, ident.StartsWith("*UDF Virtual Partition", StringComparison.Ordinal) ? "virtual (VAT)" : ident);
                    _maps.Add(new VirtualMap(number));
                }
            }

            pos += len;
        }
    }

    /// <summary>論理ブロック (パーティション参照番号、パーティション内の番号) のボリューム上のセクタ番号。</summary>
    private long? Translate(int partitionRef, long lbn)
    {
        if (partitionRef < 0 || partitionRef >= _maps.Count || lbn < 0)
        {
            return null;
        }

        int perBlock = Math.Max(1, _blockSize / SectorBytes);
        switch (_maps[partitionRef])
        {
            case PhysicalMap p when _partitions.TryGetValue(p.PartitionNumber, out Partition? part):
                return part.Start + lbn * perBlock;
            case MetadataMap m:
                IReadOnlyList<FsExtent> extents = MetadataExtents(m);
                long logical = lbn * _blockSize;
                foreach (FsExtent e in extents)
                {
                    if (logical >= e.LogicalOffset && logical < e.LogicalOffset + e.Length && e.VolumeOffset is long v)
                    {
                        return (v + (logical - e.LogicalOffset)) / SectorBytes;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>メタデータファイル (または予備) の断片 (2.50 以降のメタデータパーティション)。</summary>
    private IReadOnlyList<FsExtent> MetadataExtents(MetadataMap m)
    {
        if (_metadataExtents is { } cached)
        {
            return cached;
        }

        int physicalRef = _maps.FindIndex(x => x is PhysicalMap p && p.PartitionNumber == m.PartitionNumber);
        IReadOnlyList<FsExtent> result = [];
        foreach (long location in new[] { m.FileLocation, m.MirrorLocation })
        {
            if (physicalRef >= 0 && ReadFileEntry(physicalRef, location, out _) is { } fe && fe.Extents.Count > 0)
            {
                result = fe.Extents;
                break;
            }
        }

        return _metadataExtents = result;
    }

    // ---- ファイルエントリ ----

    private sealed record FileEntry(int FileType, long InformationLength, IReadOnlyList<FsExtent> Extents, byte[]? Embedded, FsTimestamp Accessed,
        FsTimestamp Modified, FsTimestamp Created, FsTimestamp Attribute, uint Uid, uint Gid, uint Permissions, int Links, long UniqueId, long Offset,
        bool Extended, FsCorruptionKind Problem);

    private FileEntry? ReadFileEntry(int partitionRef, long lbn, out long sector)
    {
        sector = Translate(partitionRef, lbn) ?? -1;
        if (sector < 0 || sector >= _sectors)
        {
            return null;
        }

        int id = ReadTag(sector, out byte[] d, expectedLocation: lbn, length: _blockSize);
        if (id is not (261 or 266))
        {
            return null;
        }

        bool extended = id == 266;
        int fileType = d[16 + 11];
        int adType = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(16 + 18)) & 7;
        long info = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(56));
        int eaAt = extended ? 208 : 168;
        int lea = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(eaAt)), d.Length);
        int lad = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(eaAt + 4)), d.Length);
        int adStart = eaAt + 8 + lea;
        FsCorruptionKind problem = FsCorruptionKind.None;
        if (adStart + lad > d.Length || info < 0)
        {
            problem = FsCorruptionKind.BadAttribute;
            lad = Math.Max(0, Math.Min(lad, d.Length - adStart));
            info = Math.Max(0, info);
        }

        byte[]? embedded = null;
        IReadOnlyList<FsExtent> extents = [];
        if (adType == 3)
        {
            embedded = adStart <= d.Length ? d.AsSpan(adStart, Math.Max(0, Math.Min(lad, d.Length - adStart))).ToArray() : [];
        }
        else if (adType is 0 or 1)
        {
            extents = AllocationExtents(d.AsSpan(adStart, lad).ToArray(), adType, partitionRef, info, ref problem);
        }
        else
        {
            problem = FsCorruptionKind.Unsupported;
        }

        return new FileEntry(fileType, info, extents, embedded,
            Time(d.AsSpan(extended ? 80 : 72)), Time(d.AsSpan(extended ? 92 : 84)), extended ? Time(d.AsSpan(104)) : default,
            Time(d.AsSpan(extended ? 116 : 96)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(36)), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(40)),
            BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(44)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(48)),
            BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(extended ? 200 : 160)), sector * SectorBytes, extended, problem);
    }

    /// <summary>アロケーション記述子 (short_ad / long_ad) を断片にする。続きの記述子 (種類 3) は 64 回までたどる。</summary>
    private List<FsExtent> AllocationExtents(byte[] ads, int adType, int partitionRef, long infoLength, ref FsCorruptionKind problem)
    {
        var list = new List<FsExtent>();
        long logical = 0;
        int size = adType == 0 ? 8 : 16;
        int hops = 0;
        byte[] data = ads;
        int pos = 0;
        while (list.Count < MaxExtents)
        {
            if (pos + size > data.Length)
            {
                break;
            }

            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            long length = raw & 0x3FFFFFFF;
            int type = (int)(raw >> 30);
            long position = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int part = adType == 0 ? partitionRef : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 8));
            pos += size;
            if (length == 0)
            {
                break;
            }

            if (type == 3)
            {
                // 続きのアロケーション記述子 (AED、タグ 258)。
                if (hops++ >= 64 || Translate(part, position) is not long aed || ReadTag(aed, out byte[] next, length: _blockSize) != 258)
                {
                    problem = FsCorruptionKind.BadRunList;
                    break;
                }

                int count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(next.AsSpan(20)), next.Length - 24);
                data = next.AsSpan(24, Math.Max(0, count)).ToArray();
                pos = 0;
                continue;
            }

            long blocks = (length + _blockSize - 1) / _blockSize;
            if (type == 0)
            {
                if (Translate(part, position) is long sector && sector * SectorBytes + length <= Volume.Length + _blockSize)
                {
                    list.Add(new FsExtent(logical, length, sector * SectorBytes, sector * SectorBytes / _blockSize, blocks));
                }
                else
                {
                    problem = FsCorruptionKind.ClusterOutOfRange;
                }
            }
            else
            {
                // 割り当てて記録していない区間・割り当てていない区間は 0 として読む。
                list.Add(new FsExtent(logical, length, null, -1, 0));
            }

            logical += length;
            if (logical >= infoLength)
            {
                break;
            }
        }

        return list;
    }

    /// <summary>UDF の日時 (12 バイト。時差は分、-2047 は時差なし)。</summary>
    public static FsTimestamp Time(ReadOnlySpan<byte> t)
    {
        if (t.Length < 12 || t.IndexOfAnyExcept((byte)0) < 0)
        {
            return default;
        }

        ushort typeAndZone = BinaryPrimitives.ReadUInt16LittleEndian(t);
        int zone = typeAndZone & 0xFFF;
        if ((zone & 0x800) != 0)
        {
            zone -= 0x1000;
        }

        short year = BinaryPrimitives.ReadInt16LittleEndian(t[2..]);
        try
        {
            var local = new DateTime(year, t[4], t[5], t[6], t[7], t[8], DateTimeKind.Utc);
            long ticks = t[9] * 100_000L + t[10] * 1_000L + t[11] * 10L;
            local = local.AddTicks(ticks);
            bool unknown = zone == -2047;
            return new FsTimestamp(unknown ? local : local.AddMinutes(-zone), ZoneUnknown: unknown) { Recorded = unknown ? local : null };
        }
        catch (ArgumentOutOfRangeException)
        {
            return new FsTimestamp(null, Invalid: true);
        }
    }

    /// <summary>OSTA CS0 の文字列 (先頭の圧縮 ID が 8 なら 1 バイト、16 なら UTF-16BE)。</summary>
    public static string Cs0(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0)
        {
            return string.Empty;
        }

        return b[0] switch
        {
            8 or 254 => Encoding.Latin1.GetString(b[1..]),
            16 or 255 => Encoding.BigEndianUnicode.GetString(b[1..(1 + ((b.Length - 1) & ~1))]),
            _ => Encoding.Latin1.GetString(b[1..]),
        };
    }

    /// <summary>dstring (最後のバイトが使った長さ)。</summary>
    public static string DString(ReadOnlySpan<byte> b)
    {
        int used = b[^1];
        return used == 0 || used > b.Length - 1 ? string.Empty : Cs0(b[..used]).TrimEnd('\0', ' ');
    }

    private static string Permissions(uint p, bool dir)
    {
        // UDF の権限: 他者 (下位 5 ビット)、グループ (<<5)、所有者 (<<10)。実行 1、書き込み 2、読み込み 4。
        var s = new StringBuilder(dir ? "d" : "-");
        foreach (int shift in new[] { 10, 5, 0 })
        {
            s.Append((p >> shift & 4) != 0 ? 'r' : '-').Append((p >> shift & 2) != 0 ? 'w' : '-').Append((p >> shift & 1) != 0 ? 'x' : '-');
        }

        return s.ToString();
    }

    private FsEntry? MakeEntry(int partitionRef, long lbn, string name, string path, FsEntry? parent, bool isDirectory, bool hidden)
    {
        if (ReadFileEntry(partitionRef, lbn, out _) is not { } fe)
        {
            if (parent is null)
            {
                return null;
            }

            var broken = new FsEntry
            {
                Id = $"udf:{partitionRef}:{lbn}:{name}",
                Name = name,
                Path = path,
                Flags = (isDirectory ? FsEntryFlags.Directory : 0) | FsEntryFlags.Corrupt,
                Corruption = FsCorruptionKind.BadSignature,
                CorruptionDetail = $"file entry {partitionRef}:{lbn}",
                FileIdText = $"{partitionRef}:{lbn}",
                ParentId = parent.Id,
                Tag = new UdfTag(partitionRef, lbn, -1, [], null, 0, false),
            };
            return broken;
        }

        bool dir = isDirectory || fe.FileType == 4;
        var entry = new FsEntry
        {
            Id = $"udf:{partitionRef}:{lbn}:{name}",
            Name = name,
            Path = path,
            Flags = (dir ? FsEntryFlags.Directory : 0) | (fe.Embedded is not null ? FsEntryFlags.Resident : 0),
            Size = dir ? 0 : fe.InformationLength,
            AllocatedSize = fe.Extents.Sum(e => e.ClusterCount) * _blockSize,
            Created = fe.Created,
            Modified = fe.Modified,
            Accessed = fe.Accessed,
            Changed = fe.Attribute,
            Attributes = Permissions(fe.Permissions, dir) + (hidden ? " H" : string.Empty),
            FileIdText = $"{partitionRef}:{lbn}",
            FirstCluster = fe.Extents.FirstOrDefault(e => e.VolumeOffset is not null)?.StartCluster,
            MetadataOffset = fe.Offset,
            MetadataLength = _blockSize,
            ParentId = parent?.Id,
            FragmentCount = fe.Extents.Count(e => e.VolumeOffset is not null),
            Tag = new UdfTag(partitionRef, lbn, fe.Offset, fe.Embedded is not null
                ? [new FsExtent(0, fe.InformationLength, fe.Offset, fe.Offset / _blockSize, 0) { Resident = true }] : fe.Extents, fe.Embedded, fe.FileType,
                fe.Extended),
        };
        if (fe.Problem != FsCorruptionKind.None)
        {
            entry.Corruption = fe.Problem;
            entry.Flags |= FsEntryFlags.Corrupt;
        }

        return entry;
    }

    public override IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default)
    {
        if (!directory.IsDirectory || directory.Tag is not UdfTag tag || tag.FeOffset < 0)
        {
            yield break;
        }

        IVolumeReader content = OpenContent(directory);
        long total = Math.Min(content.Length, MaxDirectoryBytes);
        byte[] buffer = new byte[64 * 1024 + 4096];
        int have = 0;
        long consumed = 0;
        long read = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read < total && have < 4096)
            {
                int n = (int)Math.Min(buffer.Length - have, total - read);
                content.Read(read, buffer.AsSpan(have, n));
                read += n;
                have += n;
            }

            if (have < 38)
            {
                yield break;
            }

            int id = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
            int lfi = buffer[19];
            int liu = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(36));
            int size = (38 + liu + lfi + 3) & ~3;
            if (id != 257 || size > have)
            {
                yield break;
            }

            if (CheckTag(buffer.AsSpan(0, Math.Min(size, have)), tag.FeOffset / SectorBytes, null) == 257)
            {
                byte characteristics = buffer[18];
                bool deleted = (characteristics & 0x04) != 0, parent = (characteristics & 0x08) != 0;
                if (!deleted && !parent && lfi > 0)
                {
                    string name = Cs0(buffer.AsSpan(38 + liu, lfi));
                    long lbn = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
                    int pref = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28));
                    if (MakeEntry(pref, lbn, name, FsText.Combine(directory.Path, name), directory, (characteristics & 0x02) != 0,
                        (characteristics & 0x01) != 0) is { } e)
                    {
                        yield return e;
                    }
                }
            }

            consumed += size;
            Buffer.BlockCopy(buffer, size, buffer, 0, have - size);
            have -= size;
            if (consumed >= total)
            {
                yield break;
            }
        }
    }

    public override IReadOnlyList<FsExtent> GetExtents(FsEntry entry) => entry.Tag switch
    {
        UdfTag t => t.Extents,
        IReadOnlyList<FsExtent> fixedExtents => fixedExtents,
        _ => [],
    };

    public override IVolumeReader OpenContent(FsEntry entry)
    {
        if (entry.Tag is UdfTag { Embedded: { } data })
        {
            return new ExtentContentReader(Volume, GetExtents(entry), data.Length, data);
        }

        IReadOnlyList<FsExtent> extents = GetExtents(entry);
        long size = entry.IsDirectory || entry.Flags.HasFlag(FsEntryFlags.Metadata) ? extents.Sum(e => e.Length) : entry.Size;
        return new ExtentContentReader(Volume, extents, size);
    }

    public override IReadOnlyList<FsDetail> Describe(FsEntry entry)
    {
        var d = new List<FsDetail>();
        if (entry.Tag is UdfTag t)
        {
            d.Add(new FsDetail("IcbLocation", $"{t.PartitionRef}:{t.Lbn}"));
            d.Add(new FsDetail("FileEntryOffset", t.FeOffset >= 0 ? $"0x{t.FeOffset:X}" : "-"));
            d.Add(new FsDetail("FileEntryType", t.Extended ? "EFE" : "FE"));
            d.Add(new FsDetail("IcbFileType", t.FileType.ToString(CultureInfo.InvariantCulture)));
            d.Add(new FsDetail("AllocationDescriptors", t.Embedded is not null ? "embedded" : t.Extents.Count.ToString(CultureInfo.InvariantCulture)));
        }

        return d;
    }

    public override IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default)
    {
        yield return Fixed("$VolumeRecognition", 16L * SectorBytes, 3L * SectorBytes);
        yield return Fixed("$AnchorVolumeDescriptor", _avdp * SectorBytes, SectorBytes);
        yield return Fixed("$MainVolumeDescriptorSequence", _mainVds.Location * SectorBytes, _mainVds.Length);
        yield return Fixed("$ReserveVolumeDescriptorSequence", _reserveVds.Location * SectorBytes, _reserveVds.Length);
        if (_maps.OfType<MetadataMap>().FirstOrDefault() is { } m)
        {
            IReadOnlyList<FsExtent> extents = MetadataExtents(m);
            yield return new FsEntry
            {
                Id = "udf:meta:$MetadataFile",
                Name = "$MetadataFile",
                Path = "\\$MetadataFile",
                Flags = FsEntryFlags.Metadata,
                Size = extents.Sum(e => e.Length),
                AllocatedSize = extents.Sum(e => e.ClusterCount) * _blockSize,
                FragmentCount = extents.Count,
                FirstCluster = extents.FirstOrDefault()?.StartCluster,
                Tag = extents,
            };
        }
    }

    private FsEntry Fixed(string name, long offset, long length)
    {
        long blocks = (length + _blockSize - 1) / _blockSize;
        return new FsEntry
        {
            Id = "udf:meta:" + name,
            Name = name,
            Path = "\\" + name,
            Flags = FsEntryFlags.Metadata,
            Size = length,
            AllocatedSize = blocks * _blockSize,
            MetadataOffset = offset,
            MetadataLength = (int)Math.Min(length, 2048),
            FirstCluster = offset / _blockSize,
            FragmentCount = 1,
            Tag = (IReadOnlyList<FsExtent>)[new FsExtent(0, length, offset, offset / _blockSize, blocks)],
        };
    }

    public override IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(1);
        return [];
    }

    public override IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        foreach (FsEntry m in EnumerateMetadata(cancellationToken))
        {
            yield return m;
        }

        var queue = new Queue<(FsEntry, int)>([(_root, 0)]);
        var seen = new HashSet<string> { _root.FileIdText };
        long done = 0;
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (FsEntry dir, int depth) = queue.Dequeue();
            foreach (FsEntry e in ListDirectory(dir, cancellationToken))
            {
                yield return e;
                if (e.IsDirectory && depth + 1 < MaxDepth && seen.Add(e.FileIdText))
                {
                    queue.Enqueue((e, depth + 1));
                }
            }

            progress?.Report(Math.Min(0.99, ++done / (double)(done + queue.Count + 1)));
        }

        progress?.Report(1);
    }

    public override IReadOnlyList<FsRegion> MetadataRegions =>
    [
        new(0, 16L * SectorBytes, FsRegionKind.Metadata),
        new(16L * SectorBytes, 3L * SectorBytes, FsRegionKind.Metadata),
        new(_avdp * SectorBytes, SectorBytes, FsRegionKind.Metadata),
        new(_mainVds.Location * SectorBytes, _mainVds.Length, FsRegionKind.Metadata),
        new(_reserveVds.Location * SectorBytes, _reserveVds.Length, FsRegionKind.Metadata),
    ];

    // ---- 割り当て ----

    public override IAllocationMap? Allocation
    {
        get
        {
            if (!_allocationRead)
            {
                _allocationRead = true;
                _allocation = ReadSpaceBitmap();
            }

            return _allocation;
        }
    }

    /// <summary>未割り当て領域のビットマップ (パーティションヘッダー記述子の unallocatedSpaceBitmap)。なければ null (強調を無効にする)。</summary>
    private IAllocationMap? ReadSpaceBitmap()
    {
        int physicalRef = _maps.FindIndex(m => m is PhysicalMap);
        if (physicalRef < 0 || !_partitions.TryGetValue(_maps[physicalRef].PartitionNumber, out Partition? part))
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(part.ContentsUse.AsSpan(8)) & 0x3FFFFFFF;
        uint position = BinaryPrimitives.ReadUInt32LittleEndian(part.ContentsUse.AsSpan(12));
        if (length == 0)
        {
            return null;
        }

        long sector = part.Start + position * Math.Max(1, _blockSize / SectorBytes);
        if (ReadTag(sector, out byte[] head) != 264)
        {
            return null;
        }

        long bits = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
        long bytes = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(20));
        if (bits == 0 || bytes < (bits + 7) / 8 || sector * SectorBytes + 24 + bytes > Volume.Length)
        {
            return null;
        }

        IVolumeReader bitmap = Volume.Slice(sector * SectorBytes + 24, bytes);
        long startBlock = part.Start * SectorBytes / _blockSize;
        return new UdfAllocationMap(bitmap, ClusterCount, startBlock, Math.Min(bits, part.Length));
    }

    /// <summary>UDF のビットマップ (1 が空き)。パーティションの外 (記述子の領域) は使用中とする。</summary>
    private sealed class UdfAllocationMap(IVolumeReader bitmap, long clusterCount, long partitionStart, long partitionBlocks) : IAllocationMap
    {
        private readonly CachedVolumeReader _bits = new(bitmap, 4096, 32);

        public long ClusterCount => clusterCount;

        public bool IsAllocated(long cluster)
        {
            long bit = cluster - partitionStart;
            if (cluster < 0 || cluster >= clusterCount)
            {
                return false;
            }

            if (bit < 0 || bit >= partitionBlocks)
            {
                return true;
            }

            Span<byte> b = stackalloc byte[1];
            _bits.Read(bit >> 3, b);
            return (b[0] & (1 << (int)(bit & 7))) == 0;
        }

        public IEnumerable<(long Start, long Count)> FreeRuns(long first, long count, CancellationToken cancellationToken = default)
        {
            long start = Math.Max(first, partitionStart);
            long end = Math.Min(Math.Min(first + count, clusterCount), partitionStart + partitionBlocks);
            long runStart = -1;
            for (long c = start; c < end; c++)
            {
                if ((c & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                bool used = IsAllocated(c);
                if (!used && runStart < 0)
                {
                    runStart = c;
                }
                else if (used && runStart >= 0)
                {
                    yield return (runStart, c - runStart);
                    runStart = -1;
                }
            }

            if (runStart >= 0)
            {
                yield return (runStart, end - runStart);
            }
        }

        public long CountFree(CancellationToken cancellationToken = default) => FreeRuns(0, clusterCount, cancellationToken).Sum(r => r.Count);
    }

    public override FileSystemSummary Summary => new()
    {
        Kind = FileSystemKind.Udf,
        TypeName = $"UDF {RevisionText}",
        Label = _label.Length > 0 ? _label : _volumeId,
        ClusterSize = _blockSize,
        TotalClusters = ClusterCount,
        Details =
        [
            new("UdfRevision", RevisionText),
            new("FileSetRevision", $"{FsdRevision >> 8:X}.{FsdRevision & 0xFF:X2}"),
            new("LogicalVolumeId", _label),
            new("VolumeId", _volumeId),
            new("LogicalBlockSize", _blockSize.ToString(CultureInfo.InvariantCulture)),
            new("AnchorSector", _avdp.ToString(CultureInfo.InvariantCulture)),
            new("PartitionMaps", string.Join(", ", _maps.Select(m => m switch
            {
                MetadataMap => $"metadata ({m.PartitionNumber})",
                PhysicalMap { Sparable: true } => $"sparable ({m.PartitionNumber})",
                PhysicalMap => $"type 1 ({m.PartitionNumber})",
                _ => $"virtual ({m.PartitionNumber})",
            }))),
            new("Partitions", string.Join(", ", _partitions.Values.Select(p => $"{p.Number}: {p.Start} + {p.Length}"))),
            new("IsoBridge", _hasIso ? "yes" : "no"),
        ],
    };
}
