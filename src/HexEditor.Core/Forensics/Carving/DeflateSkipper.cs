namespace HexEditor.Core.Forensics.Carving;

/// <summary>
/// DEFLATE のストリームの終わりを探す (gzip のカービング用)。データは展開せず、ブロックの記号を読み飛ばして最後のブロックの終端だけを求める
/// (zlib の puff.c と同じ手順)。入力は信頼できないデータなので、読むバイト数は呼び出し側が渡した <see cref="ByteWindow"/> の上限で
/// 必ず止まる。表の構築は不正な長さの組を拒否する。
/// </summary>
internal static class DeflateSkipper
{
    private const int MaxBits = 15;
    private const int MaxLiteralCodes = 286;
    private const int MaxDistanceCodes = 30;

    private sealed class Huffman
    {
        public readonly short[] Count = new short[MaxBits + 1];
        public readonly short[] Symbol = new short[288];
    }

    private static readonly short[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly short[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly Huffman FixedLiterals = BuildFixedLiterals();
    private static readonly Huffman FixedDistances = BuildFixedDistances();

    private sealed class Bits(ByteWindow window, long position)
    {
        private int _buffer;
        private int _count;

        public long Position { get; private set; } = position;

        public bool Failed { get; private set; }

        public int Read(int need)
        {
            while (_count < need)
            {
                if (!window.TryU8(Position, out byte b))
                {
                    Failed = true;
                    return 0;
                }

                Position++;
                _buffer |= b << _count;
                _count += 8;
            }

            int value = _buffer & ((1 << need) - 1);
            _buffer >>= need;
            _count -= need;
            return value;
        }

        /// <summary>バイトの端まで読み捨てる。</summary>
        public void AlignToByte()
        {
            _buffer = 0;
            _count = 0;
        }

        public bool Skip(long bytes)
        {
            if (bytes < 0 || Position + bytes > window.Limit)
            {
                Failed = true;
                return false;
            }

            Position += bytes;
            return true;
        }
    }

    private static int Construct(Huffman h, ReadOnlySpan<short> lengths)
    {
        Array.Clear(h.Count);
        foreach (short len in lengths)
        {
            h.Count[len]++;
        }

        if (h.Count[0] == lengths.Length)
        {
            return 0;
        }

        int left = 1;
        for (int len = 1; len <= MaxBits; len++)
        {
            left <<= 1;
            left -= h.Count[len];
            if (left < 0)
            {
                return left;
            }
        }

        short[] offs = new short[MaxBits + 1];
        for (int len = 1; len < MaxBits; len++)
        {
            offs[len + 1] = (short)(offs[len] + h.Count[len]);
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                h.Symbol[offs[lengths[symbol]]++] = (short)symbol;
            }
        }

        return left;
    }

    private static int Decode(Bits bits, Huffman h)
    {
        int code = 0, first = 0, index = 0;
        for (int len = 1; len <= MaxBits; len++)
        {
            code |= bits.Read(1);
            if (bits.Failed)
            {
                return -1;
            }

            int count = h.Count[len];
            if (code - count < first)
            {
                return h.Symbol[index + (code - first)];
            }

            index += count;
            first += count;
            first <<= 1;
            code <<= 1;
        }

        return -1;
    }

    private static Huffman BuildFixedLiterals()
    {
        short[] lengths = new short[288];
        for (int i = 0; i < 144; i++)
        {
            lengths[i] = 8;
        }

        for (int i = 144; i < 256; i++)
        {
            lengths[i] = 9;
        }

        for (int i = 256; i < 280; i++)
        {
            lengths[i] = 7;
        }

        for (int i = 280; i < 288; i++)
        {
            lengths[i] = 8;
        }

        var h = new Huffman();
        Construct(h, lengths);
        return h;
    }

    private static Huffman BuildFixedDistances()
    {
        short[] lengths = new short[MaxDistanceCodes];
        Array.Fill(lengths, (short)5);
        var h = new Huffman();
        Construct(h, lengths);
        return h;
    }

    /// <summary>[<paramref name="start"/>, …) の DEFLATE のストリームを読み飛ばし、終わりの位置 (バイトの境界) を返す。壊れていれば null。</summary>
    public static long? FindEnd(ByteWindow window, long start)
    {
        var bits = new Bits(window, start);
        int last;
        do
        {
            last = bits.Read(1);
            int type = bits.Read(2);
            if (bits.Failed)
            {
                return null;
            }

            switch (type)
            {
                case 0:
                    {
                        bits.AlignToByte();
                        int len = bits.Read(16), notLen = bits.Read(16);
                        if (bits.Failed || len != (~notLen & 0xFFFF) || !bits.Skip(len))
                        {
                            return null;
                        }

                        break;
                    }

                case 1:
                    if (!Codes(bits, FixedLiterals, FixedDistances))
                    {
                        return null;
                    }

                    break;
                case 2:
                    {
                        int nlen = bits.Read(5) + 257, ndist = bits.Read(5) + 1, ncode = bits.Read(4) + 4;
                        if (bits.Failed || nlen > MaxLiteralCodes || ndist > MaxDistanceCodes)
                        {
                            return null;
                        }

                        short[] lengths = new short[320];
                        for (int i = 0; i < ncode; i++)
                        {
                            lengths[CodeLengthOrder[i]] = (short)bits.Read(3);
                        }

                        var lencode = new Huffman();
                        if (bits.Failed || Construct(lencode, lengths.AsSpan(0, 19)) != 0)
                        {
                            return null;
                        }

                        Array.Clear(lengths);
                        int index = 0;
                        while (index < nlen + ndist)
                        {
                            int symbol = Decode(bits, lencode);
                            if (symbol < 0)
                            {
                                return null;
                            }

                            if (symbol < 16)
                            {
                                lengths[index++] = (short)symbol;
                                continue;
                            }

                            short previous = 0;
                            int repeat;
                            if (symbol == 16)
                            {
                                if (index == 0)
                                {
                                    return null;
                                }

                                previous = lengths[index - 1];
                                repeat = 3 + bits.Read(2);
                            }
                            else if (symbol == 17)
                            {
                                repeat = 3 + bits.Read(3);
                            }
                            else
                            {
                                repeat = 11 + bits.Read(7);
                            }

                            if (bits.Failed || index + repeat > nlen + ndist)
                            {
                                return null;
                            }

                            while (repeat-- > 0)
                            {
                                lengths[index++] = previous;
                            }
                        }

                        if (lengths[256] == 0)
                        {
                            return null;
                        }

                        var literals = new Huffman();
                        int err = Construct(literals, lengths.AsSpan(0, nlen));
                        if (err < 0 || (err > 0 && nlen - literals.Count[0] != 1))
                        {
                            return null;
                        }

                        var distances = new Huffman();
                        err = Construct(distances, lengths.AsSpan(nlen, ndist));
                        if (err < 0 || (err > 0 && ndist - distances.Count[0] != 1))
                        {
                            return null;
                        }

                        if (!Codes(bits, literals, distances))
                        {
                            return null;
                        }

                        break;
                    }

                default:
                    return null;
            }
        }
        while (last == 0);

        return bits.Position;
    }

    private static bool Codes(Bits bits, Huffman literals, Huffman distances)
    {
        while (true)
        {
            int symbol = Decode(bits, literals);
            if (symbol < 0)
            {
                return false;
            }

            if (symbol < 256)
            {
                continue;
            }

            if (symbol == 256)
            {
                return true;
            }

            symbol -= 257;
            if (symbol >= 29)
            {
                return false;
            }

            bits.Read(LengthExtra[symbol]);
            int dist = Decode(bits, distances);
            if (dist < 0 || dist >= MaxDistanceCodes)
            {
                return false;
            }

            bits.Read(DistExtra[dist]);
            if (bits.Failed)
            {
                return false;
            }
        }
    }
}
