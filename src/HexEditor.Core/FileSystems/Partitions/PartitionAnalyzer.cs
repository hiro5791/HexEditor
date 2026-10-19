using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.FileSystems.Partitions;

/// <summary>一覧の「形式」の列 (FOR-06 の仕様 3)。</summary>
public enum PartitionScheme
{
    Mbr,
    Logical,
    Gpt,
    Apm,
    Bsd,
    Ldm,
    Lvm,
    Unallocated,
}

/// <summary>検査の警告の種類 (FOR-06 の仕様 8 と「エラー」)。文は表示の言語で作る (<c>PartIssue_&lt;種類&gt;</c>)。</summary>
public enum PartitionIssueKind
{
    EbrLoop,
    LogicalLimit,
    GptHeaderCrc,
    GptEntriesCrc,
    GptBackupMismatch,
    GptPrimaryInvalid,
    Overlap,
    OutOfRange,
    HybridMbr,
    ReadError,
    ApmLimit,
}

/// <summary>警告 1 件。<see cref="Row"/> は対象の行の番号 (表全体の警告なら null)。</summary>
public sealed record PartitionIssue(PartitionIssueKind Kind, int? Row = null, long? Lba = null, int? OtherRow = null);

/// <summary>
/// 一覧の 1 行 (FOR-06 の仕様 3)。<see cref="StartLba"/>・<see cref="SectorCount"/> はドキュメントのセクタサイズの単位 (APM は
/// ブロックの単位を <see cref="BlockStart"/>・<see cref="BlockCount"/> にも持つ)。<see cref="EntryOffset"/> はその行を記述する表の
/// エントリのバイト位置 (「エントリの構造を表示」で使う)。
/// </summary>
public sealed record PartitionRow
{
    public int Number { get; init; }
    public PartitionScheme Scheme { get; init; }

    /// <summary>種類 ID (<c>07</c>) または GUID の文字列、APM・BSD の種類の文字列。</summary>
    public string TypeCode { get; init; } = string.Empty;

    /// <summary>種類の名前のリソースのキーの末尾 (知らない種類は null。<see cref="TypeText"/> を表示する)。</summary>
    public string? TypeKey { get; init; }

    /// <summary>訳さない種類の名前 (APM の種類、BSD の fstype)。</summary>
    public string? TypeText { get; init; }

    public string Name { get; init; } = string.Empty;
    public long StartLba { get; init; }
    public long SectorCount { get; init; }
    public int SectorSize { get; init; }
    public string Flags { get; init; } = string.Empty;
    public FileSystemKind FileSystem { get; init; }
    public bool Warning { get; init; }
    public Guid? TypeGuid { get; init; }
    public Guid? UniqueGuid { get; init; }
    public ulong Attributes { get; init; }
    public long? EntryOffset { get; init; }

    /// <summary>エントリの構造を表示する標準テンプレート (<c>mbr</c> / <c>gpt</c>)。</summary>
    public string? Template { get; init; }

    public long? BlockStart { get; init; }
    public long? BlockCount { get; init; }

    /// <summary>拡張パーティションの入れ物 (中の論理パーティションが本体)。</summary>
    public bool IsContainer { get; init; }

    /// <summary>暗号化の検出の結果 (FOR-29。パーティション解析のときに <see cref="PartitionEncryption.Annotate"/> が入れる)。なければ null。</summary>
    public Forensics.Encryption.EncryptionInfo? Encryption { get; init; }

    public long EndLba => StartLba + Math.Max(0, SectorCount) - 1;
    public long StartOffset => StartLba * SectorSize;
    public long Size => SectorCount * SectorSize;
    public bool IsUnallocated => Scheme == PartitionScheme.Unallocated;
}

/// <summary>解析の設定。</summary>
public sealed record PartitionAnalyzerOptions
{
    /// <summary>1 MiB 未満の隙間も表示する (設定「小さな隙間も表示する」、既定オフ。仕様 4)。</summary>
    public bool ShowSmallGaps { get; init; }
}

