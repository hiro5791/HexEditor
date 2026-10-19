using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Fat;
using HexEditor.Core.FileSystems.Ntfs;
using HexEditor.Core.Tools.Files;

namespace HexEditor.Core.Wiping;

/// <summary>墨塗りで上書きする範囲の種類 (<c>redaction-log.csv</c> の Kind)。</summary>
public enum RedactionRangeKind
{
    /// <summary>データの断片 (圧縮されたファイルは圧縮された断片)。</summary>
    Data,

    /// <summary>スラック (最後の断片の、データの終わりから割り当てたクラスタの終わりまで)。</summary>
    Slack,

    /// <summary>NTFS の常駐データ (MFT レコードの中)。</summary>
    Resident,

    /// <summary>名前 (ディレクトリエントリ・MFT の $FILE_NAME・インデックスのエントリ)。</summary>
    Name,
}

/// <summary>上書きする範囲 1 つ (ドキュメント上の位置)。</summary>
public sealed record RedactionRange(long Offset, long Length, RedactionRangeKind Kind);

/// <summary>墨塗りするファイル 1 つ。<see cref="Warnings"/> は名前を置き換えられなかった理由など。</summary>
public sealed record RedactionItem(string Path, string FileId, IReadOnlyList<RedactionRange> Ranges, IReadOnlyList<string> Warnings)
{
    public long Bytes => Ranges.Where(r => r.Kind != RedactionRangeKind.Name).Sum(r => r.Length);
}

/// <summary>対象外にした理由 (仕様「エラー」: 断片の情報が壊れているファイルは対象外にし、理由を一覧に示す)。</summary>
public enum RedactionExclusionReason
{
    Directory,

    /// <summary>削除された項目 (クラスタがほかのファイルに再利用されているおそれがある)。</summary>
    Deleted,

    /// <summary>メタデータが壊れている。</summary>
    Corrupt,

    /// <summary>断片の位置がクラスタの領域の外、未割り当て、またはメタデータの範囲に重なる。</summary>
    BadFragments,

    /// <summary>常駐データの位置が分からない。</summary>
    ResidentNotFound,

    /// <summary>ファイルシステムのメタデータファイル・仮想フォルダ。</summary>
    Metadata,
}

public sealed record RedactionExclusion(string Path, string FileId, RedactionExclusionReason Reason, string Detail = "");

/// <summary>
/// 墨塗りの計画 (確認の後に <see cref="FileSystemRedaction.Apply"/> でデータエンジンの編集として行う)。<see cref="Fills"/> はパターンで塗る範囲
/// (データとスラック)、<see cref="Patches"/> は構造 (MFT レコード・INDX・ディレクトリエントリ) を書き換えた内容。
/// </summary>
public sealed record RedactionPlan
{
    public required IReadOnlyList<RedactionItem> Items { get; init; }

    public required IReadOnlyList<RedactionExclusion> Excluded { get; init; }

    public required ShredPass Pattern { get; init; }

    public bool ReplaceNames { get; init; }

    public required IReadOnlyList<WipeRange> Fills { get; init; }

    public required IReadOnlyList<(long Offset, byte[] Bytes)> Patches { get; init; }

    public long TotalBytes => Items.Sum(i => i.Bytes);
}

/// <summary>
/// ファイルシステムを理解した墨塗り (FOR-32)。選んだファイルのデータの断片とスラックを、ファイルシステムの構造 (サイズ・日時・断片の位置) を
/// 保ったまま指定パターンで上書きする。NTFS の常駐データ (MFT レコード内) も対象にし、オプションで名前を同じ長さの <c>_</c> に置き換える
/// (FAT の LFN のチェックサム、exFAT の NameHash と SetChecksum、NTFS のフィックスアップを合わせて直す)。編集はデータエンジン上で行う
/// (保存するまで書き込まない、Undo できる)。
/// </summary>
public static class FileSystemRedaction
{
    public const string LogFileName = "redaction-log.csv";

    /// <summary>Undo の説明 (UI-xx の Undo の名前)。</summary>
    public const string UndoDescription = "墨塗り";

