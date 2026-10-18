using System.Buffers.Binary;
using K4os.Hash.xxHash;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>
/// LZ4 の展開 (フレーム形式とブロック形式)。フレームの終わり (EndMark とチェックサム) で止まる位置を正確に求めるため、入力を順に読む展開を持つ
/// (EDIT-41 の仕様 4・5)。圧縮は K4os.Compression.LZ4 を使う。窓は 64 KiB、メモリは一定 (ブロックの大きさ (最大 4 MiB) を除く)。
/// </summary>
internal static class Lz4Decoder
{
    public const uint FrameMagic = 0x184D2204;
    private const int WindowSize = 64 * 1024;

    /// <summary>ブロック形式: 範囲の終わりまでを 1 つのブロックとして展開する (ブロック形式は終わりの印を持たない)。</summary>
    public static long DecodeBlockFormat(CodecInput input, CodecOutput output, CancellationToken token)
    {
        var window = new WindowWriter(output, WindowSize);
        try
        {
            DecodeSequences(new StreamingBlock(input), window, token, () => input.Consumed);
        }
        finally
        {
            window.Flush();
        }

        return window.Total;
    }

    /// <summary>フレーム形式を 1 つ展開する。内容のチェックサムが一致しなければ <paramref name="checksumMismatch"/> を true にする。</summary>
    public static long DecodeFrame(CodecInput input, CodecOutput output, CancellationToken token, out bool checksumMismatch)
    {
        checksumMismatch = false;
        Span<byte> head = stackalloc byte[7];
        input.ReadExact(head[..4]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != FrameMagic)
        {
            throw new CompressedDataException(CompressedDataError.Corrupt, 0, "LZ4 フレームのマジックナンバーがありません");
        }

        // FLG・BD と、あれば内容の大きさ・辞書 ID、ヘッダのチェックサム。
        byte[] descriptor = new byte[14];
        input.ReadExact(descriptor.AsSpan(0, 2));
        byte flg = descriptor[0], bd = descriptor[1];
        if ((flg >> 6) != 1 || (flg & 0x02) != 0 || (bd & 0x8F) != 0)
        {
            throw new CompressedDataException(CompressedDataError.Corrupt, 4, "LZ4 フレームのヘッダが正しくありません");
        }

        bool blockChecksum = (flg & 0x10) != 0, contentSize = (flg & 0x08) != 0, contentChecksum = (flg & 0x04) != 0, dictId = (flg & 0x01) != 0;
        int descLength = 2;
        if (contentSize)
        {
            input.ReadExact(descriptor.AsSpan(descLength, 8));
            descLength += 8;
        }

        if (dictId)
        {
            input.ReadExact(descriptor.AsSpan(descLength, 4));
            descLength += 4;
        }

        int hc = input.ReadByte();
        if (hc < 0)
        {
            throw new CompressedDataException(CompressedDataError.Truncated, input.Consumed);
        }

        if (((XXH32.DigestOf(descriptor.AsSpan(0, descLength)) >> 8) & 0xFF) != hc)
        {
            throw new CompressedDataException(CompressedDataError.Corrupt, input.Consumed - 1, "LZ4 フレームのヘッダのチェックサムが一致しません");
        }

        if (dictId)
        {
            throw new CompressedDataException(CompressedDataError.Unsupported, 4, "辞書を使う LZ4 フレームには対応していません");
        }

        int maxBlock = ((bd >> 4) & 7) switch
        {
            4 => 64 * 1024,
            5 => 256 * 1024,
            6 => 1024 * 1024,
            7 => 4 * 1024 * 1024,
            _ => throw new CompressedDataException(CompressedDataError.Corrupt, 5, "LZ4 フレームのブロックの大きさが正しくありません"),
        };

        var hash = contentChecksum ? new XXH32() : null;
        var blockWindow = new WindowWriter(output, WindowSize);
        if (hash is not null)
        {
            blockWindow.Observer = data => hash.Update(data);
        }

        byte[] block = new byte[maxBlock];
        Span<byte> word = stackalloc byte[4];
        try
        {
            DecodeBlocks(input, blockWindow, block, maxBlock, blockChecksum, token);
        }
        catch (CompressedDataException)
        {
            blockWindow.Flush();
            throw;
        }

        blockWindow.Flush();
        if (contentChecksum)
        {
            input.ReadExact(word);
            checksumMismatch = hash!.Digest() != BinaryPrimitives.ReadUInt32LittleEndian(word);
        }

        if (contentSize && BinaryPrimitives.ReadInt64LittleEndian(descriptor.AsSpan(2)) != blockWindow.Total)
        {
            checksumMismatch = true;
        }

        return blockWindow.Total;
    }

