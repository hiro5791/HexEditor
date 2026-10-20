using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.FileSystems.Ntfs;

/// <summary>$LogFile のリスタート領域 (RSTR ページ 1 つ。FOR-10 の仕様 9)。</summary>
public sealed record NtfsRestartArea(long PageOffset, long CurrentLsn, int LogClients, int SystemPageSize, int LogPageSize, long FileSize,
    int RecordHeaderLength, int PageDataOffset, IReadOnlyList<NtfsLogClient> Clients, bool FixupOk);

/// <summary>ログのクライアント (通常は NTFS)。</summary>
public sealed record NtfsLogClient(string Name, long OldestLsn, long RestartLsn);

/// <summary>ログレコード 1 件 (結果一覧の 1 行)。<see cref="TargetRecord"/> は対象の MFT 番号 (分かる場合)。</summary>
public sealed record NtfsLogRecord(long Lsn, long PreviousLsn, long UndoNextLsn, int RecordType, uint TransactionId, ushort RedoOp, ushort UndoOp,
    ushort RedoLength, ushort UndoLength, long TargetVcn, long? TargetLcn, long? TargetRecord, int Length, long PageOffset)
{
    public string RedoName => NtfsLogFile.OperationName(RedoOp);
    public string UndoName => NtfsLogFile.OperationName(UndoOp);
}

/// <summary>
/// $LogFile の基本の解析 (FOR-10 の仕様 9)。リスタート領域 (RSTR) とレコードページ (RCRD) のログレコードを順に読む。ログの再生はしない。
/// ページは 1 枚ずつ読み、レコードは見つかった順に返す (全体を保持しない)。
/// </summary>
public static class NtfsLogFile
{
    private static readonly string[] Operations =
    [
        "Noop", "CompensationLogRecord", "InitializeFileRecordSegment", "DeallocateFileRecordSegment", "WriteEndOfFileRecordSegment",
        "CreateAttribute", "DeleteAttribute", "UpdateResidentValue", "UpdateNonresidentValue", "UpdateMappingPairs", "DeleteDirtyClusters",
        "SetNewAttributeSizes", "AddIndexEntryRoot", "DeleteIndexEntryRoot", "AddIndexEntryAllocation", "DeleteIndexEntryAllocation",
        "WriteEndOfIndexBuffer", "SetIndexEntryVcnRoot", "SetIndexEntryVcnAllocation", "UpdateFileNameRoot", "UpdateFileNameAllocation",
        "SetBitsInNonresidentBitMap", "ClearBitsInNonresidentBitMap", "HotFix", "EndTopLevelAction", "PrepareTransaction", "CommitTransaction",
        "ForgetTransaction", "OpenNonresidentAttribute", "OpenAttributeTableDump", "AttributeNamesDump", "DirtyPageTableDump",
        "TransactionTableDump", "UpdateRecordDataRoot", "UpdateRecordDataAllocation", "UpdateRelativeDataInIndex", "UpdateRelativeDataInIndex2",
        "ZeroEndOfFileRecord",
    ];

    /// <summary>Redo / Undo の操作名。</summary>
    public static string OperationName(ushort op) => op < Operations.Length ? Operations[op] : $"0x{op:X2}";

    /// <summary>$LogFile の内容 (MFT レコード 2 の名前なしの $DATA)。</summary>
    public static IVolumeReader? Open(NtfsFileSystem fs)
    {
        if (fs.FindById("mft:2") is not { } entry)
        {
            return null;
        }

        IVolumeReader content = fs.OpenContent(entry);

        // 壊れたレコードの大きさ (実サイズ) がランの範囲を大きく超えると、ログのページの走査がランのない (0 の) 範囲を延々と読む。
        // ログはランのある範囲 (割り当てた大きさ) までに限る。
        // ランの長さそのものが壊れていても、ボリュームより大きなログはない。
        if (content is NtfsStreamReader stream && stream.BoundedLength(fs.Volume.Length) is var bounded && bounded < content.Length)
        {
            return new SubVolumeReader(content, 0, bounded);
        }

        return content;
    }