    /// <summary>
    /// 計画を作る。<paramref name="document"/> はドキュメントの今の内容 (構造の書き換えはこの内容に重ねる)、<paramref name="volumeOffset"/> は
    /// ファイルシステムの先頭のドキュメント上の位置。
    /// </summary>
    public static RedactionPlan Plan(IFileSystem fs, long volumeOffset, IVolumeReader document, IReadOnlyList<FsEntry> entries, ShredPass pattern,
        bool replaceNames, CancellationToken cancellationToken = default)
    {
        var items = new List<RedactionItem>();
        var excluded = new List<RedactionExclusion>();
        var fills = new List<WipeRange>();
        var structures = new StructureEdits(document);
        IAllocationMap? allocation = fs.Allocation;
        FsRegion[] metadata = [.. fs.MetadataRegions];
        long clusterSize = fs.ClusterSize;
        foreach (FsEntry entry in entries.DistinctBy(e => e.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = entry.Path.Length > 0 ? entry.Path : entry.OriginalPath;
            if (entry.Flags.HasFlag(FsEntryFlags.Virtual) || entry.Flags.HasFlag(FsEntryFlags.Metadata))
            {
                excluded.Add(new(path, entry.FileIdText, RedactionExclusionReason.Metadata));
                continue;
            }

            if (entry.IsDirectory && !entry.Flags.HasFlag(FsEntryFlags.Stream))
            {
                excluded.Add(new(path, entry.FileIdText, RedactionExclusionReason.Directory));
                continue;
            }

            if (entry.State != FsEntryState.Normal)
            {
                excluded.Add(new(path, entry.FileIdText, RedactionExclusionReason.Deleted));
                continue;
            }

            if (entry.Flags.HasFlag(FsEntryFlags.Corrupt))
            {
                excluded.Add(new(path, entry.FileIdText, RedactionExclusionReason.Corrupt, entry.Corruption.ToString()));
                continue;
            }

            var ranges = new List<RedactionRange>();
            var warnings = new List<string>();
            var itemFills = new List<WipeRange>();
            bool bad = false;
            IReadOnlyList<FsExtent> extents = fs.GetExtents(entry);
            foreach (FsExtent e in extents)
            {
                if (e.IsSparse)
                {
                    continue;
                }

                if (e.Resident)
                {
                    continue;
                }

                long v = e.VolumeOffset!.Value;
                long clusters = e.ClusterCount > 0 ? e.ClusterCount : (e.DiskLength + clusterSize - 1) / clusterSize;
                long allocated = clusters * clusterSize;
                if (allocated <= 0)
                {
                    continue;
                }

                if (!FileSystemWipe.InsideAllocated(fs, allocation, v, allocated) || metadata.Any(m => m.Offset < v + allocated && v < m.End))
                {
                    bad = true;
                    break;
                }

                long data = e.Compressed ? Math.Min(allocated, e.DiskLength) : Math.Clamp(Math.Min(e.Length, entry.Size - e.LogicalOffset), 0, allocated);
                if (data > 0)
                {
                    ranges.Add(new RedactionRange(volumeOffset + v, data, RedactionRangeKind.Data));
                }

                if (allocated > data)
                {
                    ranges.Add(new RedactionRange(volumeOffset + v + data, allocated - data, RedactionRangeKind.Slack));
                }

                itemFills.Add(new WipeRange(volumeOffset + v, allocated));
            }

            if (bad)
            {
                excluded.Add(new(path, entry.FileIdText, RedactionExclusionReason.BadFragments));
                continue;
            }

            // 常駐データ (NTFS の MFT レコードの中。仕様 4)。
            if (extents.Any(x => x.Resident) || entry.Flags.HasFlag(FsEntryFlags.Resident))
            {
                if (fs is not NtfsFileSystem ntfs || !RedactResident(ntfs, volumeOffset, entry, pattern, structures, ranges))
                {
                    if (entry.Size > 0)
                    {
                        excluded.Add(new(path, entry.FileIdText, RedactionExclusionReason.ResidentNotFound));
                        structures.Rollback();
                        continue;
                    }
                }
            }

            if (replaceNames)
            {
                string? problem = fs switch
                {
                    NtfsFileSystem n => RenameNtfs(n, volumeOffset, entry, structures, ranges),
                    ExFatFileSystem => RenameExFat(volumeOffset, entry, structures, ranges),
                    FatFileSystem => RenameFat(volumeOffset, entry, structures, ranges),
                    _ => "unsupported",
                };
                if (problem is not null)
                {
                    warnings.Add(problem);
                }
            }

            structures.Commit();
            fills.AddRange(itemFills);
            items.Add(new RedactionItem(path, entry.FileIdText, ranges, warnings));
        }

        return new RedactionPlan
        {
            Items = items,
            Excluded = excluded,
            Pattern = pattern,
            ReplaceNames = replaceNames,
            Fills = WipeEngine.Normalize(fills),
            Patches = structures.Patches(),
        };
    }

    /// <summary>
    /// 墨塗りを行う (仕様 2・5・6): 読み取り専用のドキュメント (書き込み保護モード) と証拠管理の拒否を確かめ、編集として適用し、
    /// <c>redaction-log.csv</c> を書いて監査ログに記録する。拒否なら監査ログに「拒否」を記録して <see cref="WipeRefusedException"/>。
    /// </summary>
    public static void Run(Document document, RedactionPlan plan, string targetName, string? logPath, DateTimeOffset time, IWipeForensicHooks? hooks = null,
        string description = UndoDescription)
    {
        hooks ??= WipeForensics.Hooks;
        var details = new Dictionary<string, string>
        {
            ["files"] = plan.Items.Count.ToString(CultureInfo.InvariantCulture),
            ["excluded"] = plan.Excluded.Count.ToString(CultureInfo.InvariantCulture),
            ["bytes"] = plan.TotalBytes.ToString(CultureInfo.InvariantCulture),
            ["pattern"] = plan.Pattern.Pattern is { } p ? Convert.ToHexString(p) : "random",
            ["names"] = plan.ReplaceNames ? "true" : "false",
            ["paths"] = string.Join(";", plan.Items.Take(100).Select(i => i.Path)),
        };
        if (logPath is not null)
        {
            details["log"] = logPath;
        }

        WipeRefusal refusal = document.IsReadOnly ? WipeRefusal.WriteBlock : WipeRefusal.None;
        string? reason = null;
        if (refusal == WipeRefusal.None && hooks.EnsureCanWrite(WipeAuditOperations.Redact, targetName) is { } denied)
        {
            refusal = WipeRefusal.PolicyDenied;
            reason = denied;
        }

        if (refusal != WipeRefusal.None)
        {
            details["refusal"] = refusal.ToString();
            hooks.Audit(WipeAuditOperations.Redact, targetName, WipeAuditOutcome.Refused, details);
            throw new WipeRefusedException(refusal, reason);
        }

        try
        {
            Apply(document, plan, description);
            if (logPath is not null)
            {
                WriteLog(logPath, plan, targetName, time);
            }
        }
        catch (Exception ex)
        {
            details["error"] = ex.Message;
            hooks.Audit(WipeAuditOperations.Redact, targetName, WipeAuditOutcome.Failed, details);
            throw;
        }

        hooks.Audit(WipeAuditOperations.Redact, targetName, WipeAuditOutcome.Succeeded, details);
    }

    /// <summary>
    /// 計画をドキュメントの編集として行う (1 つの Undo の単位。仕様 2)。データとスラックは塗りつぶしピースで表すため、範囲の大きさによらず速い。
    /// </summary>
    public static void Apply(Document document, RedactionPlan plan, string description = UndoDescription)
    {
        using IDisposable group = document.BeginGroup(description);
        byte[]? fill = plan.Pattern.Pattern;
        foreach (WipeRange r in plan.Fills)
        {
            if (fill is null)
            {
                document.OverwriteRandom(r.Offset, r.Length, BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)), description);
            }
            else
            {
                document.OverwritePattern(r.Offset, r.Length, fill, description);
            }
        }

