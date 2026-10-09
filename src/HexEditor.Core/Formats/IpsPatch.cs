using HexEditor.Core.Clipboard;
using HexEditor.Core.Compare;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Formats;

/// <summary>IPS で表せない位置の変更 (16 MiB 以上で形式 IPS を指定した、または 4 GiB 以上。TOOL-12 の仕様 3)。</summary>
public sealed class IpsLimitException(long offset, long limit, bool ips32)
    : InvalidOperationException($"{(ips32 ? "IPS32" : "IPS")} では 0x{limit:X} 以上の位置 (0x{offset:X}) を表せません。")
{
    public long Offset { get; } = offset;

    public long Limit { get; } = limit;

    public bool Ips32 { get; } = ips32;
}

/// <summary>壊れたパッチ (TOOL-12 の仕様 4)。何番目のレコード (ファイル内のオフセット) かを含める。</summary>
public sealed class IpsFormatException(string reason, int record, long fileOffset)
    : IOException($"IPS パッチが壊れています ({reason}。レコード {record}、ファイル内の位置 0x{fileOffset:X})。")
{
    public string Reason { get; } = reason;

    public int Record { get; } = record;

    public long FileOffset { get; } = fileOffset;
}

/// <summary>パッチのレコード 1 つ。RLE なら <see cref="Data"/> は null で、<see cref="Value"/> を <see cref="Length"/> 回。</summary>
public sealed record IpsRecord(long Offset, int Length, byte[]? Data, byte Value = 0)
{
    public bool IsRle => Data is null;
}

/// <summary>読み込んだパッチ。<see cref="TruncateTo"/> は切り詰めの長さ (なければ null)。</summary>
public sealed record IpsPatchData(bool Ips32, IReadOnlyList<IpsRecord> Records, long? TruncateTo);

/// <summary>
/// IPS / IPS32 のパッチの読み書き (TOOL-12)。書き出しは差分 (元のデータと現在の内容) から、変更された範囲ごとのレコードを作る。
/// 差分はデータエンジンの変更範囲から求め、ファイル全体を比較しない (挿入・削除で後ろがずれた場合は、ずれた範囲だけを比較する)。
/// </summary>
public static class IpsPatch
{
    /// <summary>IPS のオフセットの上限 (16 MiB 未満)。</summary>
    public const long IpsLimit = 0x1000000;

    /// <summary>IPS32 のオフセットの上限 (4 GiB 未満)。</summary>
    public const long Ips32Limit = 0x1_0000_0000;

    /// <summary>レコードの最大の長さ。</summary>
    public const int MaxRecord = 0xFFFF;

    /// <summary>RLE にする同じ値の連続の最小の長さ。</summary>
    public const int RleMinimum = 8;

    /// <summary>1 つのレコードにまとめる差分の間隔 (5 バイト以下。レコードのヘッダの節約)。</summary>
    public const int MergeGap = 5;

    private static ReadOnlySpan<byte> EofMarker => "EOF"u8;

    /// <summary>
    /// 編集中のドキュメントと保存されている内容 (元データ) の差分の範囲 (現在の内容でのオフセット)。<see cref="SavedContentDiff"/> の差分から、
    /// 上書きした範囲は値を比べて違うバイトだけに、挿入・削除で後ろがずれた範囲は、そこから末尾までを比べる。
    /// </summary>
    public static List<(long Offset, long Length)> ChangedRanges(DocumentSnapshot current, ByteReader original, long originalLength,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DiffRange> diff = SavedContentDiff.Compute(current, cancellationToken);
        var candidates = new List<(long Offset, long Length)>();
        long shiftFrom = -1;
        foreach (DiffRange d in diff)
        {
            if (d.Kind == DiffKind.Changed && shiftFrom < 0)
            {
                candidates.Add((d.RightOffset, d.RightLength));
                continue;
            }

            shiftFrom = shiftFrom < 0 ? d.RightOffset : shiftFrom;
        }

        long end = Math.Max(current.Length, originalLength);
        if (shiftFrom >= 0)
        {
            candidates.Add((shiftFrom, end - shiftFrom));
        }

        ByteReader read = (o, dst) => ReadPadded(current, o, dst);
        return Refine(candidates, read, current.Length, original, originalLength, cancellationToken);
    }

    /// <summary>2 つのバイト列の差分の範囲 (テスト・2 つのファイルの比較)。</summary>
    public static List<(long Offset, long Length)> ChangedRanges(ByteReader original, long originalLength, ByteReader current, long currentLength,
        CancellationToken cancellationToken = default) =>
        Refine([(0, Math.Max(originalLength, currentLength))], current, currentLength, original, originalLength, cancellationToken);

