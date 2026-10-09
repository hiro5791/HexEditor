using System.Buffers.Binary;
using System.Numerics;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>
/// BLAKE3 (公式の参照実装と同じ構成。1 スレッド)。1,024 バイトのチャンクを葉とする二分木で、木は高さ分のスタック
/// (最大 54 段) だけで持つ。出力長は可変 (XOF)。鍵付きモードは 32 バイトの鍵を使う。
/// </summary>
public sealed class Blake3Hasher : IHasher
{
    private const int BlockLen = 64;
    private const int ChunkLen = 1024;
    private const uint ChunkStart = 1, ChunkEnd = 2, Parent = 4, Root = 8, KeyedHash = 16;

    private static ReadOnlySpan<uint> IV =>
        [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];

    private static ReadOnlySpan<byte> Permutation => [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];

    private readonly uint[] _key = new uint[8];
    private readonly uint _flags;
    private readonly int _outputBytes;

    // チャンクの状態。
    private readonly uint[] _chunkCv = new uint[8];
    private readonly byte[] _block = new byte[BlockLen];
    private ulong _chunkCounter;
    private int _blockLen;
    private int _blocksCompressed;

    // 部分木の値のスタック。
    private readonly uint[] _cvStack = new uint[54 * 8];
    private int _cvStackLen;

    /// <param name="outputBytes">出力のバイト数。</param>
    /// <param name="key">鍵 (空なら鍵なし。鍵付きモードは 32 バイト)。</param>
    public Blake3Hasher(int outputBytes = 32, ReadOnlySpan<byte> key = default)
    {
        if (outputBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(outputBytes));
        }

        _outputBytes = outputBytes;
        if (key.Length == 32)
        {
            for (int i = 0; i < 8; i++)
            {
                _key[i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(4 * i)..]);
            }

