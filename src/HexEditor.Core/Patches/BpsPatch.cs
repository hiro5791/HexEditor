using HexEditor.Core.Engine;

namespace HexEditor.Core.Patches;

/// <summary>BPS の命令 (beat の形式)。</summary>
public enum BpsAction
{
    /// <summary>変換前の同じ位置からコピー。</summary>
    SourceRead,

    /// <summary>パッチのデータ。</summary>
    TargetRead,

    /// <summary>変換前の任意の位置からコピー (相対位置)。</summary>
    SourceCopy,

    /// <summary>変換後のすでに書いた部分からコピー (相対位置。重なってよい)。</summary>
    TargetCopy,
}

/// <summary>
/// BPS パッチ (EDIT-42 の仕様 2)。ヘッダ <c>BPS1</c>、変換前・変換後・メタデータの長さ、命令の並び、末尾に変換前・変換後・パッチの CRC-32。
/// 元データからのコピー命令で挿入・削除を効率よく表す。適用はピースの操作で、変換前からのコピーはデータを読まない。
/// </summary>
public sealed class BpsPatch : LoadedPatch
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly long _actionsStart;
    private readonly long _actionsEnd;

    private BpsPatch(Stream stream, bool ownsStream, long sourceSize, long targetSize, long actionsStart, long actionsEnd,
        int actions, long changed, uint sourceCrc, uint targetCrc)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        SourceSize = sourceSize;
        TargetSize = targetSize;
        _actionsStart = actionsStart;
        _actionsEnd = actionsEnd;
        RecordCount = actions;
        ChangedBytes = changed;
        SourceCrc = sourceCrc;
        TargetCrc = targetCrc;
    }

    public long SourceSize { get; }

    public long TargetSize { get; }

    public uint SourceCrc { get; }

    public uint TargetCrc { get; }

    public override PatchFormat Format => PatchFormat.Bps;

    public override int RecordCount { get; }

    public override long ChangedBytes { get; }

    public override bool VerificationReadsWholeFile => true;

    public override long ResultLength(long inputLength, bool reverse = false) => TargetSize;

    public override PatchVerification Verify(DocumentSnapshot input, bool reverse = false, Action<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        input.Length == SourceSize && Crc32(input, progress, cancellationToken) == SourceCrc ? PatchVerification.Match : PatchVerification.Mismatch;

    protected override bool VerifyResult(DocumentSnapshot result, bool reverse, Action<long>? progress, CancellationToken cancellationToken) =>
        result.Length == TargetSize && Crc32(result, progress, cancellationToken) == TargetCrc;

    public override void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }

        base.Dispose();
    }

    /// <summary>命令を先頭から読む (種類、長さ、相対位置、TargetRead のデータのストリーム上の位置)。</summary>
    private IEnumerable<(BpsAction Action, long Length, long Relative, long DataAt)> Actions()
    {
        var body = new BufferedStream(new SubStream(_stream, 0, _actionsEnd), 1 << 16);
        body.Position = _actionsStart;
        int index = 0;
        while (body.Position < _actionsEnd)
        {
            BpsAction action;
            long length, relative = 0, dataAt = 0;
            try
            {
                ulong data = BeatVarint.Read(body);
                action = (BpsAction)(data & 3);
                length = checked((long)(data >> 2) + 1);
                if (action == BpsAction.TargetRead)
                {
                    dataAt = body.Position;
                    if (length > _actionsEnd - dataAt)
                    {
                        throw new EndOfStreamException();
                    }

                    body.Position = dataAt + length;
                }
                else if (action is BpsAction.SourceCopy or BpsAction.TargetCopy)
                {
                    relative = BeatVarint.ReadSigned(body);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException)
            {
                throw new PatchException(PatchError.Corrupt, $"BPS: レコード {index + 1} が壊れています。", index);
            }

            yield return (action, length, relative, dataAt);
            index++;
        }
    }

    protected override void ApplyCore(Document document, bool reverse, string description, CancellationToken cancellationToken)
    {
        PieceTree source = document.DataSnapshot.Tree;
        if (source.Length != SourceSize)
        {
            throw new PatchException(PatchError.OutOfRange, "BPS: 変換前の長さが違います。");
        }

        PieceTree output = PieceTree.Empty;
        long sourceRelative = 0, targetRelative = 0;
        int index = 0;
        byte[] buffer = new byte[1 << 20];
        foreach ((BpsAction action, long length, long relative, long dataAt) in Actions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long outPos = output.Length;
            if (length > TargetSize - outPos)
            {
                throw new PatchException(PatchError.Corrupt, $"BPS: レコード {index + 1} が範囲外です。", index);
            }

            switch (action)
            {
                case BpsAction.SourceRead:
                    if (outPos > SourceSize || length > SourceSize - outPos)
                    {
                        throw new PatchException(PatchError.Corrupt, $"BPS: レコード {index + 1} が範囲外です。", index);
                    }

                    output = output.Concat(source.Slice(outPos, length));
                    break;
                case BpsAction.TargetRead:
                    for (long done = 0; done < length;)
                    {
                        int n = (int)Math.Min(buffer.Length, length - done);
                        _stream.Position = dataAt + done;
                        _stream.ReadExactly(buffer, 0, n);
                        output = output.Concat(document.BufferBytes(buffer.AsSpan(0, n)));
                        done += n;
                    }

                    break;
                case BpsAction.SourceCopy:
                    sourceRelative += relative;
                    if (sourceRelative < 0 || sourceRelative > SourceSize || length > SourceSize - sourceRelative)
                    {
                        throw new PatchException(PatchError.Corrupt, $"BPS: レコード {index + 1} が範囲外です。", index);
                    }

                    output = output.Concat(source.Slice(sourceRelative, length));
                    sourceRelative += length;
                    break;
                default:
                    targetRelative += relative;
                    if (targetRelative < 0 || targetRelative >= outPos)
                    {
                        throw new PatchException(PatchError.Corrupt, $"BPS: レコード {index + 1} が範囲外です。", index);
                    }

                    output = TargetCopy(document, output, targetRelative, length);
                    targetRelative += length;
                    break;
            }

            index++;
        }

        if (output.Length != TargetSize)
        {
            throw new PatchException(PatchError.Corrupt, "BPS: 変換後の長さが合いません。");
        }

        document.ReplaceAll(output, description);
    }

    /// <summary>
    /// 変換後のすでに書いた部分からのコピー。重なる場合 (距離 d &lt; 長さ) は、直前の d バイトの繰り返しになる: d が 4,096 バイト以下ならパターン、
    /// それより長ければ書いた部分を少しずつ伸ばしながらコピーする。
    /// </summary>
    private static PieceTree TargetCopy(Document document, PieceTree output, long from, long length)
    {
        long distance = output.Length - from;
        if (length <= distance)
        {
            return output.Concat(output.Slice(from, length));
        }

        if (distance <= Engine.GeneratedData.MaxPatternLength)
        {
            byte[] pattern = new byte[distance];
            new DocumentSnapshot(document.DataSnapshot.Storage, output.Slice(from, distance)).Read(0, pattern);
            return output.Concat(document.PatternTree(pattern, length));
        }

        for (long remaining = length; remaining > 0;)
        {
            long take = Math.Min(remaining, output.Length - from);
            output = output.Concat(output.Slice(from, take));
            from += take;
            remaining -= take;
        }

        return output;
    }

    internal static BpsPatch Load(Stream stream, bool ownsStream)
    {
        try
        {
            if (stream.Length < 4 + 3 + 12)
            {
                throw new PatchException(PatchError.Corrupt, "BPS: 短すぎます。");
            }

            stream.Position = 0;
            uint actual = PatchCrc32.Compute(new SubStream(stream, 0, stream.Length - 4));
            byte[] footer = new byte[12];
            stream.Position = stream.Length - 12;
            stream.ReadExactly(footer);
            if (actual != BitConverter.ToUInt32(footer, 8))
            {
                throw new PatchException(PatchError.Corrupt, "BPS: パッチの CRC-32 が合いません。");
            }

            var head = new BufferedStream(new SubStream(stream, 0, stream.Length - 12), 1 << 16);
            head.Position = 4;
            long sourceSize = checked((long)BeatVarint.Read(head));
            long targetSize = checked((long)BeatVarint.Read(head));
            long metadata = checked((long)BeatVarint.Read(head));

            // 引き算で比べる (巨大なメタデータの長さで位置があふれないように)。
            if (metadata > stream.Length - 12 - head.Position)
            {
                throw new PatchException(PatchError.Corrupt, "BPS: メタデータが壊れています。");
            }

            long actionsStart = head.Position + metadata;
            if (actionsStart > stream.Length - 12)
            {
                throw new PatchException(PatchError.Corrupt, "BPS: メタデータが壊れています。");
            }

            var patch = new BpsPatch(stream, ownsStream, sourceSize, targetSize, actionsStart, stream.Length - 12, 0, 0,
                BitConverter.ToUInt32(footer, 0), BitConverter.ToUInt32(footer, 4));

            // 命令を一度読み、数と変更するバイト数 (変換前の同じ位置からのコピー以外) を数える。壊れていればここで分かる。
            int count = 0;
            long changed = 0, total = 0;
            foreach ((BpsAction action, long length, _, _) in patch.Actions())
            {
                count++;
                total = checked(total + length);
                if (action != BpsAction.SourceRead)
                {
                    changed += length;
                }
            }

            if (total != targetSize)
            {
                throw new PatchException(PatchError.Corrupt, "BPS: 変換後の長さが合いません。");
            }

            return new BpsPatch(stream, ownsStream, sourceSize, targetSize, actionsStart, stream.Length - 12, count, changed,
                patch.SourceCrc, patch.TargetCrc);
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException)
        {
            if (ownsStream)
            {
                stream.Dispose();
            }

            throw new PatchException(PatchError.Corrupt, "BPS: ヘッダが壊れています。");
        }
        catch (PatchException)
        {
            if (ownsStream)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    // ---- 書き出し ----

    /// <summary>推定サイズ (命令の数とパッチの大きさ。データを読まずに求まる)。</summary>
    internal static (long Records, long Size) Estimate(PatchScript script)
    {
        long size = 4 + BeatVarint.Size((ulong)script.SourceLength) + BeatVarint.Size((ulong)script.TargetLength) + 1 + 12;
        long sourceRelative = 0;
        foreach (PatchSegment s in script.Segments)
        {
            ulong head = (ulong)(s.Length - 1) << 2;
            size += BeatVarint.Size(head | 3);
            if (!s.FromSource)
            {
                size += s.Length;
            }
            else if (s.SourceOffset != s.TargetOffset)
            {
                long rel = s.SourceOffset - sourceRelative;
                size += BeatVarint.Size((ulong)Math.Abs(rel) << 1 | 1);
                sourceRelative = s.SourceOffset + s.Length;
            }
        }

        return (script.Segments.Count, size);
    }

    /// <summary>BPS を書く。変換前・変換後の CRC-32 のため全体を読む。</summary>
    public static void Write(Stream output, PatchScript script, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var crc = new CrcWriteStream(output);
        crc.Write("BPS1"u8);
        BeatVarint.Write(crc, (ulong)script.SourceLength);
        BeatVarint.Write(crc, (ulong)script.TargetLength);
        BeatVarint.Write(crc, 0);
        long sourceRelative = 0;
        byte[] buffer = new byte[1 << 20];
        foreach (PatchSegment s in script.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong head = (ulong)(s.Length - 1) << 2;
            if (!s.FromSource)
            {
                BeatVarint.Write(crc, head | (ulong)BpsAction.TargetRead);
                for (long done = 0; done < s.Length; done += buffer.Length)
                {
                    int n = (int)Math.Min(buffer.Length, s.Length - done);
                    script.ReadTarget(s.TargetOffset + done, buffer.AsSpan(0, n));
                    crc.Write(buffer.AsSpan(0, n));
                }
            }
            else if (s.SourceOffset == s.TargetOffset)
            {
                BeatVarint.Write(crc, head | (ulong)BpsAction.SourceRead);
            }
            else
            {
                BeatVarint.Write(crc, head | (ulong)BpsAction.SourceCopy);
                BeatVarint.WriteSigned(crc, s.SourceOffset - sourceRelative);
                sourceRelative = s.SourceOffset + s.Length;
            }
        }

        Span<byte> footer = stackalloc byte[4];
        BitConverter.TryWriteBytes(footer, UpsPatch.Crc(script.ReadSource, script.SourceLength, progress, 0, cancellationToken));
        crc.Write(footer);
        BitConverter.TryWriteBytes(footer, UpsPatch.Crc(script.ReadTarget, script.TargetLength, progress, script.SourceLength, cancellationToken));
        crc.Write(footer);
        BitConverter.TryWriteBytes(footer, crc.Crc);
        output.Write(footer);
    }
}
