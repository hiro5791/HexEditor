using HexEditor.Core.Engine;

namespace HexEditor.Core.Patches;

/// <summary>
/// UPS パッチ (EDIT-42 の仕様 2)。ヘッダ <c>UPS1</c>、変換前・変換後の長さ (可変長整数)、レコード (前のレコードからの距離と、0 で終わる XOR の差分)、
/// 末尾に変換前・変換後・パッチの CRC-32。XOR なので同じパッチで逆向きにも適用できる (EDIT-43 の仕様 7)。
/// </summary>
public sealed class UpsPatch : LoadedPatch
{
    private readonly List<(long Offset, byte[] Xor)> _records;

    private UpsPatch(long sourceSize, long targetSize, List<(long, byte[])> records, uint sourceCrc, uint targetCrc)
    {
        SourceSize = sourceSize;
        TargetSize = targetSize;
        _records = records;
        SourceCrc = sourceCrc;
        TargetCrc = targetCrc;
    }

    public long SourceSize { get; }

    public long TargetSize { get; }

    public uint SourceCrc { get; }

    public uint TargetCrc { get; }

    public IReadOnlyList<(long Offset, byte[] Xor)> Records => _records;

    public override PatchFormat Format => PatchFormat.Ups;

    public override int RecordCount => _records.Count;

    public override long ChangedBytes => _records.Sum(r => (long)r.Xor.Length);

    public override bool CanReverse => true;

    public override bool VerificationReadsWholeFile => true;

    public override long ResultLength(long inputLength, bool reverse = false) => reverse ? SourceSize : TargetSize;

    public override PatchVerification Verify(DocumentSnapshot input, bool reverse = false, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        (long size, uint crc) = reverse ? (TargetSize, TargetCrc) : (SourceSize, SourceCrc);
        return input.Length == size && Crc32(input, progress, cancellationToken) == crc ? PatchVerification.Match : PatchVerification.Mismatch;
    }

    protected override bool VerifyResult(DocumentSnapshot result, bool reverse, Action<long>? progress, CancellationToken cancellationToken)
    {
        (long size, uint crc) = reverse ? (SourceSize, SourceCrc) : (TargetSize, TargetCrc);
        return result.Length == size && Crc32(result, progress, cancellationToken) == crc;
    }

    protected override void ApplyCore(Document document, bool reverse, string description, CancellationToken cancellationToken)
    {
        long outLength = reverse ? SourceSize : TargetSize;
        long length = document.Length;
        if (outLength > length)
        {
            // 長くなる部分は 00 として XOR する。
            document.InsertPattern(length, outLength - length, [0], description);
        }

        long limit = document.Length;
        foreach ((long offset, byte[] xor) in _records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Clamp(limit - offset, 0, xor.Length);
            if (n == 0)
            {
                continue;
            }

            byte[] data = new byte[n];
            PatchDiff.ReadPadded(document.DataSnapshot, offset, data);
            for (int i = 0; i < n; i++)
            {
                data[i] ^= xor[i];
            }

            document.Overwrite(offset, data, description);
        }

        if (outLength < document.Length)
        {
            document.Delete(outLength, document.Length - outLength, description);
        }
    }

    /// <summary>バイト列に当てる (テスト用)。</summary>
    public byte[] Apply(byte[] input, bool reverse = false)
    {
        long outLength = reverse ? SourceSize : TargetSize;
        byte[] result = new byte[Math.Max(outLength, input.Length)];
        input.CopyTo(result, 0);
        foreach ((long offset, byte[] xor) in _records)
        {
            for (int i = 0; i < xor.Length && offset + i < result.Length; i++)
            {
                result[offset + i] ^= xor[i];
            }
        }

        return result[..(int)outLength];
    }

    internal static UpsPatch Load(Stream stream, bool ownsStream)
    {
        try
        {
            if (stream.Length < 4 + 2 + 12)
            {
                throw new PatchException(PatchError.Corrupt, "UPS: 短すぎます。");
            }

            // パッチ自身の CRC-32 (最後の 4 バイトを除く全体)。
            stream.Position = 0;
            uint actual = PatchCrc32.Compute(new SubStream(stream, 0, stream.Length - 4));
            byte[] footer = new byte[12];
            stream.Position = stream.Length - 12;
            stream.ReadExactly(footer);
            uint sourceCrc = BitConverter.ToUInt32(footer, 0), targetCrc = BitConverter.ToUInt32(footer, 4), patchCrc = BitConverter.ToUInt32(footer, 8);
            if (actual != patchCrc)
            {
                throw new PatchException(PatchError.Corrupt, "UPS: パッチの CRC-32 が合いません。");
            }

            var body = new BufferedStream(new SubStream(stream, 0, stream.Length - 12), 1 << 16);
            body.Position = 4;
            long sourceSize = checked((long)BeatVarint.Read(body));
            long targetSize = checked((long)BeatVarint.Read(body));
            var records = new List<(long, byte[])>();
            long pos = 0;
            var xor = new List<byte>();
            while (body.Position < body.Length)
            {
                int index = records.Count;
                try
                {
                    pos = checked(pos + (long)BeatVarint.Read(body));
                    xor.Clear();
                    while (true)
                    {
                        int b = body.ReadByte();
                        if (b < 0)
                        {
                            throw new EndOfStreamException();
                        }

                        if (b == 0)
                        {
                            break;
                        }

                        xor.Add((byte)b);
                    }
                }
                catch (Exception ex) when (ex is EndOfStreamException or OverflowException)
                {
                    throw new PatchException(PatchError.Corrupt, $"UPS: レコード {index + 1} が壊れています。", index);
                }

                if (pos > Math.Max(sourceSize, targetSize) - xor.Count)
                {
                    throw new PatchException(PatchError.Corrupt, $"UPS: レコード {index + 1} が範囲外です。", index);
                }

                records.Add((pos, xor.ToArray()));
                pos += xor.Count + 1;
            }

            return new UpsPatch(sourceSize, targetSize, records, sourceCrc, targetCrc);
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException)
        {
            throw new PatchException(PatchError.Corrupt, "UPS: ヘッダが壊れています。");
        }
        finally
        {
            if (ownsStream)
            {
                stream.Dispose();
            }
        }
    }