/// <summary>解析の結果 (FOR-06)。</summary>
public sealed record PartitionAnalysis
{
    public int SectorSize { get; init; }
    public long DiskLength { get; init; }

    /// <summary>パーティションと未割り当ての行 (開始位置の順)。</summary>
    public IReadOnlyList<PartitionRow> Rows { get; init; } = [];

    public IReadOnlyList<PartitionIssue> Issues { get; init; } = [];

    /// <summary>パーティション表が見つからない (仕様の「エラー」。FOR-08 のスキャンへの案内を出す)。</summary>
    public bool NoTable { get; init; }

    /// <summary>GPT が見つからず、別のセクタサイズで見つかった場合のそのセクタサイズ (仕様 2 の提案)。</summary>
    public int? SuggestedSectorSize { get; init; }

    /// <summary>読めなかったセクタの LBA (仕様の「エラー」)。</summary>
    public IReadOnlyList<long> UnreadableLbas { get; init; } = [];

    public uint? DiskSignature { get; init; }
    public Guid? DiskGuid { get; init; }

    /// <summary>パーティションの行 (未割り当て・入れ物を除く)。</summary>
    public IEnumerable<PartitionRow> Partitions => Rows.Where(r => !r.IsUnallocated);

    /// <summary>オフセットを含むパーティション (入れ物より中身を優先する。ステータスバーの番号。仕様 9)。</summary>
    public PartitionRow? PartitionAt(long offset) =>
        Rows.Where(r => !r.IsUnallocated && offset >= r.StartOffset && offset < r.StartOffset + r.Size)
            .OrderBy(r => r.IsContainer ? 1 : 0).ThenBy(r => r.Size).FirstOrDefault();

    /// <summary>警告のある行の番号。</summary>
    public IEnumerable<PartitionIssue> IssuesFor(int number) => Issues.Where(i => i.Row == number || i.OtherRow == number);
}

/// <summary>
/// パーティション構造の解析 (FOR-06)。読むのは各形式の表の数セクタだけで、ディスクの大きさに依存しない。EBR の連鎖は循環を検出し、
/// 論理パーティションは 256 個で打ち切る。読めないセクタは記録して、読めた範囲で一覧を作る。
/// </summary>
public static class PartitionAnalyzer
{
    public const int MaxLogical = 256;
    public const int MaxGptEntries = 4096;
    public const int MaxApmEntries = 256;
    public const long OneMiB = 1024 * 1024;
    public const uint BsdMagic = 0x82564557;

    private sealed class Context(IVolumeReader reader, int sectorSize, CancellationToken token)
    {
        public IVolumeReader Reader { get; } = reader;
        public int SectorSize { get; } = sectorSize;
        public CancellationToken Token { get; } = token;
        public List<PartitionRow> Rows { get; } = [];
        public List<PartitionIssue> Issues { get; } = [];
        public SortedSet<long> Unreadable { get; } = [];
        public long SectorCount => Reader.Length / SectorSize;

        public byte[] Sector(long lba, int count = 1)
        {
            Token.ThrowIfCancellationRequested();
            byte[] data = Reader.ReadBytes(lba * SectorSize, count * SectorSize, out bool complete);
            if (!complete && lba >= 0 && (lba + count) * SectorSize <= Reader.Length)
            {
                Unreadable.Add(lba);
            }

            return data;
        }
    }

