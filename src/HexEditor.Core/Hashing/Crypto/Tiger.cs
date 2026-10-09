using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>
/// Tiger / Tiger2 (Anderson と Biham)。192 bit。Tiger はパディングの先頭が 0x01、Tiger2 は 0x80 (MD4 と同じ)。
/// 値は 3 つの 64 bit ワードをリトルエンディアンで並べたもの (公式のテストベクタの表記)。
/// </summary>
public sealed class TigerHasher : BlockHasher
{
    /// <summary>S ボックス (4 × 256 個)。公式の生成手順 (sboxes.c) で作る。</summary>
    private static readonly ulong[] Table = GenerateTable();

    private readonly byte _marker;
    private readonly int _outputBytes;
    private ulong _a, _b, _c;

    /// <param name="tiger2">Tiger2 (パディング 0x80)。</param>
    /// <param name="outputBits">128 / 160 / 192 (先頭を切り詰めて表示する)。</param>
    public TigerHasher(bool tiger2 = false, int outputBits = 192)
        : base(64)
    {
        _marker = tiger2 ? (byte)0x80 : (byte)0x01;
        _outputBytes = outputBits / 8;
        Reset();
    }

    /// <summary>最初の状態に戻す (TTH で葉ごとに使い回す)。</summary>
    internal void Reset()
    {
        ResetBuffer();
        _a = 0x0123456789ABCDEF;
        _b = 0xFEDCBA9876543210;
        _c = 0xF096A5B4C3B2E187;
    }

    protected override void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Span<ulong> x = stackalloc ulong[8];
        for (int i = 0; i < 8; i++)
        {
            x[i] = BinaryPrimitives.ReadUInt64LittleEndian(block[(8 * i)..]);
        }

        Compress(Table, x, ref _a, ref _b, ref _c);
    }

    protected override byte[] FinishCore() => FinishTo(new byte[_outputBytes]);

    /// <summary>値を書き込む (TTH が割り当てずに使う)。</summary>
    internal byte[] FinishTo(byte[] output)
    {
        PadMd(_marker, 8, bigEndianLength: false);
        Span<byte> full = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(full, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(full[8..], _b);
        BinaryPrimitives.WriteUInt64LittleEndian(full[16..], _c);
        full[..output.Length].CopyTo(output);
        return output;
    }

    private static void Round(ulong[] t, ref ulong a, ref ulong b, ref ulong c, ulong x, ulong mul)
    {
        c ^= x;
        a -= t[(byte)c] ^ t[256 + (byte)(c >> 16)] ^ t[512 + (byte)(c >> 32)] ^ t[768 + (byte)(c >> 48)];
        b += t[768 + (byte)(c >> 8)] ^ t[512 + (byte)(c >> 24)] ^ t[256 + (byte)(c >> 40)] ^ t[(byte)(c >> 56)];
        b *= mul;
    }

    private static void Pass(ulong[] t, ref ulong a, ref ulong b, ref ulong c, Span<ulong> x, ulong mul)
    {
        Round(t, ref a, ref b, ref c, x[0], mul);
        Round(t, ref b, ref c, ref a, x[1], mul);
        Round(t, ref c, ref a, ref b, x[2], mul);
        Round(t, ref a, ref b, ref c, x[3], mul);
        Round(t, ref b, ref c, ref a, x[4], mul);
        Round(t, ref c, ref a, ref b, x[5], mul);
        Round(t, ref a, ref b, ref c, x[6], mul);
        Round(t, ref b, ref c, ref a, x[7], mul);
    }

    private static void KeySchedule(Span<ulong> x)
    {
        x[0] -= x[7] ^ 0xA5A5A5A5A5A5A5A5;
        x[1] ^= x[0];
        x[2] += x[1];
        x[3] -= x[2] ^ (~x[1] << 19);
        x[4] ^= x[3];
        x[5] += x[4];
        x[6] -= x[5] ^ (~x[4] >> 23);
        x[7] ^= x[6];
        x[0] += x[7];
        x[1] -= x[0] ^ (~x[7] << 19);
        x[2] ^= x[1];
        x[3] += x[2];
        x[4] -= x[3] ^ (~x[2] >> 23);
        x[5] ^= x[4];
        x[6] += x[5];
        x[7] -= x[6] ^ 0x0123456789ABCDEF;
    }

    /// <summary>圧縮関数 (x は書き換える)。</summary>
    private static void Compress(ulong[] t, Span<ulong> x, ref ulong state0, ref ulong state1, ref ulong state2)
    {
        ulong a = state0, b = state1, c = state2;
        Pass(t, ref a, ref b, ref c, x, 5);
        KeySchedule(x);
        Pass(t, ref c, ref a, ref b, x, 7);
        KeySchedule(x);
        Pass(t, ref b, ref c, ref a, x, 9);
        state0 = a ^ state0;
        state1 = b - state1;
        state2 = c + state2;
    }

    /// <summary>S ボックスの生成 (Tiger の公式の sboxes.c と同じ手順。生成中の表で圧縮関数を回す)。</summary>
    private static ulong[] GenerateTable()
    {
        var table = new ulong[1024];
        for (int i = 0; i < 1024; i++)
        {
            table[i] = 0x0101010101010101UL * (ulong)(i & 0xFF);
        }

        byte[] text = Encoding.ASCII.GetBytes("Tiger - A Fast New Hash Function, by Ross Anderson and Eli Biham");
        Span<ulong> message = stackalloc ulong[8];
        Span<ulong> x = stackalloc ulong[8];
        for (int i = 0; i < 8; i++)
        {
            message[i] = BinaryPrimitives.ReadUInt64LittleEndian(text.AsSpan(8 * i));
        }

        Span<ulong> state = [0x0123456789ABCDEF, 0xFEDCBA9876543210, 0xF096A5B4C3B2E187];
        int abc = 2;
        for (int pass = 0; pass < 5; pass++)
        {
            for (int i = 0; i < 256; i++)
            {
                for (int sb = 0; sb < 1024; sb += 256)
                {
                    abc++;
                    if (abc == 3)
                    {
                        abc = 0;
                        message.CopyTo(x);
                        Compress(table, x, ref state[0], ref state[1], ref state[2]);
                    }

                    for (int col = 0; col < 8; col++)
                    {
                        int other = sb + (byte)(state[abc] >> (8 * col));
                        int shift = 8 * col;
                        ulong mask = 0xFFUL << shift;
                        ulong mine = table[sb + i] & mask;
                        ulong theirs = table[other] & mask;
                        table[sb + i] = (table[sb + i] & ~mask) | theirs;
                        table[other] = (table[other] & ~mask) | mine;
                    }
                }
            }
        }

        return table;
    }
}