    // ---- 書き出し ----

    /// <summary>UPS のレコード: 位置で比べた差分の範囲 (変換前と変換後の長い方まで。短くなった部分は変換前のバイトとの XOR)。</summary>
    internal static List<(long Offset, long Length)> Runs(PatchScript script, CancellationToken cancellationToken)
    {
        List<(long Offset, long Length)> runs = script.PositionalChanges(cancellationToken);
        if (script.SourceLength > script.TargetLength)
        {
            // 変換後より後ろ: 変換前の 0 でないバイト (変換後は 0 とみなす)。
            byte[] buffer = new byte[1 << 20];
            for (long pos = script.TargetLength; pos < script.SourceLength; pos += buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, script.SourceLength - pos);
                script.ReadSource(pos, buffer.AsSpan(0, n));
                for (int i = 0; i < n; i++)
                {
                    if (buffer[i] == 0)
                    {
                        continue;
                    }

                    long at = pos + i;
                    if (runs.Count > 0 && runs[^1].Offset + runs[^1].Length == at)
                    {
                        runs[^1] = (runs[^1].Offset, runs[^1].Length + 1);
                    }
                    else
                    {
                        runs.Add((at, 1));
                    }
                }
            }
        }

        return runs;
    }

    /// <summary>推定サイズ (レコード数とパッチの大きさ)。</summary>
    internal static (long Records, long Size) Estimate(PatchScript script, List<(long Offset, long Length)> runs)
    {
        long size = 4 + BeatVarint.Size((ulong)script.SourceLength) + BeatVarint.Size((ulong)script.TargetLength) + 12;
        long pos = 0;
        foreach ((long o, long l) in runs)
        {
            size += BeatVarint.Size((ulong)(o - pos)) + l + 1;
            pos = o + l + 1;
        }

        return (runs.Count, size);
    }

    /// <summary>UPS を書く。変換前・変換後の CRC-32 のため全体を読む (<paramref name="progress"/> に読んだバイト数を知らせる)。</summary>
    public static void Write(Stream output, PatchScript script, List<(long Offset, long Length)>? runs = null, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        runs ??= Runs(script, cancellationToken);
        var crc = new CrcWriteStream(output);
        crc.Write("UPS1"u8);
        BeatVarint.Write(crc, (ulong)script.SourceLength);
        BeatVarint.Write(crc, (ulong)script.TargetLength);
        long pos = 0;
        bool inRecord = false;
        byte[] a = new byte[1 << 16], b = new byte[1 << 16];
        var pending = new MemoryStream();
        foreach ((long o, long l) in runs)
        {
            for (long done = 0; done < l; done += a.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(a.Length, l - done);
                ReadOrZero(script.ReadSource, script.SourceLength, o + done, a.AsSpan(0, n));
                ReadOrZero(script.ReadTarget, script.TargetLength, o + done, b.AsSpan(0, n));
                pending.SetLength(0);
                for (int i = 0; i < n; i++)
                {
                    byte x = (byte)(a[i] ^ b[i]);
                    long at = o + done + i;
                    if (x == 0)
                    {
                        // 差分の範囲の中の XOR 0 (長くなった部分の 00 など) はレコードの終わり。
                        if (inRecord)
                        {
                            pending.WriteByte(0);
                            inRecord = false;
                            pos = at + 1;
                        }

                        continue;
                    }

                    if (!inRecord)
                    {
                        BeatVarint.Write(pending, (ulong)(at - pos));
                        inRecord = true;
                    }

                    pending.WriteByte(x);
                }

                crc.Write(pending.GetBuffer().AsSpan(0, (int)pending.Length));
            }

            if (inRecord)
            {
                crc.WriteByte(0);
                inRecord = false;
                pos = o + l + 1;
            }
        }

        Span<byte> footer = stackalloc byte[4];
        BitConverter.TryWriteBytes(footer, Crc(script.ReadSource, script.SourceLength, progress, 0, cancellationToken));
        crc.Write(footer);
        BitConverter.TryWriteBytes(footer, Crc(script.ReadTarget, script.TargetLength, progress, script.SourceLength, cancellationToken));
        crc.Write(footer);
        BitConverter.TryWriteBytes(footer, crc.Crc);
        output.Write(footer);
    }

    private static void ReadOrZero(Clipboard.ByteReader read, long length, long offset, Span<byte> destination)
    {
        destination.Clear();
        int n = (int)Math.Clamp(length - offset, 0, destination.Length);
        if (n > 0)
        {
            read(offset, destination[..n]);
        }
    }

    /// <summary>全体の CRC-32。</summary>
    internal static uint Crc(Clipboard.ByteReader read, long length, Action<long>? progress, long progressBase, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1 << 20];
        uint state = PatchCrc32.Initial;
        for (long pos = 0; pos < length; pos += buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, length - pos);
            read(pos, buffer.AsSpan(0, n));
            state = PatchCrc32.Append(state, buffer.AsSpan(0, n));
            progress?.Invoke(progressBase + pos + n);
        }

        return PatchCrc32.Finish(state);
    }
}