            _flags = KeyedHash;
        }
        else if (key.Length == 0)
        {
            IV.CopyTo(_key);
        }
        else
        {
            throw new ArgumentException("BLAKE3 の鍵は 32 バイトです。", nameof(key));
        }

        _key.CopyTo(_chunkCv, 0);
    }

    private int ChunkLength => BlockLen * _blocksCompressed + _blockLen;

    private uint StartFlag => _blocksCompressed == 0 ? ChunkStart : 0;

    public void Append(ReadOnlySpan<byte> data)
    {
        Span<uint> words = stackalloc uint[16];
        Span<uint> output = stackalloc uint[16];
        Span<uint> cv = stackalloc uint[8];
        while (data.Length > 0)
        {
            if (ChunkLength == ChunkLen)
            {
                ChunkOutputCv(cv);
                AddChunkCv(cv, _chunkCounter + 1);
                _key.CopyTo(_chunkCv, 0);
                _chunkCounter++;
                _blockLen = 0;
                _blocksCompressed = 0;
            }

            // チャンクの中: 揃ったブロックは次のデータが来てから処理する (チャンクの最後のブロックには印を付けるため)。
            if (_blockLen == BlockLen)
            {
                Words(_block, words);
                Compress(_chunkCv, words, _chunkCounter, BlockLen, _flags | StartFlag, output);
                output[..8].CopyTo(_chunkCv);
                _blocksCompressed++;
                _blockLen = 0;
            }

            int take = Math.Min(BlockLen - _blockLen, Math.Min(data.Length, ChunkLen - ChunkLength));
            data[..take].CopyTo(_block.AsSpan(_blockLen));
            _blockLen += take;
            data = data[take..];
        }
    }

    public byte[] Finish()
    {
        // 根の出力の材料 (入力の連鎖値、ブロック、カウンタ、長さ、印)。
        Span<uint> inputCv = stackalloc uint[8];
        Span<uint> block = stackalloc uint[16];
        _chunkCv.CopyTo(inputCv);
        _block.AsSpan(_blockLen).Clear();
        Words(_block, block);
        ulong counter = _chunkCounter;
        uint blockLen = (uint)_blockLen;
        uint flags = _flags | StartFlag | ChunkEnd;

        Span<uint> output = stackalloc uint[16];
        for (int remaining = _cvStackLen - 1; remaining >= 0; remaining--)
        {
            Compress(inputCv, block, counter, blockLen, flags, output);
            _cvStack.AsSpan(remaining * 8, 8).CopyTo(block);
            output[..8].CopyTo(block[8..]);
            _key.CopyTo(inputCv);
            counter = 0;
            blockLen = BlockLen;
            flags = _flags | Parent;
        }

        byte[] result = new byte[_outputBytes];
        Span<byte> word = stackalloc byte[4];
        for (ulong outputCounter = 0, at = 0; at < (ulong)result.Length; outputCounter++, at += 64)
        {
            Compress(inputCv, block, outputCounter, blockLen, flags | Root, output);
            for (int i = 0; i < 16 && at + (ulong)(4 * i) < (ulong)result.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, output[i]);
                int n = (int)Math.Min(4, (ulong)result.Length - at - (ulong)(4 * i));
                word[..n].CopyTo(result.AsSpan((int)at + 4 * i));
            }
        }

        return result;
    }

    /// <summary>今のチャンクの最後のブロックを終わりの印付きで処理した連鎖値。</summary>
    private void ChunkOutputCv(Span<uint> cv)
    {
        Span<uint> words = stackalloc uint[16];
        Span<uint> output = stackalloc uint[16];
        Words(_block, words);
        Compress(_chunkCv, words, _chunkCounter, (uint)_blockLen, _flags | StartFlag | ChunkEnd, output);
        output[..8].CopyTo(cv);
    }

    private void AddChunkCv(Span<uint> cv, ulong totalChunks)
    {
        Span<uint> block = stackalloc uint[16];
        Span<uint> output = stackalloc uint[16];
        while ((totalChunks & 1) == 0)
        {
            _cvStackLen--;
            _cvStack.AsSpan(_cvStackLen * 8, 8).CopyTo(block);
            cv.CopyTo(block[8..]);
            Compress(_key, block, 0, BlockLen, _flags | Parent, output);
            output[..8].CopyTo(cv);
            totalChunks >>= 1;
        }

        cv.CopyTo(_cvStack.AsSpan(_cvStackLen * 8, 8));
        _cvStackLen++;
    }

    private static void Words(ReadOnlySpan<byte> block, Span<uint> words)
    {
        for (int i = 0; i < 16; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(4 * i)..]);
        }
    }

    private static void Compress(ReadOnlySpan<uint> cv, ReadOnlySpan<uint> blockWords, ulong counter, uint blockLen, uint flags, Span<uint> state)
    {
        Span<uint> m = stackalloc uint[16];
        Span<uint> permuted = stackalloc uint[16];
        blockWords.CopyTo(m);
        cv.CopyTo(state);
        IV[..4].CopyTo(state[8..]);
        state[12] = (uint)counter;
        state[13] = (uint)(counter >> 32);
        state[14] = blockLen;
        state[15] = flags;
        ReadOnlySpan<byte> perm = Permutation;
        for (int round = 0; round < 7; round++)
        {
            G(state, 0, 4, 8, 12, m[0], m[1]);
            G(state, 1, 5, 9, 13, m[2], m[3]);
            G(state, 2, 6, 10, 14, m[4], m[5]);
            G(state, 3, 7, 11, 15, m[6], m[7]);
            G(state, 0, 5, 10, 15, m[8], m[9]);
            G(state, 1, 6, 11, 12, m[10], m[11]);
            G(state, 2, 7, 8, 13, m[12], m[13]);
            G(state, 3, 4, 9, 14, m[14], m[15]);
            if (round < 6)
            {
                for (int i = 0; i < 16; i++)
                {
                    permuted[i] = m[perm[i]];
                }

                permuted.CopyTo(m);
            }
        }

        for (int i = 0; i < 8; i++)
        {
            state[i] ^= state[i + 8];
            state[i + 8] ^= cv[i];
        }
    }

    private static void G(Span<uint> v, int a, int b, int c, int d, uint x, uint y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
    }
}