        foreach ((long offset, byte[] bytes) in plan.Patches)
        {
            document.Overwrite(offset, bytes, description);
        }
    }

    /// <summary>
    /// <c>redaction-log.csv</c> を書く (仕様 5: パス、ファイル ID、上書きした範囲)。ファイルがあれば行を追加する。位置はドキュメント上のバイト位置。
    /// </summary>
    public static void WriteLog(string path, RedactionPlan plan, string image, DateTimeOffset time)
    {
        bool exists = File.Exists(path);
        var s = new StringBuilder();
        if (!exists)
        {
            s.Append("TimeUtc,Image,Path,FileId,Kind,Offset,Length,Pattern,Status,Reason\r\n");
        }

        string when = time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string pattern = plan.Pattern.Pattern is { } p ? Convert.ToHexString(p) : "random";
        foreach (RedactionItem item in plan.Items)
        {
            foreach (RedactionRange r in item.Ranges)
            {
                s.Append(when).Append(',').Append(Csv(image)).Append(',').Append(Csv(item.Path)).Append(',').Append(Csv(item.FileId)).Append(',')
                    .Append(r.Kind.ToString().ToLowerInvariant()).Append(',').Append(r.Offset.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.Length.ToString(CultureInfo.InvariantCulture)).Append(',').Append(r.Kind == RedactionRangeKind.Name ? "5F" : pattern)
                    .Append(",redacted,").Append(Csv(string.Join("; ", item.Warnings))).Append("\r\n");
            }

            if (item.Ranges.Count == 0)
            {
                s.Append(when).Append(',').Append(Csv(image)).Append(',').Append(Csv(item.Path)).Append(',').Append(Csv(item.FileId))
                    .Append(",,,,,redacted,empty\r\n");
            }
        }

        foreach (RedactionExclusion x in plan.Excluded)
        {
            s.Append(when).Append(',').Append(Csv(image)).Append(',').Append(Csv(x.Path)).Append(',').Append(Csv(x.FileId)).Append(",,,,,excluded,")
                .Append(Csv(x.Detail.Length > 0 ? $"{x.Reason} ({x.Detail})" : x.Reason.ToString())).Append("\r\n");
        }

        File.AppendAllText(path, s.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: !exists));
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    // ---- NTFS ----

    private static bool RedactResident(NtfsFileSystem ntfs, long volumeOffset, FsEntry entry, ShredPass pattern, StructureEdits structures,
        List<RedactionRange> ranges)
    {
        if (entry.Tag is not NtfsTag tag || ntfs.Load(tag.Record) is not { } file)
        {
            return false;
        }

        bool found = false;
        foreach (NtfsAttribute a in file.Attributes.Where(a => a.Type == NtfsAttributeType.Data && !a.NonResident && a.Name == tag.Stream))
        {
            if (ntfs.RecordOffset(a.RecordNumber) is not long record)
            {
                return false;
            }

            Structure s = structures.Get(volumeOffset + record, ntfs.RecordSize, fixupSectorSize: ntfs.BytesPerSector);
            if (!s.Valid || a.ValueOffset + a.Value.Length > s.Logical.Length)
            {
                return false;
            }

            Span<byte> value = s.Logical.AsSpan(a.ValueOffset, a.Value.Length);
            if (pattern.Pattern is null)
            {
                RandomNumberGenerator.Fill(value);
            }
            else
            {
                SecureDelete.Fill(pattern, value, 0);
            }

            s.Touch();
            if (a.Value.Length > 0)
            {
                ranges.Add(new RedactionRange(volumeOffset + record + a.ValueOffset, a.Value.Length, RedactionRangeKind.Resident));
            }

            found = true;
        }

        return found || entry.Size == 0;
    }

    /// <summary>NTFS の名前: MFT の $FILE_NAME すべてと、親ディレクトリのインデックス (INDEX_ROOT と INDX) のエントリ。問題があれば理由。</summary>
    private static string? RenameNtfs(NtfsFileSystem ntfs, long volumeOffset, FsEntry entry, StructureEdits structures, List<RedactionRange> ranges)
    {
        if (entry.Tag is not NtfsTag tag || tag.Stream.Length > 0)
        {
            return "names of named streams are not replaced";
        }

        if (ntfs.Load(tag.Record) is not { } file)
        {
            return "the MFT record could not be read";
        }

        var parents = new HashSet<long>();
        foreach (NtfsAttribute a in file.Attributes.Where(a => a.Type == NtfsAttributeType.FileName && !a.NonResident && a.Value.Length >= 0x42))
        {
            if (ntfs.RecordOffset(a.RecordNumber) is not long record)
            {
                continue;
            }

            Structure s = structures.Get(volumeOffset + record, ntfs.RecordSize, fixupSectorSize: ntfs.BytesPerSector);
            if (!s.Valid)
            {
                return "the MFT record could not be read";
            }

            int at = a.ValueOffset;
            int length = s.Logical[at + 0x40];
            if (at + 0x42 + length * 2 > s.Logical.Length)
            {
                continue;
            }

            Underscore(s.Logical.AsSpan(at + 0x42, length * 2));
            s.Touch();
            ranges.Add(new RedactionRange(volumeOffset + record + at + 0x42, length * 2, RedactionRangeKind.Name));
            parents.Add((long)(BinaryPrimitives.ReadUInt64LittleEndian(s.Logical.AsSpan(at)) & 0xFFFFFFFFFFFF));
        }

        int replaced = 0;
        foreach (long parent in parents)
        {
            if (ntfs.Load(parent) is not { } dir)
            {
                continue;
            }

            // INDEX_ROOT (親の MFT レコードの中)。
            foreach (NtfsAttribute root in dir.Attributes.Where(a => a.Type == NtfsAttributeType.IndexRoot && !a.NonResident && a.Name == "$I30"))
            {
                if (ntfs.RecordOffset(root.RecordNumber) is not long record)
                {
                    continue;
                }

                Structure s = structures.Get(volumeOffset + record, ntfs.RecordSize, fixupSectorSize: ntfs.BytesPerSector);
                if (!s.Valid || root.Value.Length < 32)
                {
                    continue;
                }

                int header = root.ValueOffset + 16;
                int start = header + (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Logical.AsSpan(header));
                int end = Math.Min(root.ValueOffset + root.Value.Length, header + (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Logical.AsSpan(header + 4)));
                replaced += RenameIndexEntries(s, start, end, tag.Record, volumeOffset + record, ranges);
            }

            // INDEX_ALLOCATION (INDX レコード)。
            List<NtfsAttribute> allocation = [.. dir.Attributes.Where(a => a.Type == NtfsAttributeType.IndexAllocation && a.NonResident && a.Name == "$I30")
                .OrderBy(a => a.StartVcn)];
            if (allocation.Count > 0)
            {
                var reader = new NtfsStreamReader(ntfs.Volume, [.. allocation.SelectMany(a => a.Runs)], ntfs.ClusterSize, allocation[0].RealSize,
                    allocation[0].InitializedSize, 0);
                long blocks = Math.Min(reader.Length / ntfs.IndexRecordSize, 1 << 20);
                for (long b = 0; b < blocks; b++)
                {
                    // INDX レコードがディスク上で続いている場合だけ書き換える (クラスタがインデックスのレコードより小さく、途中で離れていることがある)。
                    if (reader.VolumeOffsetOf(b * ntfs.IndexRecordSize) is not long blockOffset
                        || reader.VolumeOffsetOf((b + 1) * ntfs.IndexRecordSize - 1) != blockOffset + ntfs.IndexRecordSize - 1)
                    {
                        continue;
                    }

                    Structure s = structures.Get(volumeOffset + blockOffset, ntfs.IndexRecordSize, fixupSectorSize: ntfs.BytesPerSector, signature: "INDX"u8);
                    if (!s.Valid)
                    {
                        continue;
                    }

                    int start = 0x18 + (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Logical.AsSpan(0x18));
                    int end = Math.Min(ntfs.IndexRecordSize, 0x18 + (int)BinaryPrimitives.ReadUInt32LittleEndian(s.Logical.AsSpan(0x1C)));
                    replaced += RenameIndexEntries(s, start, end, tag.Record, volumeOffset + blockOffset, ranges);
                }
            }
        }

        return replaced == 0 ? "the directory index entry was not found" : null;
    }

    private static int RenameIndexEntries(Structure s, int start, int end, long record, long structureOffset, List<RedactionRange> ranges)
    {
        int count = 0;
        int at = start;
        if (start < 0 || end > s.Logical.Length)
        {
            return 0;
        }

        for (int n = 0; n < 65536 && at + 16 <= end; n++)
        {
            ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(s.Logical.AsSpan(at));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(s.Logical.AsSpan(at + 8));
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(s.Logical.AsSpan(at + 10));
            int flags = BinaryPrimitives.ReadUInt16LittleEndian(s.Logical.AsSpan(at + 12));
            if (length < 16 || at + length > end || (flags & 2) != 0)
            {
                break;
            }

            if ((long)(reference & 0xFFFFFFFFFFFF) == record && keyLength >= 0x42 && 16 + keyLength <= length)
            {
                int key = at + 16;
                int nameLength = s.Logical[key + 0x40];
                if (key + 0x42 + nameLength * 2 <= at + length)
                {
                    Underscore(s.Logical.AsSpan(key + 0x42, nameLength * 2));
                    s.Touch();
                    ranges.Add(new RedactionRange(structureOffset + key + 0x42, nameLength * 2, RedactionRangeKind.Name));
                    count++;
                }
            }

            at += length;
        }

        return count;
    }

    /// <summary>UTF-16 の文字をすべて <c>_</c> にする。</summary>
    private static void Underscore(Span<byte> utf16)
    {
        for (int i = 0; i + 1 < utf16.Length; i += 2)
        {
            utf16[i] = 0x5F;
            utf16[i + 1] = 0;
        }
    }

    // ---- FAT ----

    private static readonly int[] LfnSlots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];

    private static string? RenameFat(long volumeOffset, FsEntry entry, StructureEdits structures, List<RedactionRange> ranges)
    {
        if (entry.MetadataOffset is not long first || entry.MetadataLength < 32 || entry.MetadataLength % 32 != 0)
        {
            return "the directory entry was not found";
        }

        int count = entry.MetadataLength / 32;
        Structure s = structures.Get(volumeOffset + first, entry.MetadataLength);
        byte[] b = s.Logical;
        int sfn = (count - 1) * 32;
        byte checksum = FatTime.LfnChecksum(b.AsSpan(sfn, 11));
        for (int i = 0; i < count - 1; i++)
        {
            // LFN のエントリが続いているか (属性 0x0F、チェックサムが短い名前と合う)。合わなければ途中で切れたディレクトリなので書き換えない。
            if (b[i * 32 + 11] != 0x0F || b[i * 32 + 13] != checksum)
            {
                return "the long file name entries are not contiguous";
            }
        }

        for (int i = 0; i < 11; i++)
        {
            if (b[sfn + i] != 0x20)
            {
                b[sfn + i] = 0x5F;
            }
        }

        byte newChecksum = FatTime.LfnChecksum(b.AsSpan(sfn, 11));
        ranges.Add(new RedactionRange(volumeOffset + first + sfn, 11, RedactionRangeKind.Name));
        for (int i = 0; i < count - 1; i++)
        {
            int at = i * 32;
            foreach (int slot in LfnSlots)
            {
                ushort unit = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + slot));
                if (unit is not 0x0000 and not 0xFFFF)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + slot), 0x005F);
                }
            }

            b[at + 13] = newChecksum;
            ranges.Add(new RedactionRange(volumeOffset + first + at, 32, RedactionRangeKind.Name));
        }

        s.Touch();
        return null;
    }

    // ---- exFAT ----

    private static string? RenameExFat(long volumeOffset, FsEntry entry, StructureEdits structures, List<RedactionRange> ranges)
    {
        if (entry.MetadataOffset is not long first || entry.MetadataLength < 96 || entry.MetadataLength % 32 != 0)
        {
            return "the directory entry set was not found";
        }

        int count = entry.MetadataLength / 32;
        Structure s = structures.Get(volumeOffset + first, entry.MetadataLength);
        byte[] b = s.Logical;
        if (b[0] != 0x85 || b[32] != 0xC0 || b[1] != count - 1 || Enumerable.Range(2, count - 2).Any(i => b[i * 32] != 0xC1))
        {
            return "the directory entry set is not contiguous";
        }

        int nameLength = b[32 + 3];
        int written = 0;
        for (int i = 2; i < count && written < nameLength; i++)
        {
            for (int c = 0; c < 15 && written < nameLength; c++, written++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 32 + 2 + c * 2), 0x005F);
            }

            ranges.Add(new RedactionRange(volumeOffset + first + i * 32 + 2, 30, RedactionRangeKind.Name));
        }

        // NameHash (大文字にした名前。'_' はそのまま) と SetChecksum を直す。
        ushort hash = 0;
        for (int i = 0; i < nameLength; i++)
        {
            foreach (byte x in (byte[])[0x5F, 0x00])
            {
                hash = (ushort)((((hash & 1) != 0 ? 0x8000 : 0) | (hash >> 1)) + x);
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32 + 4), hash);
        ushort sum = 0;
        for (int i = 0; i < count * 32; i++)
        {
            if (i is 2 or 3)
            {
                continue;
            }

            sum = (ushort)((((sum & 1) != 0 ? 0x8000 : 0) | (sum >> 1)) + b[i]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), sum);
        s.Touch();
        return null;
    }

    // ---- 構造の書き換え (フィックスアップに対応) ----

    /// <summary>
    /// 構造 1 つ (MFT レコード・INDX レコード・ディレクトリエントリの並び)。<see cref="Logical"/> はフィックスアップを適用した内容で、書き換えた後に
    /// <see cref="StructureEdits.Patches"/> がディスク上の形 (各セクタの末尾を更新シーケンス番号に戻し、元の値を配列に入れる) に戻す。
    /// </summary>
    private sealed class Structure
    {
        public required long Offset { get; init; }
        public required byte[] Raw { get; init; }
        public required byte[] Logical { get; init; }
        public int FixupSectorSize { get; init; }
        public bool Valid { get; init; }
        public bool Changed { get; private set; }
        public byte[]? Snapshot { get; set; }

        public void Touch() => Changed = true;

        public void Reset(byte[] logical, bool changed)
        {
            logical.CopyTo(Logical, 0);
            Changed = changed;
        }

        public byte[] ToRaw()
        {
            byte[] raw = (byte[])Logical.Clone();
            if (FixupSectorSize <= 0)
            {
                return raw;
            }

            int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(4));
            int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(6));
            for (int i = 1; i < usaCount; i++)
            {
                int end = i * FixupSectorSize - 2;
                raw[usaOffset + i * 2] = Logical[end];
                raw[usaOffset + i * 2 + 1] = Logical[end + 1];
                raw[end] = Logical[usaOffset];
                raw[end + 1] = Logical[usaOffset + 1];
            }

            return raw;
        }
    }

    /// <summary>構造の書き換えをまとめる (同じ MFT レコード・INDX レコードを複数のファイルが書き換えても 1 つにする)。</summary>
    private sealed class StructureEdits(IVolumeReader document)
    {
        private readonly Dictionary<long, Structure> _items = [];
        private readonly List<(Structure Item, byte[] Logical, bool Changed)> _undo = [];

        public Structure Get(long offset, int length, int fixupSectorSize = 0, ReadOnlySpan<byte> signature = default)
        {
            if (_items.TryGetValue(offset, out Structure? existing) && existing.Raw.Length == length)
            {
                Remember(existing);
                return existing;
            }

            byte[] raw = document.ReadBytes(offset, length, out bool complete);
            byte[] logical = (byte[])raw.Clone();
            bool valid = complete;
            if (fixupSectorSize > 0)
            {
                ReadOnlySpan<byte> expected = signature.IsEmpty ? "FILE"u8 : signature;
                valid = valid && raw.AsSpan(0, 4).SequenceEqual(expected) && NtfsFormat.ApplyFixup(logical, fixupSectorSize);
            }

            var item = new Structure { Offset = offset, Raw = raw, Logical = logical, FixupSectorSize = fixupSectorSize, Valid = valid };
            _items[offset] = item;
            Remember(item);
            return item;
        }

        private void Remember(Structure item)
        {
            if (!_undo.Any(u => ReferenceEquals(u.Item, item)))
            {
                _undo.Add((item, (byte[])item.Logical.Clone(), item.Changed));
            }
        }

        /// <summary>このファイルの書き換えを確定する。</summary>
        public void Commit() => _undo.Clear();

        /// <summary>このファイルの書き換えを取り消す (対象外にしたファイル)。</summary>
        public void Rollback()
        {
            foreach ((Structure item, byte[] logical, bool changed) in _undo)
            {
                item.Reset(logical, changed);
            }

            _undo.Clear();
        }

        public IReadOnlyList<(long Offset, byte[] Bytes)> Patches() =>
            [.. _items.Values.Where(s => s.Changed && s.Valid).OrderBy(s => s.Offset).Select(s => (s.Offset, s.ToRaw()))];
    }
}
