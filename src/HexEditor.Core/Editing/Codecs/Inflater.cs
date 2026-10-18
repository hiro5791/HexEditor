namespace HexEditor.Core.Editing.Codecs;

/// <summary>
/// deflate (RFC 1951) の展開。<see cref="System.IO.Compression.DeflateStream"/> は入力を先読みし、圧縮データの終わりの位置を返さないため、
/// 終わりの位置を正確に求める (EDIT-41 の仕様 4・5) ために、入力を 1 ビット単位で追う展開を持つ。圧縮は .NET の zlib を使う。
/// 窓は 32 KiB、メモリは一定。
/// </summary>
internal sealed class Inflater
{
    private const int WindowSize = 32 * 1024;

    private static readonly ushort[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static readonly ushort[] DistanceBase =
        [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    private static readonly byte[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static HuffmanTable? s_fixedLiteral;
    private static HuffmanTable? s_fixedDistance;

    private readonly CodecInput _input;
    private readonly WindowWriter _window;
    private readonly CancellationToken _token;
    private ulong _bits;
    private int _count;

    public Inflater(CodecInput input, CodecOutput output, CancellationToken token = default)
    {
        _input = input;
        _window = new WindowWriter(output, WindowSize);
        _token = token;
    }

    /// <summary>展開した長さ。</summary>
    public long Total => _window.Total;

    /// <summary>展開した内容を見る (gzip の CRC-32、zlib の Adler-32)。</summary>
    public SpanObserver? Observer
    {
        get => _window.Observer;
        set => _window.Observer = value;
    }

    /// <summary>
    /// 最後のブロックまで展開する。終わったら、使わなかったビットバッファのバイトを入力に戻す (続くトレーラーを読めるように)。
    /// </summary>
    public void Run()
    {
        try
        {
            RunBlocks();
        }
        catch (CompressedDataException)
        {
            // それまでに展開できた分を流す (新しいタブで開くかを選べる。EDIT-41 の「エラー」)。
            _window.Flush();
            throw;
        }

        // 端数のビットを捨て、まるごと残ったバイトを戻す。
        int whole = _count / 8;
        _bits = 0;
        _count = 0;
        _input.Unread(whole);
    }

    private void RunBlocks()
    {
        bool last;
        int blocks = 0;
        do
        {
            if ((++blocks & 63) == 0)
            {
                _token.ThrowIfCancellationRequested();
            }

            last = Bits(1) == 1;
            switch (Bits(2))
            {
                case 0:
                    Stored();
                    break;
                case 1:
                    s_fixedLiteral ??= FixedLiteral();
                    s_fixedDistance ??= FixedDistance();
                    Codes(s_fixedLiteral, s_fixedDistance);
                    break;
                case 2:
                    Dynamic();
                    break;
                default:
                    throw Corrupt("ブロックの種類が正しくありません");
            }
        }
        while (!last);

        _window.Flush();
    }

    // ---- ビット ----

    private void Refill()
    {
        while (_count <= 56)
        {
            int b = _input.ReadByte();
            if (b < 0)
            {
                return;
            }

            _bits |= (ulong)b << _count;
            _count += 8;
        }
    }

    private int Bits(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        if (_count < n)
        {
            Refill();
            if (_count < n)
            {
                throw new CompressedDataException(CompressedDataError.Truncated, _input.Consumed);
            }
        }

        int value = (int)(_bits & ((1UL << n) - 1));
        _bits >>= n;
        _count -= n;
        return value;
    }

    private int Decode(HuffmanTable table)
    {
        if (_count < table.Bits)
        {
            Refill();
        }

        int entry = table.Entries[(int)(_bits & table.Mask)];
        int length = entry & 15;
        if (length == 0)
        {
            throw Corrupt("ハフマン符号が正しくありません");
        }

        if (length > _count)
        {
            throw new CompressedDataException(CompressedDataError.Truncated, _input.Consumed);
        }

        _bits >>= length;
        _count -= length;
        return entry >> 4;
    }

    private CompressedDataException Corrupt(string detail) =>
        new(CompressedDataError.Corrupt, Math.Max(0, _input.Consumed - _count / 8), detail);

    // ---- ブロック ----

    private void Stored()
    {
        // バイト境界にそろえ、LEN・NLEN を読む。
        Bits(_count % 8);
        int len = Bits(16);
        int nlen = Bits(16);
        if ((len ^ 0xFFFF) != nlen)
        {
            throw Corrupt("格納ブロックの長さが正しくありません");
        }

        // ビットバッファに残っているバイトから先に使う。
        while (len > 0 && _count >= 8)
        {
            _window.WriteByte((byte)Bits(8));
            len--;
        }

        while (len > 0)
        {
            ReadOnlySpan<byte> available = _input.Available;
            if (available.IsEmpty)
            {
                throw new CompressedDataException(CompressedDataError.Truncated, _input.Consumed);
            }

            int n = Math.Min(len, available.Length);
            _window.Write(available[..n]);
            _input.Advance(n);
            len -= n;
        }
    }

    private void Dynamic()
    {
        int hlit = Bits(5) + 257;
        int hdist = Bits(5) + 1;
        int hclen = Bits(4) + 4;
        if (hlit > 286 || hdist > 30)
        {
            throw Corrupt("符号の数が正しくありません");
        }

        Span<byte> codeLengths = stackalloc byte[19];
        codeLengths.Clear();
        for (int i = 0; i < hclen; i++)
        {
            codeLengths[CodeLengthOrder[i]] = (byte)Bits(3);
        }

        HuffmanTable lengthTable = HuffmanTable.Build(codeLengths) ?? throw Corrupt("符号長の符号が正しくありません");
        Span<byte> lengths = stackalloc byte[hlit + hdist];
        int index = 0;
        while (index < hlit + hdist)
        {
            int symbol = Decode(lengthTable);
            if (symbol < 16)
            {
                lengths[index++] = (byte)symbol;
                continue;
            }

            byte value = 0;
            int repeat;
            if (symbol == 16)
            {
                if (index == 0)
                {
                    throw Corrupt("繰り返す符号長がありません");
                }

                value = lengths[index - 1];
                repeat = 3 + Bits(2);
            }
            else if (symbol == 17)
            {
                repeat = 3 + Bits(3);
            }
            else
            {
                repeat = 11 + Bits(7);
            }

            if (index + repeat > hlit + hdist)
            {
                throw Corrupt("符号長の繰り返しが長すぎます");
            }

            lengths.Slice(index, repeat).Fill(value);
            index += repeat;
        }

        if (lengths[256] == 0)
        {
            throw Corrupt("ブロックの終わりの符号がありません");
        }

        HuffmanTable literal = HuffmanTable.Build(lengths[..hlit]) ?? throw Corrupt("リテラルの符号が正しくありません");
        HuffmanTable distance = HuffmanTable.Build(lengths[hlit..]) ?? throw Corrupt("距離の符号が正しくありません");
        Codes(literal, distance);
    }

    private void Codes(HuffmanTable literal, HuffmanTable distance)
    {
        int steps = 0;
        while (true)
        {
            int symbol = Decode(literal);
            if (symbol < 256)
            {
                _window.WriteByte((byte)symbol);
                continue;
            }

            if (symbol == 256)
            {
                return;
            }

            symbol -= 257;
            if (symbol >= 29)
            {
                throw Corrupt("長さの符号が正しくありません");
            }

            int length = LengthBase[symbol] + Bits(LengthExtra[symbol]);
            int d = Decode(distance);
            if (d >= 30)
            {
                throw Corrupt("距離の符号が正しくありません");
            }

            int dist = DistanceBase[d] + Bits(DistanceExtra[d]);
            if (!_window.Copy(dist, length))
            {
                throw Corrupt("距離が展開した長さを超えています");
            }

            if ((++steps & 0xFFFF) == 0)
            {
                _token.ThrowIfCancellationRequested();
            }
        }
    }

    private static HuffmanTable FixedLiteral()
    {
        byte[] lengths = new byte[288];
        lengths.AsSpan(0, 144).Fill(8);
        lengths.AsSpan(144, 112).Fill(9);
        lengths.AsSpan(256, 24).Fill(7);
        lengths.AsSpan(280, 8).Fill(8);
        return HuffmanTable.Build(lengths)!;
    }

    private static HuffmanTable FixedDistance()
    {
        byte[] lengths = new byte[30];
        lengths.AsSpan().Fill(5);
        return HuffmanTable.Build(lengths)!;
    }

    /// <summary>
    /// ハフマン符号の 1 段の引き表。添字は読み込んだビット (下位から)、値は (記号 &lt;&lt; 4) | 符号長 (0 は割り当てのない符号)。
    /// </summary>
    private sealed class HuffmanTable(int[] entries, int bits)
    {
        public int[] Entries { get; } = entries;

        public int Bits { get; } = bits;

        public ulong Mask => (1UL << Bits) - 1;

        /// <summary>符号長から作る。符号が多すぎる (過剰) なら null。</summary>
        public static HuffmanTable? Build(ReadOnlySpan<byte> lengths)
        {
            Span<int> count = stackalloc int[16];
            count.Clear();
            int max = 0;
            foreach (byte len in lengths)
            {
                count[len]++;
                max = Math.Max(max, len);
            }

            count[0] = 0;
            int left = 1;
            for (int len = 1; len <= 15; len++)
            {
                left = (left << 1) - count[len];
                if (left < 0)
                {
                    return null;
                }
            }

            int bits = Math.Max(1, max);
            int[] entries = new int[1 << bits];
            Span<int> next = stackalloc int[16];
            int code = 0;
            for (int len = 1; len <= 15; len++)
            {
                code = (code + count[len - 1]) << 1;
                next[len] = code;
            }

            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                int len = lengths[symbol];
                if (len == 0)
                {
                    continue;
                }

                int reversed = Reverse(next[len]++, len);
                for (int i = reversed; i < entries.Length; i += 1 << len)
                {
                    entries[i] = (symbol << 4) | len;
                }
            }

            return new HuffmanTable(entries, bits);
        }

        private static int Reverse(int code, int length)
        {
            int result = 0;
            for (int i = 0; i < length; i++)
            {
                result = (result << 1) | (code & 1);
                code >>= 1;
            }

            return result;
        }
    }
}