    private static void ReadPadded(DocumentSnapshot snapshot, long offset, Span<byte> destination)
    {
        destination.Clear();
        if (offset < snapshot.Length)
        {
            snapshot.Read(offset, destination[..(int)Math.Min(destination.Length, snapshot.Length - offset)]);
        }
    }

    /// <summary>候補の範囲で、元のデータと値が違うバイトの範囲を求める。現在の長さを超える部分 (短くなった) は差分にしない。</summary>
    private static List<(long Offset, long Length)> Refine(List<(long Offset, long Length)> candidates, ByteReader current, long currentLength,
        ByteReader original, long originalLength, CancellationToken cancellationToken)
    {
        var result = new List<(long Offset, long Length)>();
        byte[] a = new byte[1 << 20], b = new byte[1 << 20];
        foreach ((long offset, long length) in candidates)
        {
            long end = Math.Min(offset + length, currentLength);
            for (long pos = offset; pos < end; pos += a.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(a.Length, end - pos);
                current(pos, a.AsSpan(0, n));
                int m = (int)Math.Clamp(originalLength - pos, 0, n);
                if (m > 0)
                {
                    original(pos, b.AsSpan(0, m));
                }

                for (int i = 0; i < n; i++)
                {
                    if (i < m && a[i] == b[i])
                    {
                        continue;
                    }

                    long at = pos + i;
                    if (result.Count > 0 && result[^1].Offset + result[^1].Length == at)
                    {
                        result[^1] = (result[^1].Offset, result[^1].Length + 1);
                    }
                    else
                    {
                        result.Add((at, 1));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// パッチを書く (TOOL-12 の仕様 3)。<paramref name="changes"/> は差分の範囲 (現在の内容でのオフセット、昇順)、<paramref name="current"/> は
    /// 現在の内容。現在の内容が短くなっていれば切り詰めの長さを付ける。
    /// </summary>
    /// <returns>書いた形式が IPS32 か。</returns>
    public static bool Write(Stream output, IReadOnlyList<(long Offset, long Length)> changes, ByteReader current, long currentLength,
        long originalLength, IpsFormat format, bool useRle = true)
    {
        // 差分の間隔が 5 バイト以下なら 1 つのレコードにまとめる。
        var merged = new List<(long Offset, long Length)>();
        foreach ((long o, long l) in changes)
        {
            if (merged.Count > 0 && o - (merged[^1].Offset + merged[^1].Length) <= MergeGap)
            {
                merged[^1] = (merged[^1].Offset, o + l - merged[^1].Offset);
            }
            else
            {
                merged.Add((o, l));
            }
        }

        long lastOffset = merged.Count > 0 ? merged[^1].Offset + merged[^1].Length - 1 : 0;
        long? truncate = currentLength < originalLength ? currentLength : null;
        bool needs32 = merged.Any(r => r.Offset + r.Length - 1 >= IpsLimit) || truncate >= IpsLimit;
        bool ips32 = format switch
        {
            IpsFormat.Ips32 => true,
            IpsFormat.Ips => false,
            _ => needs32,
        };

        if (merged.Count > 0 && lastOffset >= Ips32Limit || truncate >= Ips32Limit)
        {
            throw new IpsLimitException(Math.Max(lastOffset, truncate ?? 0), Ips32Limit, true);
        }

        if (!ips32 && needs32)
        {
            throw new IpsLimitException(merged.Count > 0 && lastOffset >= IpsLimit ? merged.First(r => r.Offset + r.Length - 1 >= IpsLimit).Offset : truncate!.Value,
                IpsLimit, false);
        }

        int offsetBytes = ips32 ? 4 : 3;
        output.Write(ips32 ? "IPS32"u8 : "PATCH"u8);
        Span<byte> header = stackalloc byte[9];

        // 範囲を 1 MiB ずつ読んでレコードにし、すぐに書く (大きな差分でもメモリに全体を持たない)。
        const int Chunk = 1 << 20;
        foreach ((long offset, long length) in merged)
        {
            for (long pos = 0; pos < length; pos += Chunk)
            {
                byte[] data = new byte[(int)Math.Min(Chunk, length - pos)];
                current(offset + pos, data);
                var records = new List<IpsRecord>();
                Split(records, offset + pos, data, useRle);
                WriteRecords(output, ips32 ? records : AvoidEofOffset(records, current), offsetBytes, header);
            }
        }

        output.Write(ips32 ? "EEOF"u8 : "EOF"u8);
        if (truncate is { } t)
        {
            WriteBigEndian(header, t, offsetBytes);
            output.Write(header[..offsetBytes]);
        }

        return ips32;
    }

    private static void WriteRecords(Stream output, List<IpsRecord> records, int offsetBytes, Span<byte> header)
    {
        foreach (IpsRecord r in records)
        {
            WriteBigEndian(header, r.Offset, offsetBytes);
            if (r.IsRle)
            {
                header[offsetBytes] = 0;
                header[offsetBytes + 1] = 0;
                header[offsetBytes + 2] = (byte)(r.Length >> 8);
                header[offsetBytes + 3] = (byte)r.Length;
                header[offsetBytes + 4] = r.Value;
                output.Write(header[..(offsetBytes + 5)]);
            }
            else
            {
                header[offsetBytes] = (byte)(r.Length >> 8);
                header[offsetBytes + 1] = (byte)r.Length;
                output.Write(header[..(offsetBytes + 2)]);
                output.Write(r.Data);
            }
        }
    }

    /// <summary>データをレコードに分ける: 同じ値が 8 バイト以上続く部分は RLE、1 レコードは 65,535 バイトまで。</summary>
    private static void Split(List<IpsRecord> records, long offset, byte[] data, bool useRle)
    {
        int i = 0;
        int literalStart = 0;
        void Literal(int from, int to)
        {
            for (int p = from; p < to; p += MaxRecord)
            {
                int n = Math.Min(MaxRecord, to - p);
                records.Add(new IpsRecord(offset + p, n, data.AsSpan(p, n).ToArray()));
            }
        }

        while (i < data.Length)
        {
            int run = 1;
            while (useRle && i + run < data.Length && data[i + run] == data[i])
            {
                run++;
            }

            if (useRle && run >= RleMinimum)
            {
                Literal(literalStart, i);
                for (int p = 0; p < run; p += MaxRecord)
                {
                    records.Add(new IpsRecord(offset + i + p, Math.Min(MaxRecord, run - p), null, data[i]));
                }

                i += run;
                literalStart = i;
            }
            else
            {
                i += run;
            }
        }

        Literal(literalStart, data.Length);
    }

    /// <summary>
    /// IPS で、オフセットがちょうど 0x454F46 (<c>EOF</c>) のレコードは終端と区別できないため、1 バイト手前から始める (手前の 1 バイトは
    /// 現在の値 = 元の値)。RLE のレコードは、先頭の 1 バイトを手前の 1 バイトと合わせて通常のレコードにする。
    /// </summary>
    private static List<IpsRecord> AvoidEofOffset(List<IpsRecord> records, ByteReader current)
    {
        const long Eof = 0x454F46;
        var result = new List<IpsRecord>(records.Count + 1);
        Span<byte> before = stackalloc byte[1];
        foreach (IpsRecord r in records)
        {
            if (r.Offset != Eof)
            {
                result.Add(r);
                continue;
            }

            current(Eof - 1, before);
            if (!r.IsRle && r.Length < MaxRecord)
            {
                result.Add(new IpsRecord(Eof - 1, r.Length + 1, [before[0], .. r.Data!]));
            }
            else if (!r.IsRle)
            {
                result.Add(new IpsRecord(Eof - 1, 2, [before[0], r.Data![0]]));
                result.Add(new IpsRecord(Eof + 1, r.Length - 1, r.Data[1..]));
            }
            else
            {
                result.Add(new IpsRecord(Eof - 1, 2, [before[0], r.Value]));
                if (r.Length > 1)
                {
                    result.Add(new IpsRecord(Eof + 1, r.Length - 1, r.Length - 1 >= RleMinimum ? null : Enumerable.Repeat(r.Value, r.Length - 1).ToArray(), r.Value));
                }
            }
        }

        return result;
    }

    private static void WriteBigEndian(Span<byte> destination, long value, int bytes)
    {
        for (int i = 0; i < bytes; i++)
        {
            destination[i] = (byte)(value >> ((bytes - 1 - i) * 8));
        }
    }

    // ---- 読み込み (TOOL-12 の仕様 4) ----

    /// <summary>パッチを読む。ヘッダの不一致・レコードの途中での終わり・終端がないことは <see cref="IpsFormatException"/>。</summary>
    public static IpsPatchData Read(Stream patch)
    {
        using var ms = new MemoryStream();
        patch.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        bool ips32;
        int at;
        if (bytes.AsSpan().StartsWith("PATCH"u8))
        {
            ips32 = false;
            at = 5;
        }
        else if (bytes.AsSpan().StartsWith("IPS32"u8))
        {
            ips32 = true;
            at = 5;
        }
        else
        {
            throw new IpsFormatException("header", 0, 0);
        }

        int offsetBytes = ips32 ? 4 : 3;
        ReadOnlySpan<byte> end = ips32 ? "EEOF"u8 : "EOF"u8;
        var records = new List<IpsRecord>();
        int index = 0;
        while (true)
        {
            if (at + end.Length <= bytes.Length && bytes.AsSpan(at, end.Length).SequenceEqual(end))
            {
                // 終端。続く 3 (IPS32 は 4) バイトは切り詰めの長さ。
                int rest = bytes.Length - (at + end.Length);
                long? truncate = null;
                if (rest >= offsetBytes)
                {
                    truncate = BigEndian(bytes.AsSpan(at + end.Length, offsetBytes));
                }

                return new IpsPatchData(ips32, records, truncate);
            }

            if (at + offsetBytes + 2 > bytes.Length)
            {
                throw new IpsFormatException(at >= bytes.Length ? "eof" : "truncated", index, at);
            }

            long offset = BigEndian(bytes.AsSpan(at, offsetBytes));
            int length = bytes[at + offsetBytes] << 8 | bytes[at + offsetBytes + 1];
            int body = at + offsetBytes + 2;
            if (length == 0)
            {
                if (body + 3 > bytes.Length)
                {
                    throw new IpsFormatException("truncated", index, at);
                }

                int count = bytes[body] << 8 | bytes[body + 1];
                records.Add(new IpsRecord(offset, count, null, bytes[body + 2]));
                at = body + 3;
            }
            else
            {
                if (body + length > bytes.Length)
                {
                    throw new IpsFormatException("truncated", index, at);
                }

                records.Add(new IpsRecord(offset, length, bytes.AsSpan(body, length).ToArray()));
                at = body + length;
            }

            index++;
        }
    }

    private static long BigEndian(ReadOnlySpan<byte> bytes)
    {
        long v = 0;
        foreach (byte b in bytes)
        {
            v = v << 8 | b;
        }

        return v;
    }

    /// <summary>パッチを当てた後の長さ (レコードが対象の長さを超えれば延長、切り詰めの長さがあれば切り詰める)。</summary>
    public static long ResultLength(IpsPatchData patch, long targetLength)
    {
        long length = targetLength;
        foreach (IpsRecord r in patch.Records)
        {
            length = Math.Max(length, r.Offset + r.Length);
        }

        return patch.TruncateTo is { } t ? t : length;
    }

    /// <summary>バイト列にパッチを当てる (テスト・小さなデータ用)。</summary>
    public static byte[] Apply(byte[] target, IpsPatchData patch)
    {
        long length = Math.Max(target.Length, patch.Records.Count == 0 ? 0 : patch.Records.Max(r => r.Offset + r.Length));
        byte[] result = new byte[length];
        target.CopyTo(result, 0);
        foreach (IpsRecord r in patch.Records)
        {
            if (r.IsRle)
            {
                result.AsSpan((int)r.Offset, r.Length).Fill(r.Value);
            }
            else
            {
                r.Data!.CopyTo(result, r.Offset);
            }
        }

        return patch.TruncateTo is { } t ? result[..(int)Math.Min(t, result.Length)] : result;
    }

    /// <summary>
    /// ドキュメントにパッチを当てる (1 回の Undo で戻せる)。長さ固定のドキュメントでは、長さが変わるパッチは当てない
    /// (<see cref="FixedLengthException"/>。EDIT-43 の仕様 6)。
    /// </summary>
    public static void ApplyTo(Document document, IpsPatchData patch, string description = "パッチの適用")
    {
        long resultLength = ResultLength(patch, document.Length);
        if (resultLength != document.Length && !document.CanResize)
        {
            throw new FixedLengthException();
        }

        using (document.BeginGroup(description))
        {
            foreach (IpsRecord r in patch.Records)
            {
                if (r.Offset > document.Length)
                {
                    // 対象を延長する (延長した部分は 00)。
                    document.InsertPattern(document.Length, r.Offset - document.Length, [0], description);
                }

                if (r.IsRle)
                {
                    if (r.Offset + r.Length > document.Length)
                    {
                        long inside = document.Length - r.Offset;
                        if (inside > 0)
                        {
                            document.OverwritePattern(r.Offset, inside, [r.Value], description);
                        }

                        document.InsertPattern(document.Length, r.Length - Math.Max(0, inside), [r.Value], description);
                    }
                    else
                    {
                        document.OverwritePattern(r.Offset, r.Length, [r.Value], description);
                    }
                }
                else
                {
                    document.Overwrite(r.Offset, r.Data, description);
                }
            }

            if (patch.TruncateTo is { } t && t < document.Length)
            {
                document.Delete(t, document.Length - t, description);
            }
        }
    }
}