    /// <summary>解析する。<paramref name="sectorSize"/> はデータソースの論理セクタサイズ。</summary>
    public static PartitionAnalysis Analyze(IVolumeReader reader, int sectorSize, PartitionAnalyzerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PartitionAnalyzerOptions();
        if (sectorSize < 128)
        {
            sectorSize = 512;
        }

        var c = new Context(reader, sectorSize, cancellationToken);
        uint? signature = null;
        Guid? diskGuid = null;
        bool found = false;
        int? suggested = null;

        byte[] lba0 = reader.Length >= sectorSize ? c.Sector(0) : [];
        bool hasMbr = lba0.Length >= 512 && lba0[510] == 0x55 && lba0[511] == 0xAA;
        bool protective = false;
        bool apm = lba0.Length >= 2 && lba0[0] == (byte)'E' && lba0[1] == (byte)'R';

        if (hasMbr && FileSystemDetector.DetectHead(lba0, reader.Length) is { } volume && volume.Kind != FileSystemKind.Unknown
            && !MbrLooksLikeTable(lba0))
        {
            // パーティション表のないボリューム (ブートセクタの 55 AA)。
            hasMbr = false;
        }

        if (hasMbr)
        {
            signature = BinaryPrimitives.ReadUInt32LittleEndian(lba0.AsSpan(0x1B8));
            protective = Enumerable.Range(0, 4).Any(i => lba0[0x1BE + i * 16 + 4] == 0xEE);
        }

        // GPT (LBA 1 の EFI PART)。保護 MBR がなくても探す。
        GptResult? gpt = ReadGpt(c);
        if (gpt is not null)
        {
            found = true;
            diskGuid = gpt.DiskGuid;
        }
        else
        {
            // 別のセクタサイズで EFI PART があるか (仕様 2)。
            foreach (int other in new[] { 512, 4096 })
            {
                if (other != sectorSize && reader.Length > other + 8
                    && reader.ReadBytes(other, 8).AsSpan().SequenceEqual("EFI PART"u8))
                {
                    suggested = other;
                    break;
                }
            }
        }

        if (hasMbr && (!protective || gpt is null))
        {
            bool onlyProtective = protective && Enumerable.Range(0, 4).All(i => lba0[0x1BE + i * 16 + 4] is 0xEE or 0);
            if (!onlyProtective)
            {
                ReadMbr(c, lba0, hybrid: protective);
                found = true;
            }
        }
        else if (hasMbr && protective && gpt is not null)
        {
            // ハイブリッド MBR: EE 以外のエントリも持つ (両方を表示して警告。仕様 1)。
            if (Enumerable.Range(0, 4).Any(i => lba0[0x1BE + i * 16 + 4] is not (0xEE or 0)))
            {
                ReadMbr(c, lba0, hybrid: true);
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.HybridMbr));
            }
        }

        if (apm)
        {
            found |= ReadApm(c, lba0);
        }

        // BSD ディスクラベル: LBA 1 (オフセット 512) にマジック。
        if (!found || !hasMbr)
        {
            found |= ReadBsd(c, 512, sliceStart: 0, sliceNumber: null);
        }

        // パーティション表のないディスク全体の LDM・LVM2。
        if (!found)
        {
            if (FileSystemDetector.DetectHead(reader.ReadBytes(0, 4 * 512), reader.Length) is { Kind: FileSystemKind.Lvm2 })
            {
                c.Rows.Add(new PartitionRow
                {
                    Number = 1, Scheme = PartitionScheme.Lvm, TypeKey = "Lvm2PhysicalVolume", StartLba = 0, SectorCount = c.SectorCount,
                    SectorSize = sectorSize, FileSystem = FileSystemKind.Lvm2,
                });
                found = true;
            }
        }

        // 推定ファイルシステム (FOR-09 と同じ判定)。入れ物は判定しない。
        for (int i = 0; i < c.Rows.Count; i++)
        {
            PartitionRow row = c.Rows[i];
            if (row.IsContainer || row.FileSystem != FileSystemKind.Unknown || row.SectorCount <= 0 || row.StartOffset >= reader.Length)
            {
                continue;
            }

            FileSystemKind fs = FileSystemDetector.Detect(reader, row.StartOffset, checkBackup: false)?.Kind ?? FileSystemKind.Unknown;
            if (fs == FileSystemKind.Unknown && row.Scheme != PartitionScheme.Ldm && row.TypeGuid == PartitionTypes.LdmMetadata)
            {
                fs = FileSystemKind.Ldm;
            }

            c.Rows[i] = row with { FileSystem = fs };
        }

        // LDM (MBR の 42 は LBA 6 の PRIVHEAD)。
        if (c.Rows.Any(r => r.TypeCode == "42") && reader.ReadBytes(6L * sectorSize, 8).AsSpan().SequenceEqual("PRIVHEAD"u8))
        {
            for (int i = 0; i < c.Rows.Count; i++)
            {
                if (c.Rows[i].TypeCode == "42")
                {
                    c.Rows[i] = c.Rows[i] with { FileSystem = FileSystemKind.Ldm };
                }
            }
        }

        CheckRanges(c);
        List<PartitionRow> rows = [.. c.Rows.OrderBy(r => r.StartLba).ThenBy(r => r.IsContainer ? 0 : 1)];
        if (found)
        {
            rows = AddGaps(rows, c, gpt, options.ShowSmallGaps);
        }

        // 警告のある行に印を付ける。
        var warned = c.Issues.SelectMany(i => new[] { i.Row, i.OtherRow }).Where(n => n is not null).Select(n => n!.Value).ToHashSet();
        rows = [.. rows.Select(r => warned.Contains(r.Number) && !r.IsUnallocated ? r with { Warning = true } : r)];
        if (c.Unreadable.Count > 0)
        {
            c.Issues.Add(new PartitionIssue(PartitionIssueKind.ReadError, Lba: c.Unreadable.Min));
        }

        return new PartitionAnalysis
        {
            SectorSize = sectorSize,
            DiskLength = reader.Length,
            Rows = rows,
            Issues = c.Issues,
            NoTable = !found,
            SuggestedSectorSize = gpt is null ? suggested : null,
            UnreadableLbas = [.. c.Unreadable],
            DiskSignature = signature,
            DiskGuid = diskGuid,
        };
    }

    /// <summary>MBR のエントリが表として成り立つか (FAT のブートセクタの 55 AA と区別する)。</summary>
    private static bool MbrLooksLikeTable(byte[] lba0)
    {
        int used = 0;
        for (int i = 0; i < 4; i++)
        {
            int e = 0x1BE + i * 16;
            byte status = lba0[e];
            byte type = lba0[e + 4];
            if (status is not (0x00 or 0x80))
            {
                return false;
            }

            if (type != 0)
            {
                used++;
                if (BinaryPrimitives.ReadUInt32LittleEndian(lba0.AsSpan(e + 12)) == 0)
                {
                    return false;
                }
            }
        }

        return used > 0;
    }

    private static PartitionRow MbrRow(Context c, byte[] sector, long sectorLba, int index, int number, long baseLba, PartitionScheme scheme)
    {
        int e = 0x1BE + index * 16;
        byte status = sector[e];
        byte type = sector[e + 4];
        long start = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(e + 8));
        long count = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(e + 12));
        string chs = $"CHS {Chs(sector.AsSpan(e + 1))}–{Chs(sector.AsSpan(e + 5))}";
        return new PartitionRow
        {
            Number = number,
            Scheme = scheme,
            TypeCode = type.ToString("X2"),
            TypeKey = PartitionTypes.MbrKey(type),
            StartLba = baseLba + start,
            SectorCount = count,
            SectorSize = c.SectorSize,
            Flags = (status == 0x80 ? "Boot " : string.Empty) + chs,
            EntryOffset = sectorLba * c.SectorSize + e,
            Template = "mbr",
            IsContainer = PartitionTypes.IsExtended(type),
        };
    }

    private static string Chs(ReadOnlySpan<byte> b)
    {
        int head = b[0];
        int sector = b[1] & 0x3F;
        int cylinder = ((b[1] & 0xC0) << 2) | b[2];
        return $"{cylinder}/{head}/{sector}";
    }

    private static void ReadMbr(Context c, byte[] lba0, bool hybrid)
    {
        int logicalNumber = 5;
        for (int i = 0; i < 4; i++)
        {
            byte type = lba0[0x1BE + i * 16 + 4];
            if (type == 0 || hybrid && type == 0xEE)
            {
                continue;
            }

            PartitionRow row = MbrRow(c, lba0, 0, i, i + 1, 0, PartitionScheme.Mbr);
            if (row.TypeCode == "42")
            {
                row = row with { Scheme = PartitionScheme.Mbr, FileSystem = FileSystemKind.Unknown };
            }

            c.Rows.Add(row);
            if (row.IsContainer)
            {
                logicalNumber = ReadExtended(c, row.StartLba, row.SectorCount, logicalNumber);
            }
            else if (PartitionTypes.IsBsdSlice(type))
            {
                ReadBsd(c, row.StartOffset + 512, row.StartLba, row.Number);
                ReadBsd(c, row.StartOffset, row.StartLba, row.Number);
            }
        }
    }

    /// <summary>
    /// EBR の連鎖をたどる (仕様 1)。EBR 内の開始 LBA は各 EBR 相対、次の EBR は拡張パーティションの先頭からの相対。
    /// 同じ EBR を 2 回たどったら打ち切って警告する。論理パーティションは 256 個まで。
    /// </summary>
    private static int ReadExtended(Context c, long extendedStart, long extendedCount, int number)
    {
        var visited = new HashSet<long>();
        long ebr = extendedStart;
        int count = 0;
        while (true)
        {
            c.Token.ThrowIfCancellationRequested();
            if (!visited.Add(ebr))
            {
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.EbrLoop, count > 0 ? number - 1 : null, ebr));
                break;
            }

            if (ebr < 0 || ebr >= c.SectorCount)
            {
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.OutOfRange, Lba: ebr));
                break;
            }

            if (count >= MaxLogical)
            {
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.LogicalLimit));
                break;
            }

            byte[] sector = c.Sector(ebr);
            if (sector[510] != 0x55 || sector[511] != 0xAA)
            {
                break;
            }

            if (sector[0x1BE + 4] != 0)
            {
                c.Rows.Add(MbrRow(c, sector, ebr, 0, number++, ebr, PartitionScheme.Logical) with { IsContainer = false });
                count++;
            }

            byte nextType = sector[0x1BE + 16 + 4];
            if (nextType == 0 || !PartitionTypes.IsExtended(nextType))
            {
                break;
            }

            ebr = extendedStart + BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x1BE + 16 + 8));
        }

        return number;
    }

    private sealed record GptResult(Guid DiskGuid, long FirstUsable, long LastUsable);

    private sealed record GptHeader(byte[] Raw, bool HeaderCrcOk, long Current, long Backup, long FirstUsable, long LastUsable, Guid DiskGuid,
        long EntriesLba, int EntryCount, int EntrySize, uint EntriesCrc)
    {
        public static GptHeader? Parse(byte[] s)
        {
            if (s.Length < 92 || !s.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
            {
                return null;
            }

            int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(12));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(16));
            bool crcOk = false;
            if (headerSize is >= 92 && headerSize <= s.Length)
            {
                byte[] copy = s.AsSpan(0, headerSize).ToArray();
                copy.AsSpan(16, 4).Clear();
                crcOk = Crc32Ieee.Compute(copy) == crc;
            }

            return new GptHeader(s, crcOk,
                BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(24)), BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(32)),
                BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(40)), BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(48)),
                new Guid(s.AsSpan(56, 16)), BinaryPrimitives.ReadInt64LittleEndian(s.AsSpan(72)),
                (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(80))),
                (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(84))),
                BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(88)));
        }

        public bool EntryLayoutValid => EntrySize is >= 128 and <= 4096 && EntrySize % 8 == 0 && EntryCount is > 0 and <= MaxGptEntries;
    }

    private static (byte[]? Entries, bool CrcOk) ReadEntries(Context c, GptHeader h)
    {
        if (!h.EntryLayoutValid || h.EntriesLba <= 0 || h.EntriesLba >= c.SectorCount)
        {
            return (null, false);
        }

        long bytes = (long)h.EntryCount * h.EntrySize;
        int sectors = (int)((bytes + c.SectorSize - 1) / c.SectorSize);
        byte[] raw = c.Sector(h.EntriesLba, sectors);
        byte[] entries = raw.AsSpan(0, (int)bytes).ToArray();
        return (entries, Crc32Ieee.Compute(entries) == h.EntriesCrc);
    }

    private static GptResult? ReadGpt(Context c)
    {
        if (c.SectorCount < 3)
        {
            return null;
        }

        GptHeader? primary = GptHeader.Parse(c.Sector(1));
        long backupLba = primary is { Backup: > 1 } p && p.Backup < c.SectorCount ? p.Backup : c.SectorCount - 1;
        GptHeader? backup = GptHeader.Parse(c.Sector(backupLba));
        if (primary is null && backup is null)
        {
            return null;
        }

        GptHeader use;
        byte[]? entries;
        if (primary is not null && primary.HeaderCrcOk && primary.EntryLayoutValid)
        {
            use = primary;
            (entries, bool entriesOk) = ReadEntries(c, primary);
            if (!entriesOk)
            {
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.GptEntriesCrc, Lba: primary.EntriesLba));
            }

            if (backup is null || !backup.HeaderCrcOk)
            {
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.GptBackupMismatch, Lba: backupLba));
            }
            else
            {
                (byte[]? backupEntries, _) = ReadEntries(c, backup);
                bool same = backup.DiskGuid == primary.DiskGuid && backup.EntriesCrc == primary.EntriesCrc && backup.EntryCount == primary.EntryCount
                    && backup.FirstUsable == primary.FirstUsable && backup.LastUsable == primary.LastUsable && backup.Current == primary.Backup
                    && (backupEntries is null || entries is null || backupEntries.AsSpan().SequenceEqual(entries));
                if (!same)
                {
                    c.Issues.Add(new PartitionIssue(PartitionIssueKind.GptBackupMismatch, Lba: backupLba));
                }
            }
        }
        else if (backup is not null && backup.HeaderCrcOk && backup.EntryLayoutValid)
        {
            // 主ヘッダーが壊れている: バックアップから一覧を作る。
            use = backup;
            c.Issues.Add(new PartitionIssue(PartitionIssueKind.GptPrimaryInvalid, Lba: 1));
            (entries, bool entriesOk) = ReadEntries(c, backup);
            if (!entriesOk)
            {
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.GptEntriesCrc, Lba: backup.EntriesLba));
            }
        }
        else
        {
            GptHeader any = (primary ?? backup)!;
            c.Issues.Add(new PartitionIssue(PartitionIssueKind.GptHeaderCrc, Lba: ReferenceEquals(any, primary) ? 1 : backupLba));
            use = any;
            (entries, _) = ReadEntries(c, any);
        }

        if (entries is not null)
        {
            for (int i = 0; i < use.EntryCount; i++)
            {
                ReadOnlySpan<byte> e = entries.AsSpan(i * use.EntrySize, use.EntrySize);
                var type = new Guid(e[..16]);
                if (type == Guid.Empty)
                {
                    continue;
                }

                long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
                long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
                ulong attributes = BinaryPrimitives.ReadUInt64LittleEndian(e[48..]);
                string name = Encoding.Unicode.GetString(e.Slice(56, Math.Min(72, use.EntrySize - 56))).Split('\0')[0];
                c.Rows.Add(new PartitionRow
                {
                    Number = i + 1,
                    Scheme = PartitionScheme.Gpt,
                    TypeCode = type.ToString("D").ToUpperInvariant(),
                    TypeKey = PartitionTypes.GptKey(type),
                    TypeGuid = type,
                    UniqueGuid = new Guid(e.Slice(16, 16)),
                    Name = name,
                    StartLba = first,
                    SectorCount = last >= first ? last - first + 1 : 0,
                    SectorSize = c.SectorSize,
                    Attributes = attributes,
                    Flags = GptFlags(attributes),
                    EntryOffset = use.EntriesLba * c.SectorSize + (long)i * use.EntrySize,
                    Template = "gpt",
                });
            }
        }

        return new GptResult(use.DiskGuid, use.FirstUsable, use.LastUsable);
    }

    private static string GptFlags(ulong attributes)
    {
        var parts = new List<string>();
        if ((attributes & 1) != 0)
        {
            parts.Add("Required");
        }

        if ((attributes & 4) != 0)
        {
            parts.Add("LegacyBootable");
        }

        if ((attributes & (1UL << 60)) != 0)
        {
            parts.Add("ReadOnly");
        }

        if ((attributes & (1UL << 62)) != 0)
        {
            parts.Add("Hidden");
        }

        if ((attributes & (1UL << 63)) != 0)
        {
            parts.Add("NoDriveLetter");
        }

        return attributes == 0 ? string.Empty : $"0x{attributes:X16}" + (parts.Count > 0 ? " " + string.Join(", ", parts) : string.Empty);
    }

    /// <summary>Apple パーティションマップ (ブロック 0 の ER、ブロック 1 以降の PM)。</summary>
    private static bool ReadApm(Context c, byte[] lba0)
    {
        int blockSize = BinaryPrimitives.ReadUInt16BigEndian(lba0.AsSpan(2));
        if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            blockSize = 512;
        }

        int count = 1;
        int added = 0;
        for (int i = 1; i <= count && i <= MaxApmEntries; i++)
        {
            byte[] b = c.Reader.ReadBytes((long)i * blockSize, 512, out bool complete);
            c.Token.ThrowIfCancellationRequested();
            if (!complete && (long)(i + 1) * blockSize <= c.Reader.Length)
            {
                c.Unreadable.Add((long)i * blockSize / c.SectorSize);
            }

            if (b[0] != (byte)'P' || b[1] != (byte)'M')
            {
                break;
            }

            if (i == 1)
            {
                long mapCount = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
                count = (int)Math.Min(mapCount, MaxApmEntries);
                if (mapCount > MaxApmEntries)
                {
                    c.Issues.Add(new PartitionIssue(PartitionIssueKind.ApmLimit));
                }
            }

            long start = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
            long blocks = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12));
            string name = Encoding.ASCII.GetString(b, 16, 32).Split('\0')[0];
            string type = Encoding.ASCII.GetString(b, 48, 32).Split('\0')[0];
            uint status = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(88));
            c.Rows.Add(new PartitionRow
            {
                Number = i,
                Scheme = PartitionScheme.Apm,
                TypeCode = type,
                TypeText = type,
                Name = name,
                StartLba = start * blockSize / c.SectorSize,
                SectorCount = blocks * blockSize / c.SectorSize,
                SectorSize = c.SectorSize,
                BlockStart = start,
                BlockCount = blocks,
                Flags = status == 0 ? string.Empty : $"0x{status:X8}",
                EntryOffset = (long)i * blockSize,
            });
            added++;
        }

        return added > 0;
    }

    /// <summary>
    /// BSD ディスクラベル (マジック 0x82564557、パーティションは最大 16)。スライスの中のラベルでは、開始がスライスの開始より前なら
    /// スライスからの相対とみなす。
    /// </summary>
    private static bool ReadBsd(Context c, long offset, long sliceStart, int? sliceNumber)
    {
        byte[] b = c.Reader.ReadBytes(offset, 512);
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != BsdMagic || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(132)) != BsdMagic)
        {
            return false;
        }

        if (c.Rows.Any(r => r.Scheme == PartitionScheme.Bsd && r.EntryOffset is long at && at >= offset && at < offset + 512))
        {
            return true;
        }

        int secSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40));
        if (secSize is < 128 or > 65536)
        {
            secSize = c.SectorSize;
        }

        int count = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(138)), 16);
        int added = 0;
        for (int i = 0; i < count; i++)
        {
            int e = 148 + i * 16;
            if (e + 16 > 512)
            {
                break;
            }

            long size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(e));
            long start = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(e + 4));
            byte fsType = b[e + 12];
            if (size == 0)
            {
                continue;
            }

            if (sliceStart > 0 && start < sliceStart)
            {
                start += sliceStart;
            }

            char letter = (char)('a' + i);
            c.Rows.Add(new PartitionRow
            {
                Number = (sliceNumber ?? 0) * 100 + i + 1,
                Scheme = PartitionScheme.Bsd,
                TypeCode = fsType.ToString("D"),
                TypeText = PartitionTypes.BsdFsType(fsType),
                Name = sliceNumber is int s ? $"s{s}{letter}" : letter.ToString(),
                StartLba = start * secSize / c.SectorSize,
                SectorCount = size * secSize / c.SectorSize,
                SectorSize = c.SectorSize,
                EntryOffset = offset + e,
            });
            added++;
        }

        return added > 0;
    }

    /// <summary>重なりと、ディスクの範囲外を指すエントリを調べる (仕様 8)。入れ物と中の論理パーティション、スライスと BSD は重なりとしない。</summary>
    private static void CheckRanges(Context c)
    {
        long sectors = c.SectorCount;
        var rows = c.Rows.Where(r => r.SectorCount > 0).ToList();
        foreach (PartitionRow r in rows)
        {
            if (r.StartLba < 0 || r.EndLba >= sectors)
            {
                c.Issues.Add(new PartitionIssue(PartitionIssueKind.OutOfRange, r.Number, r.StartLba));
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            for (int j = i + 1; j < rows.Count; j++)
            {
                PartitionRow a = rows[i], b = rows[j];
                if (a.IsContainer || b.IsContainer || a.Scheme != b.Scheme && (a.Scheme == PartitionScheme.Bsd || b.Scheme == PartitionScheme.Bsd))
                {
                    continue;
                }

                // ハイブリッド MBR の MBR の行と GPT の行は同じ範囲を指してよい。
                if (a.Scheme != b.Scheme && a.Scheme is PartitionScheme.Mbr or PartitionScheme.Gpt && b.Scheme is PartitionScheme.Mbr or PartitionScheme.Gpt)
                {
                    continue;
                }

                if (a.StartLba <= b.EndLba && b.StartLba <= a.EndLba)
                {
                    c.Issues.Add(new PartitionIssue(PartitionIssueKind.Overlap, a.Number, a.StartLba, b.Number));
                }
            }

            if (c.Rows.Count > 512)
            {
                break;
            }
        }
    }

    /// <summary>未割り当ての領域の行 (仕様 4): 最上位のパーティションの間の隙間と、最後のパーティションの後。</summary>
    private static List<PartitionRow> AddGaps(List<PartitionRow> rows, Context c, GptResult? gpt, bool showSmall)
    {
        long sectors = c.SectorCount;
        long first = gpt is not null ? Math.Max(1, gpt.FirstUsable) : 1;
        long last = gpt is not null && gpt.LastUsable < sectors ? gpt.LastUsable : sectors - 1;
        var top = rows.Where(r => r.Scheme is not (PartitionScheme.Logical or PartitionScheme.Bsd) && r.SectorCount > 0)
            .OrderBy(r => r.StartLba).ToList();
        if (top.Count == 0)
        {
            top = [.. rows.Where(r => r.SectorCount > 0).OrderBy(r => r.StartLba)];
        }

        var gaps = new List<PartitionRow>();
        long cursor = first;
        foreach (PartitionRow r in top)
        {
            if (r.StartLba > cursor)
            {
                AddGap(gaps, c, cursor, r.StartLba - cursor, showSmall);
            }

            cursor = Math.Max(cursor, r.EndLba + 1);
        }

        if (last >= cursor)
        {
            AddGap(gaps, c, cursor, last - cursor + 1, showSmall);
        }

        return [.. rows.Concat(gaps).OrderBy(r => r.StartLba).ThenBy(r => r.IsUnallocated ? 1 : 0).ThenBy(r => r.IsContainer ? 0 : 1)];
    }

    private static void AddGap(List<PartitionRow> gaps, Context c, long start, long count, bool showSmall)
    {
        if (count <= 0 || !showSmall && count * c.SectorSize < OneMiB)
        {
            return;
        }

        gaps.Add(new PartitionRow
        {
            Number = 0,
            Scheme = PartitionScheme.Unallocated,
            TypeKey = "Unallocated",
            StartLba = start,
            SectorCount = count,
            SectorSize = c.SectorSize,
        });
    }
}
