using HexEditor.Core.Selection;

namespace HexEditor.Core.Clipboard;

/// <summary>マルチ選択の「形式を選択してコピー」(EDIT-25 の仕様 7)。</summary>
public enum CopyRangesMode
{
    /// <summary>要素をオフセット順に連結する (既定)。</summary>
    Concatenate,

    /// <summary>
    /// 要素ごとに分ける: 配列形式は <c>data_0</c>、<c>data_1</c> … の別の変数、テキスト形式は空行で区切る、JSON は配列にする。
    /// </summary>
    Separate,

    /// <summary>矩形選択: 行ごとに改行する (要素ごとに分けて、改行 1 つで区切る)。</summary>
    Rows,
}

public static partial class CopyFormatter
{
    /// <summary>
    /// 要素を連結したデータを読む関数 (連結したデータの位置 0〜合計の長さ)。<paramref name="read"/> はドキュメントの読み込み。
    /// </summary>
    public static ByteReader Concatenated(IReadOnlyList<ByteRange> ranges, ByteReader read)
    {
        long[] starts = new long[ranges.Count];
        long at = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            starts[i] = at;
            at += ranges[i].Length;
        }

        return (offset, destination) =>
        {
            int i = Array.BinarySearch(starts, offset);
            if (i < 0)
            {
                i = ~i - 1;
            }

            int done = 0;
            while (done < destination.Length && i < ranges.Count)
            {
                long within = offset + done - starts[i];
                int n = (int)Math.Min(destination.Length - done, ranges[i].Length - within);
                if (n > 0)
                {
                    read(ranges[i].Start + within, destination.Slice(done, n));
                    done += n;
                }

                i++;
            }
        };
    }

    /// <summary>
    /// マルチ選択・矩形選択の要素を出力する。連結では、要素を連結した 1 つのデータとして書く (位置の形式の開始は最初の要素の開始)。
    /// 分ける・行ごとでは、要素ごとに書いて区切る (配列は変数名に番号を付け、JSON は配列にする)。
    /// </summary>
    public static IReadOnlyList<CopyNote> WriteRanges(CopyFormat format, CopyOptions options, ByteReader read, IReadOnlyList<ByteRange> ranges,
        CopyRangesMode mode, TextWriter writer, CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        if (ranges.Count == 0)
        {
            return [];
        }

        if (mode == CopyRangesMode.Concatenate)
        {
            long total = ranges.Sum(r => r.Length);
            ByteReader concatenated = Concatenated(ranges, read);
            long first = ranges[0].Start;
            return Write(format, options, (o, d) => concatenated(o - first, d), first, total, writer, cancellationToken, progress);
        }

        var notes = new List<CopyNote>();
        bool json = format == CopyFormat.Json;
        bool array = IsArray(format);
        string separator = mode == CopyRangesMode.Rows || array ? options.NewLine : options.NewLine + options.NewLine;
        if (json)
        {
            writer.Write("[");
            separator = "," + options.NewLine;
        }

        long done = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0)
            {
                writer.Write(separator);
            }

            CopyOptions element = array ? options with { VariableName = options.VariableName + "_" + i } : options;
            ByteRange r = ranges[i];
            foreach (CopyNote note in Write(format, element, read, r.Start, r.Length, writer, cancellationToken))
            {
                if (!notes.Contains(note))
                {
                    notes.Add(note);
                }
            }

            done += r.Length;
            progress?.Invoke(done);
        }

        if (json)
        {
            writer.Write("]");
        }

        return notes;
    }
}
