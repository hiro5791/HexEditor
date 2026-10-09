namespace HexEditor.Core.Hashing.Crypto;

/// <summary>
/// ed2k (eDonkey2000 / eMule)。9,728,000 バイトの部品ごとの MD4 を求め、部品が 1 つならその値、2 つ以上なら部品の値を
/// 連結した列の MD4。データ長がちょうど部品の倍数のとき、旧方式 (red) は末尾に空の部品の MD4 を加え、新方式 (blue) は加えない。
/// 部品の値は 16 バイトずつ MD4 に流し込むため、データの大きさに比例するものは持たない。
/// </summary>
public sealed class Ed2kHasher(Ed2kMode mode) : IHasher
{
    /// <summary>部品の大きさ (9,500 KiB)。</summary>
    public const int ChunkSize = 9_728_000;

    private Md4Hasher _chunk = new();
    private readonly Md4Hasher _list = new();
    private long _chunkBytes;
    private long _chunks;
    private byte[]? _firstChunkHash;

    public void Append(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_chunkBytes == ChunkSize)
            {
                CloseChunk();
            }

            int n = (int)Math.Min(data.Length, ChunkSize - _chunkBytes);
            _chunk.Append(data[..n]);
            _chunkBytes += n;
            data = data[n..];
        }
    }

    private void CloseChunk()
    {
        byte[] hash = _chunk.Finish();
        if (_chunks == 0)
        {
            _firstChunkHash = hash;
        }

        _list.Append(hash);
        _chunks++;
        _chunk = new Md4Hasher();
        _chunkBytes = 0;
    }

    public byte[] Finish()
    {
        if (_chunkBytes == ChunkSize && mode == Ed2kMode.Blue)
        {
            // 新方式: 最後の部品で終わり、空の部品を加えない。
            CloseChunk();
        }
        else
        {
            // 端数の部品 (旧方式で倍数のときは、満杯の部品のあとに空の部品)。
            if (_chunkBytes == ChunkSize)
            {
                CloseChunk();
            }

            CloseChunk();
        }

        return _chunks == 1 ? _firstChunkHash! : _list.Finish();
    }
}
