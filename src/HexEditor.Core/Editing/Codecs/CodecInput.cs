namespace HexEditor.Core.Editing.Codecs;

/// <summary>
/// 展開の入力 (範囲を 64 KiB ずつ読むバッファ)。展開が圧縮データの終わりで止まった位置 (<see cref="Consumed"/>) を正確に求めるため、
/// 読みすぎた最後の 8 バイトまでを <see cref="Unread"/> で戻せる (EDIT-41 の仕様 4・5)。
/// </summary>
internal sealed class CodecInput(RangeReader reader)
{
    private const int Carry = 8;
    private readonly byte[] _buffer = new byte[Carry + 64 * 1024];
    private int _start = Carry;
    private int _pos = Carry;
    private int _end = Carry;

    public RangeReader Reader { get; } = reader;

    /// <summary>範囲の先頭から、展開に使ったバイト数。</summary>
    public long Consumed => Reader.Position - (_end - _pos);

    /// <summary>範囲の終わりまで読み終えたか (バッファも空)。</summary>
    public bool AtEnd => _pos == _end && Reader.Remaining == 0;

    /// <summary>まだ使っていないバッファの内容 (空なら読み足す。範囲の終わりでは空)。</summary>
    public ReadOnlySpan<byte> Available
    {
        get
        {
            if (_pos == _end)
            {
                Fill();
            }

            return _buffer.AsSpan(_pos, _end - _pos);
        }
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _end - _pos);
        _pos += count;
    }

    /// <summary>1 バイト読む (範囲の終わりでは -1)。</summary>
    public int ReadByte()
    {
        if (_pos == _end && !Fill())
        {
            return -1;
        }

        return _buffer[_pos++];
    }

    /// <summary>ちょうど <paramref name="destination"/> の長さを読む。足りなければ <see cref="CompressedDataException"/> (途中で終わっている)。</summary>
    public void ReadExact(Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            ReadOnlySpan<byte> available = Available;
            if (available.IsEmpty)
            {
                throw new CompressedDataException(CompressedDataError.Truncated, Consumed);
            }

            int n = Math.Min(available.Length, destination.Length - done);
            available[..n].CopyTo(destination[done..]);
            Advance(n);
            done += n;
        }
    }

    /// <summary>直前に読んだ <paramref name="count"/> バイト (8 バイトまで) を戻す。</summary>
    public void Unread(int count)
    {
        if (count < 0 || count > Carry || _pos - count < _start)
        {
            throw new InvalidOperationException("戻せるのは直前に読んだ 8 バイトまでです。");
        }

        _pos -= count;
    }

    private bool Fill()
    {
        // 最後の 8 バイトを先頭に残す (Unread のため)。
        int keep = Math.Min(Carry, _end - _start);
        _buffer.AsSpan(_end - keep, keep).CopyTo(_buffer.AsSpan(Carry - keep));
        _start = Carry - keep;
        int n = Reader.Read(_buffer.AsSpan(Carry));
        _pos = Carry;
        _end = Carry + n;
        return n > 0;
    }
}

/// <summary>圧縮データの誤りの種類 (EDIT-41 の「エラー」)。</summary>
public enum CompressedDataError
{
    /// <summary>圧縮データが壊れている。</summary>
    Corrupt,

    /// <summary>圧縮データの途中で範囲が終わっている。</summary>
    Truncated,

    /// <summary>対応していない機能 (zlib のプリセット辞書など)。</summary>
    Unsupported,

    /// <summary>展開に必要なメモリが上限を超える (辞書の大きさなど)。</summary>
    MemoryLimit,
}

/// <summary>
/// 展開できなかった (EDIT-41 の「エラー」)。<see cref="InputOffset"/> は範囲の先頭からの位置、<see cref="Partial"/> はそれまでに展開できた分
/// (新しいタブで開くかを選べる。使わなければ <see cref="Engine.EditContent.Dispose"/> で消す)。
/// </summary>
public sealed class CompressedDataException(CompressedDataError error, long inputOffset, string? detail = null)
    : IOException($"圧縮データの誤り ({error}) 位置 {inputOffset}" + (detail is null ? string.Empty : ": " + detail))
{
    public CompressedDataError Error { get; } = error;

    public long InputOffset { get; } = inputOffset;

    /// <summary>ライブラリの説明 (あれば)。</summary>
    public string? Detail { get; } = detail;

    public Engine.EditContent? Partial { get; internal set; }

    /// <summary>展開できた分の長さ。</summary>
    public long PartialLength { get; internal set; }
}
