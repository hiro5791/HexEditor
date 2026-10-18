using HexEditor.Core.Engine;
using HexEditor.Core.Processes;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Remote.Gdb;

/// <summary>
/// GDB サーバーの対象のメモリへの書き込み (保存。ENG-40 の仕様 6、ENG-34 と同じ扱い)。変更範囲ごとに、旧内容を読んで追加バッファに退避
/// (Undo のため) してから書く。一部の範囲が失敗しても他の範囲は書く。切断したら残りは「切断」で失敗にする。
/// 結果の型はプロセスメモリと同じ (<see cref="ProcessWriteResult"/>。切断は <see cref="ProcessWriteReason.ProcessExited"/>)。
/// </summary>
public static class GdbWrite
{
    public static async Task<ProcessWriteResult> ExecuteAsync(Document document, GdbByteSource target, CancellationToken cancellationToken = default)
    {
        DocumentSnapshot snapshot = document.DataSnapshot;
        AddBuffer addBuffer = document.AddBuffer;
        var written = new List<SavedRange>();
        var failed = new List<ProcessWriteFailure>();
        const int chunk = 64 * 1024;
        byte[] buffer = new byte[chunk];
        bool disconnected = false;

        IEnumerable<(long Offset, long Length)> dirty = snapshot.EnumerateModifiedRanges().Concat(document.LastDeviceWriteRanges);
        foreach ((long offset, long length) in Merge(dirty))
        {
            if (disconnected)
            {
                failed.Add(new ProcessWriteFailure(offset, length, ProcessWriteReason.ProcessExited));
                continue;
            }

            try
            {
                // 旧内容を退避する。読めない範囲 (アクセス不可) は書けないものとして失敗にする。
                long firstAt = -1;
                bool readable = true;
                for (long pos = 0; pos < length && readable; pos += chunk)
                {
                    int n = (int)Math.Min(chunk, length - pos);
                    ReadResult old = await target.ReadAsync(offset + pos, buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    if (!old.IsComplete)
                    {
                        readable = false;
                        disconnected = old.Unreadable.Any(u => u.Reason == UnreadableReason.Disconnected);
                        break;
                    }

                    long at = addBuffer.Append(buffer.AsSpan(0, n));
                    firstAt = firstAt < 0 ? at : firstAt;
                }

                if (!readable)
                {
                    failed.Add(new ProcessWriteFailure(offset, length, disconnected ? ProcessWriteReason.ProcessExited : ProcessWriteReason.WriteError));
                    continue;
                }

                for (long pos = 0; pos < length; pos += chunk)
                {
                    int n = (int)Math.Min(chunk, length - pos);
                    if (!snapshot.Read(offset + pos, buffer.AsSpan(0, n)).IsComplete)
                    {
                        throw new IOException("The edited data could not be read.");
                    }

                    await target.WriteAsync(offset + pos, buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                }

                written.Add(new SavedRange(offset, length, firstAt));
            }
            catch (GdbErrorReplyException ex)
            {
                failed.Add(new ProcessWriteFailure(offset, length, ProcessWriteReason.WriteError, ex.Code));
            }
            catch (GdbConnectionException)
            {
                disconnected = true;
                failed.Add(new ProcessWriteFailure(offset, length, ProcessWriteReason.ProcessExited));
            }
        }

        return new ProcessWriteResult(written, failed);
    }

    private static List<(long Offset, long Length)> Merge(IEnumerable<(long Offset, long Length)> ranges)
    {
        var result = new List<(long Offset, long Length)>();
        foreach ((long offset, long length) in ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset))
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
}