/// <summary>
/// TTH (Tiger Tree Hash。THEX の仕様)。1,024 バイトの葉を Tiger(0x00 || 葉)、節を Tiger(0x01 || 左 || 右) で求め、
/// 相手のいない節はそのまま上に上げる。木は高さ分のスタックだけで持つ (データの大きさに比例するものを持たない)。
/// </summary>
public sealed class TigerTreeHasher : IHasher
{
    private const int LeafSize = 1024;
    private const int HashSize = 24;

    private static readonly byte[] LeafPrefix = [0x00];
    private static readonly byte[] NodePrefix = [0x01];

    private readonly TigerHasher _tiger = new();

    /// <summary>まだ組み合わせていない部分木の値 (下から順に高さが減る。高さは 64 を超えない)。</summary>
    private readonly byte[] _stack = new byte[65 * HashSize];
    private readonly int[] _levels = new int[65];
    private readonly byte[] _scratch = new byte[HashSize];
    private int _count;
    private int _leafBytes;
    private bool _leafOpen;
    private bool _any;

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (!_leafOpen)
            {
                OpenLeaf();
            }

            int n = Math.Min(data.Length, LeafSize - _leafBytes);
            _tiger.Append(data[..n]);
            _leafBytes += n;
            data = data[n..];
            if (_leafBytes == LeafSize)
            {
                CloseLeaf();
            }
        }
    }

    private void OpenLeaf()
    {
        _tiger.Reset();
        _tiger.Append(LeafPrefix);
        _leafBytes = 0;
        _leafOpen = true;
    }

    private Span<byte> Entry(int index) => _stack.AsSpan(index * HashSize, HashSize);

    private void CloseLeaf()
    {
        _leafOpen = false;
        _any = true;
        _tiger.FinishTo(_scratch);
        int level = 0;
        while (_count > 0 && _levels[_count - 1] == level)
        {
            Node(Entry(_count - 1), _scratch, _scratch);
            _count--;
            level++;
        }

        _scratch.CopyTo(Entry(_count));
        _levels[_count] = level;
        _count++;
    }

    /// <summary>節の値 (output は right と同じでもよい)。</summary>
    private void Node(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, byte[] output)
    {
        _tiger.Reset();
        _tiger.Append(NodePrefix);
        _tiger.Append(left);
        _tiger.Append(right);
        _tiger.FinishTo(output);
    }

    public byte[] Finish()
    {
        if (_leafOpen || !_any)
        {
            // 端数の葉 (データが空なら空の葉 1 つ)。
            if (!_leafOpen)
            {
                OpenLeaf();
            }

            CloseLeaf();
        }

        byte[] root = Entry(_count - 1).ToArray();
        for (int i = _count - 2; i >= 0; i--)
        {
            Node(Entry(i), root, root);
        }

        return root;
    }
}
