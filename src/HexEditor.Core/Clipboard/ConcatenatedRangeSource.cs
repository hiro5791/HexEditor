using HexEditor.Core.Engine;
using HexEditor.Core.Selection;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Clipboard;

/// <summary>
/// 範囲の参照の中の要素を連結して 1 つのデータとして見せる (マルチ選択・矩形をアプリ内クリップボードにコピーしたときの内容。EDIT-24)。
/// </summary>
public sealed class ConcatenatedRangeSource : ByteSourceBase
{
    private readonly SnapshotRange _range;
    private readonly IReadOnlyList<ByteRange> _parts;

    // 各要素の連結した内容の中での開始位置 (二分探索に使う)。
    private readonly long[] _starts;

    public ConcatenatedRangeSource(SnapshotRange range, IReadOnlyList<ByteRange> parts)
    {
        _range = range;
        _parts = parts;
        _starts = new long[parts.Count];
        long at = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            _starts[i] = at;
            at += parts[i].Length;
        }

        Length = at;
    }

    public override string DisplayName => _range.DisplayName;

    public override string Identity => _range.Identity + ":parts";

    public override long Length { get; }

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length || buffer.IsEmpty)
        {
            return new ReadResult(0);
        }

        int i = Array.BinarySearch(_starts, offset);
        if (i < 0)
        {
            i = ~i - 1;
        }

        int done = 0;
        List<UnreadableRange>? unreadable = null;
        while (done < buffer.Length && i < _parts.Count)
        {
            long within = offset + done - _starts[i];
            ByteRange part = _parts[i];
            int n = (int)Math.Min(buffer.Length - done, part.Length - within);
            if (n > 0)
            {
                long sourceOffset = part.Start + within;
                ReadResult r = _range.Read(sourceOffset, buffer.Slice(done, n));

                // 読めない範囲は連結した内容の位置に直して返す。
                foreach (UnreadableRange u in r.Unreadable)
                {
                    (unreadable ??= []).Add(u with { Offset = u.Offset - sourceOffset + offset + done });
                }

                done += r.BytesReturned;
                if (r.BytesReturned < n)
                {
                    break;
                }
            }

            i++;
        }

        return new ReadResult(done, unreadable);
    }
}