    /// <summary>リスタート領域 (ページ 0 と 1)。</summary>
    public static IReadOnlyList<NtfsRestartArea> ReadRestartAreas(IVolumeReader log, int sectorSize = 512)
    {
        var list = new List<NtfsRestartArea>();
        long pageSize = 4096;
        for (int page = 0; page < 2; page++)
        {
            long offset = page * pageSize;
            byte[] p = log.ReadBytes(offset, (int)pageSize);
            if (!p.AsSpan(0, 4).SequenceEqual("RSTR"u8))
            {
                continue;
            }

            bool fixup = NtfsFormat.ApplyFixup(p, sectorSize);
            int systemPage = (int)BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(16));
            int logPage = (int)BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(20));
            int areaAt = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(24));
            if (systemPage is >= 512 and <= 65536 && (systemPage & (systemPage - 1)) == 0)
            {
                pageSize = systemPage;
            }

            if (areaAt + 48 > p.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> a = p.AsSpan(areaAt);
            long current = BinaryPrimitives.ReadInt64LittleEndian(a);
            int clients = BinaryPrimitives.ReadUInt16LittleEndian(a[8..]);
            int clientArray = BinaryPrimitives.ReadUInt16LittleEndian(a[22..]);
            long fileSize = BinaryPrimitives.ReadInt64LittleEndian(a[24..]);
            int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(a[36..]);
            int dataOffset = BinaryPrimitives.ReadUInt16LittleEndian(a[38..]);
            var clientList = new List<NtfsLogClient>();
            for (int c = 0; c < Math.Min(clients, 8); c++)
            {
                int at = areaAt + clientArray + c * 0xA0;
                if (at + 0x24 > p.Length)
                {
                    break;
                }

                int nameLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(at + 0x20)), 64);
                string name = at + 0x24 + nameLength <= p.Length ? Encoding.Unicode.GetString(p, at + 0x24, nameLength & ~1) : string.Empty;
                clientList.Add(new NtfsLogClient(name, BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(at)), BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(at + 8))));
            }

            list.Add(new NtfsRestartArea(offset, current, clients, systemPage, logPage, fileSize, headerLength, dataOffset, clientList, fixup));
        }

        return list;
    }

    /// <summary>
    /// レコードページのログレコードを順に返す。<paramref name="mftRuns"/> があれば、対象の LCN が $MFT にあるレコードの MFT 番号を求める。
    /// 返す件数は <paramref name="limit"/> まで。
    /// </summary>
    public static IEnumerable<NtfsLogRecord> ReadRecords(IVolumeReader log, IReadOnlyList<NtfsRun>? mftRuns = null, long clusterSize = 4096,
        int recordSize = 1024, int sectorSize = 512, int limit = 1_000_000, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NtfsRestartArea> restart = ReadRestartAreas(log, sectorSize);
        int pageSize = restart.FirstOrDefault()?.LogPageSize is int lp and >= 512 and <= 65536 ? lp : 4096;
        int systemPage = restart.FirstOrDefault()?.SystemPageSize is int sp and >= 512 and <= 65536 ? sp : 4096;
        int dataOffset = restart.FirstOrDefault()?.PageDataOffset is int d and >= 0x28 ? d : 0x40;
        int returned = 0;
        for (long offset = 2L * systemPage; offset + pageSize <= log.Length; offset += pageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] page = log.ReadBytes(offset, pageSize);
            if (!page.AsSpan(0, 4).SequenceEqual("RCRD"u8))
            {
                continue;
            }

            NtfsFormat.ApplyFixup(page, sectorSize);
            int nextRecord = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(24));
            int end = nextRecord is > 0 && nextRecord <= pageSize ? Math.Max(nextRecord, dataOffset) : pageSize;
            int at = dataOffset;
            while (at + 0x30 <= pageSize && at < end + 0x30)
            {
                long lsn = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(at));
                int clientLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at + 24));
                int type = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at + 32));
                if (lsn <= 0 || type is < 1 or > 2 || clientLength is < 0 or > 1 << 20)
                {
                    break;
                }

                long previous = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(at + 8));
                long undoNext = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(at + 16));
                uint transaction = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at + 36));
                ushort redo = 0, undo = 0, redoLength = 0, undoLength = 0;
                long targetVcn = 0;
                long? targetLcn = null, targetRecord = null;
                int c = at + 0x30;
                if (type == 1 && c + 32 <= pageSize)
                {
                    redo = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(c));
                    undo = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(c + 2));
                    redoLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(c + 6));
                    undoLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(c + 10));
                    int lcns = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(c + 14));
                    int clusterBlock = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(c + 20));
                    targetVcn = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(c + 24));
                    if (lcns > 0 && c + 40 <= pageSize)
                    {
                        targetLcn = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(c + 32));
                        targetRecord = MftRecordOf(targetLcn.Value, clusterBlock, mftRuns, clusterSize, recordSize);
                    }
                }

                yield return new NtfsLogRecord(lsn, previous, undoNext, type, transaction, redo, undo, redoLength, undoLength, targetVcn, targetLcn,
                    targetRecord, 0x30 + clientLength, offset);
                if (++returned >= limit)
                {
                    yield break;
                }

                at += (0x30 + clientLength + 7) & ~7;
            }
        }
    }

    private static long? MftRecordOf(long lcn, int clusterBlockOffset, IReadOnlyList<NtfsRun>? runs, long clusterSize, int recordSize)
    {
        if (runs is null || recordSize <= 0)
        {
            return null;
        }

        foreach (NtfsRun run in runs)
        {
            if (run.Lcn is long start && lcn >= start && lcn < start + run.Length)
            {
                long byteInMft = (run.Vcn + (lcn - start)) * clusterSize + clusterBlockOffset * 512L;
                return byteInMft / recordSize;
            }
        }

        return null;
    }
}
