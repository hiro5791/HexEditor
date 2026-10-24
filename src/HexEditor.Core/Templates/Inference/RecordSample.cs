using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Templates.Inference;

/// <summary>
/// フィールドの推定に使う標本のレコード (TPL-38 の仕様 3)。レコードの数が 4,096 以下ならすべて、超える場合は先頭から続く 1,024 個と、
/// 残りから均等な間隔で選んだレコード (末尾のレコードを含む) の合わせて 4,096 個。標本の合計は 16 MiB までで、レコード長が大きいときは
/// 数を減らす (最小 4 個)。読めないバイトを含むレコードは標本から除く (TPL-38 の「エラー」)。
/// </summary>
public sealed class RecordSample
{
    /// <summary>標本のレコードの最大の数。</summary>
    public const int MaxRecords = 4096;

    /// <summary>先頭から続けて使うレコードの最大の数。</summary>
    public const int MaxConsecutive = 1024;

    /// <summary>標本の合計の最大のバイト数。</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    private readonly byte[] _data;

    private RecordSample(int recordLength, long recordCount, byte[] data, long[] indices, int count, int consecutive, int unreadable, long bytesRead)
    {
        RecordLength = recordLength;
        RecordCount = recordCount;
        _data = data;
        Indices = indices;
        Count = count;
        ConsecutiveCount = consecutive;
        UnreadableRecords = unreadable;
        BytesRead = bytesRead;
    }

    public int RecordLength { get; }

    /// <summary>範囲の中のレコードの数。</summary>
    public long RecordCount { get; }

    /// <summary>使った (読めた) 標本のレコードの数 (N)。</summary>
    public int Count { get; }

    /// <summary>標本の i 番目のレコードの、範囲の中での番号 r (小さい順)。</summary>
    public long[] Indices { get; }

    /// <summary>標本の先頭の、範囲の先頭から続くレコードの数 (TPL-39 の仕様 1 の「連続した標本」)。</summary>
    public int ConsecutiveCount { get; }

    /// <summary>読めなかったため除いたレコードの数。</summary>
    public int UnreadableRecords { get; }

    /// <summary>標本のために読んだバイト数。</summary>
    public long BytesRead { get; }

    /// <summary>標本の i 番目のレコード。</summary>
    public ReadOnlySpan<byte> Record(int i) => _data.AsSpan(i * RecordLength, RecordLength);

    /// <summary>標本の i 番目のレコードの列 c のバイト。</summary>
    public byte At(int i, int column) => _data[(i * RecordLength) + column];

    /// <summary>標本に使うレコードの番号を選ぶ (読む前。試験用に公開)。</summary>
    public static long[] SelectIndices(long recordCount, int recordLength)
    {
        long bySize = Math.Max(TemplateInferenceRequest.MinRecords, MaxBytes / Math.Max(1, recordLength));
        int target = (int)Math.Min(MaxRecords, bySize);
        if (recordCount <= target)
        {
            long[] all = new long[recordCount];
            for (long i = 0; i < recordCount; i++)
            {
                all[i] = i;
            }

            return all;
        }

        // 先頭から続くレコードは、標本の 1/4 (4,096 個なら 1,024 個)。残りは均等な間隔で、末尾のレコードを含める。
        int consecutive = Math.Min(MaxConsecutive, target / 4);
        int rest = target - consecutive;
        long[] result = new long[target];
        for (int i = 0; i < consecutive; i++)
        {
            result[i] = i;
        }

        long first = consecutive;
        long last = recordCount - 1;
        for (int j = 0; j < rest; j++)
        {
            result[consecutive + j] = rest == 1 ? last : first + (long)((Int128)(last - first) * j / (rest - 1));
        }

        return result;
    }

    /// <summary>標本を読む。</summary>
    public static RecordSample Read(DocumentSnapshot snapshot, long start, int recordLength, long recordCount, CancellationToken cancellationToken)
    {
        long[] selected = SelectIndices(recordCount, recordLength);
        byte[] data = new byte[selected.Length * recordLength];
        var kept = new List<long>(selected.Length);
        int unreadable = 0;
        long bytesRead = 0;
        int consecutive = 0;
        bool stillConsecutive = true;

        // 続いている番号はまとめて読む (1 回の読み込みは 1 MiB まで)。
        int i = 0;
        while (i < selected.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int j = i + 1;
            while (j < selected.Length && selected[j] == selected[j - 1] + 1 && (long)(j - i + 1) * recordLength <= 1024 * 1024)
            {
                j++;
            }

            int count = j - i;
            byte[] buffer = new byte[count * recordLength];
            ReadResult r = snapshot.Read(start + (selected[i] * recordLength), buffer);
            bytesRead += buffer.Length;
            for (int n = 0; n < count; n++)
            {
                long recStart = start + (selected[i + n] * recordLength);
                bool bad = r.BytesReturned < (n + 1) * recordLength
                    || r.Unreadable.Any(u => u.Offset < recStart + recordLength && u.Offset + u.Length > recStart);
                if (bad)
                {
                    unreadable++;
                    if (selected[i + n] < MaxConsecutive)
                    {
                        stillConsecutive = false;
                    }

                    continue;
                }

                buffer.AsSpan(n * recordLength, recordLength).CopyTo(data.AsSpan(kept.Count * recordLength));
                if (stillConsecutive && selected[i + n] == kept.Count)
                {
                    consecutive++;
                }
                else
                {
                    stillConsecutive = false;
                }

                kept.Add(selected[i + n]);
            }

            i = j;
        }

        return new RecordSample(recordLength, recordCount, data, [.. kept], kept.Count, Math.Min(consecutive, MaxConsecutive), unreadable, bytesRead);
    }

    /// <summary>試験用: バイト列から標本を作る (範囲の先頭から続くレコード)。</summary>
    public static RecordSample FromBytes(ReadOnlySpan<byte> records, int recordLength)
    {
        int count = records.Length / recordLength;
        long[] indices = new long[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = i;
        }

        return new RecordSample(recordLength, count, records[..(count * recordLength)].ToArray(), indices, count, Math.Min(count, MaxConsecutive), 0, records.Length);
    }
}
