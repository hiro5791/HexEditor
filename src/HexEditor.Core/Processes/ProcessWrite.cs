using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Processes;

/// <summary>書き込みに失敗した範囲 (ENG-34 の仕様 5・「エラー」)。</summary>
public sealed record ProcessWriteFailure(long Offset, long Length, ProcessWriteReason Reason, int ErrorCode = 0);

/// <summary>書き込めなかった理由。</summary>
public enum ProcessWriteReason
{
    /// <summary>割り当てられていない範囲 (ENG-34 の仕様 7)。</summary>
    Unallocated,

    /// <summary>読み取り専用のページで、保護の変更が承認されなかった (仕様 3)。</summary>
    Protected,

    /// <summary>プロセスが終了した。</summary>
    ProcessExited,

    /// <summary>その他の書き込みエラー。</summary>
    WriteError,
}

/// <summary>書き込みの結果。</summary>
public sealed record ProcessWriteResult(IReadOnlyList<SavedRange> Written, IReadOnlyList<ProcessWriteFailure> Failed)
{
    /// <summary>すべての範囲を書けた。</summary>
    public bool AllWritten => Failed.Count == 0;
}

/// <summary>プロセスメモリへの書き込み (ENG-34)。</summary>
public static class ProcessWrite
{
    /// <summary>
    /// 変更範囲をプロセスに書き込む (ENG-34 の仕様 3〜5)。書き込む前に旧内容を追加バッファに退避する (Undo のため)。読み取り専用の
    /// ページは <paramref name="confirmProtectChange"/> で確認し、承認されたら一時的に書き込み可能にして書き、元の保護属性に戻す。
    /// 一部の範囲が失敗しても他の範囲は書く。未割り当ての範囲は書けない (仕様 7)。
    /// </summary>
    /// <param name="confirmProtectChange">読み取り専用のページへの書き込みの確認 (保護属性を渡す)。承認したら true。</param>
    public static ProcessWriteResult Execute(Document document, ProcessMemoryByteSource memory, Func<uint, bool>? confirmProtectChange = null)
    {
        DocumentSnapshot snapshot = document.Current;
        AddBuffer addBuffer = document.AddBuffer;
        var written = new List<SavedRange>();
        var failed = new List<ProcessWriteFailure>();
        byte[] buffer = new byte[ProcessMemoryByteSource.MaxTransfer];
        var protectDecisions = new Dictionary<long, bool>();

        // 現在の版の変更範囲に加えて、前回書き込んだ範囲も含める (Undo 後に旧内容を書き戻すため。ENG-34 の仕様 2 の Undo)。
        IEnumerable<(long Offset, long Length)> dirty = snapshot.EnumerateModifiedRanges().Concat(document.LastDeviceWriteRanges);
        foreach ((long offset, long length) in Merge(dirty))
        {
            long pos = offset;
            while (pos < offset + length)
            {
                MemoryRegion? region = memory.RegionAtOffset(pos);
                long regionEnd = region is null ? offset + length : Math.Min(region.End - memory.BaseAddress, offset + length);
                long chunk = regionEnd - pos;
                if (chunk <= 0)
                {
                    chunk = offset + length - pos;
                    region = null;
                }

                if (region is null || region.State != RegionState.Commit)
                {
                    failed.Add(new ProcessWriteFailure(pos, chunk, ProcessWriteReason.Unallocated));
                    pos += chunk;
                    continue;
                }

                bool changeProtect = false;
                if (!region.IsWritable)
                {
                    if (!protectDecisions.TryGetValue(region.BaseAddress, out bool allow))
                    {
                        allow = confirmProtectChange?.Invoke(region.Protect) ?? false;
                        protectDecisions[region.BaseAddress] = allow;
                    }

                    if (!allow)
                    {
                        failed.Add(new ProcessWriteFailure(pos, chunk, ProcessWriteReason.Protected));
                        pos += chunk;
                        continue;
                    }

                    changeProtect = true;
                }

                (bool ok, int error) = WriteChunk(snapshot, memory, addBuffer, pos, chunk, region, changeProtect, buffer, written);
                if (!ok)
                {
                    ProcessWriteReason reason = memory.HasExited ? ProcessWriteReason.ProcessExited : ProcessWriteReason.WriteError;
                    failed.Add(new ProcessWriteFailure(pos, chunk, reason, error));
                }

                pos += chunk;
            }
        }

        return new ProcessWriteResult(written, failed);
    }

    /// <summary>範囲を昇順にして重なり・隣接をまとめる。</summary>
    private static List<(long Offset, long Length)> Merge(IEnumerable<(long Offset, long Length)> ranges)
    {
        var sorted = ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset).ToList();
        var result = new List<(long Offset, long Length)>();
        foreach ((long offset, long length) in sorted)
        {
            if (result.Count > 0 && offset <= result[^1].Offset + result[^1].Length)
            {
                long end = Math.Max(result[^1].Offset + result[^1].Length, offset + length);
                result[^1] = (result[^1].Offset, end - result[^1].Offset);
            }
            else
            {
                result.Add((offset, length));
            }
        }

        return result;
    }

    private static (bool Ok, int Error) WriteChunk(DocumentSnapshot snapshot, ProcessMemoryByteSource memory, AddBuffer addBuffer,
        long offset, long length, MemoryRegion region, bool changeProtect, byte[] buffer, List<SavedRange> written)
    {
        // 旧内容を読み、追加バッファに退避する (Undo のため。ENG-34 の仕様 4)。
        long firstAt = -1;
        for (long pos = 0; pos < length; pos += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, length - pos);
            ReadResult read = memory.Read(offset + pos, buffer.AsSpan(0, n));
            if (!read.IsComplete)
            {
                return (false, read.Unreadable.Count > 0 ? read.Unreadable[0].ErrorCode : Win32Errors.PartialCopy);
            }

            long at = addBuffer.Append(buffer.AsSpan(0, n));
            firstAt = firstAt < 0 ? at : firstAt;
        }

        uint oldProtect = 0;
        bool protectChanged = false;
        if (changeProtect)
        {
            int error = memory.Memory.Protect(region.BaseAddress, region.Size, PageProtection.MakeWritable(region.Protect), out oldProtect);
            if (error != 0)
            {
                return (false, error);
            }

            protectChanged = true;
        }

        try
        {
            for (long pos = 0; pos < length; pos += buffer.Length)
            {
                int n = (int)Math.Min(buffer.Length, length - pos);
                ReadResult read = snapshot.Read(offset + pos, buffer.AsSpan(0, n));
                if (!read.IsComplete)
                {
                    return (false, Win32Errors.PartialCopy);
                }

                int error = memory.TryWrite(offset + pos, buffer.AsSpan(0, n), out _);
                if (error != 0)
                {
                    return (false, error);
                }
            }
        }
        finally
        {
            if (protectChanged)
            {
                // 元の保護属性に戻す (ENG-34 の仕様 3)。
                memory.Memory.Protect(region.BaseAddress, region.Size, region.Protect, out _);
            }
        }

        written.Add(new SavedRange(offset, length, firstAt));
        return (true, 0);
    }
}