    private static void DecodeBlocks(CodecInput input, WindowWriter blockWindow, byte[] block, int maxBlock, bool blockChecksum, CancellationToken token)
    {
        Span<byte> word = stackalloc byte[4];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long at = input.Consumed;
            input.ReadExact(word);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (size == 0)
            {
                break;
            }

            bool raw = (size & 0x80000000) != 0;
            size &= 0x7FFFFFFF;
            if (size > maxBlock)
            {
                throw new CompressedDataException(CompressedDataError.Corrupt, at, "LZ4 のブロックが大きすぎます");
            }

            input.ReadExact(block.AsSpan(0, (int)size));
            if (blockChecksum)
            {
                input.ReadExact(word);
                if (XXH32.DigestOf(block.AsSpan(0, (int)size)) != BinaryPrimitives.ReadUInt32LittleEndian(word))
                {
                    throw new CompressedDataException(CompressedDataError.Corrupt, at, "LZ4 のブロックのチェックサムが一致しません");
                }
            }

            long before = blockWindow.Total;
            if (raw)
            {
                blockWindow.Write(block.AsSpan(0, (int)size));
            }
            else
            {
                DecodeSequences(new MemoryBlock(block, (int)size), blockWindow, token, () => at);
            }

            if (blockWindow.Total - before > maxBlock)
            {
                throw new CompressedDataException(CompressedDataError.Corrupt, at, "LZ4 のブロックの展開後の大きさが上限を超えています");
            }
        }
    }

    /// <summary>LZ4 のシーケンス (トークン、リテラル、オフセット、一致の長さ) を、入力の終わりまで展開する。</summary>
    private static void DecodeSequences(IBlockInput input, WindowWriter window, CancellationToken token, Func<long> position)
    {
        int steps = 0;
        while (true)
        {
            int tokenByte = input.ReadByte();
            if (tokenByte < 0)
            {
                // 空のブロック (ブロック形式で範囲が空) は 0 バイト。
                return;
            }

            int literal = tokenByte >> 4;
            if (literal == 15)
            {
                literal += ReadLength(input, position);
            }

            input.CopyTo(window, literal, position);
            if (input.AtEnd)
            {
                // 最後のシーケンスはリテラルだけ。
                return;
            }

            int lo = input.ReadByte(), hi = input.ReadByte();
            if (hi < 0)
            {
                throw new CompressedDataException(CompressedDataError.Truncated, position());
            }

            int offset = lo | (hi << 8);
            int match = tokenByte & 15;
            if (match == 15)
            {
                match += ReadLength(input, position);
            }

            if (!window.Copy(offset, match + 4))
            {
                throw new CompressedDataException(CompressedDataError.Corrupt, position(), "LZ4 の一致の距離が正しくありません");
            }

            if ((++steps & 0xFFFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
        }
    }

    private static int ReadLength(IBlockInput input, Func<long> position)
    {
        int total = 0;
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0)
            {
                throw new CompressedDataException(CompressedDataError.Truncated, position());
            }

            total += b;
            if (total > int.MaxValue / 2)
            {
                throw new CompressedDataException(CompressedDataError.Corrupt, position(), "LZ4 の長さが大きすぎます");
            }

            if (b != 255)
            {
                return total;
            }
        }
    }

    private interface IBlockInput
    {
        bool AtEnd { get; }

        int ReadByte();

        void CopyTo(WindowWriter window, int count, Func<long> position);
    }

    /// <summary>範囲から順に読むブロック (ブロック形式)。</summary>
    private sealed class StreamingBlock(CodecInput input) : IBlockInput
    {
        public bool AtEnd => input.AtEnd;

        public int ReadByte() => input.ReadByte();

        public void CopyTo(WindowWriter window, int count, Func<long> position)
        {
            while (count > 0)
            {
                ReadOnlySpan<byte> available = input.Available;
                if (available.IsEmpty)
                {
                    throw new CompressedDataException(CompressedDataError.Truncated, position());
                }

                int n = Math.Min(count, available.Length);
                window.Write(available[..n]);
                input.Advance(n);
                count -= n;
            }
        }
    }

    /// <summary>読み込んだフレームのブロック。</summary>
    private sealed class MemoryBlock(byte[] data, int length) : IBlockInput
    {
        private int _pos;

        public bool AtEnd => _pos >= length;

        public int ReadByte() => _pos < length ? data[_pos++] : -1;

        public void CopyTo(WindowWriter window, int count, Func<long> position)
        {
            if (count > length - _pos)
            {
                throw new CompressedDataException(CompressedDataError.Corrupt, position(), "LZ4 のリテラルがブロックを超えています");
            }

            window.Write(data.AsSpan(_pos, count));
            _pos += count;
        }
    }
}
