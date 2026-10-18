namespace HexEditor.Core.Editing.Codecs;

/// <summary>流れていく内容を見る (チェックサムの計算)。</summary>
internal delegate void SpanObserver(ReadOnlySpan<byte> data);

/// <summary>
/// 展開した内容を、過去の <c>history</c> バイトを参照できる窓に書き、まとめて <see cref="CodecOutput"/> へ流す (deflate・LZ4 の展開)。
/// メモリは窓の大きさ + 1 MiB で一定。
/// </summary>
internal sealed class WindowWriter
{
    private const int FlushSize = 1024 * 1024;
    private readonly CodecOutput _output;
    private readonly int _history;
    private readonly byte[] _buffer;
    private int _pos;
    private int _flushed;

    public WindowWriter(CodecOutput output, int history)
    {
        _output = output;
        _history = history;
        _buffer = new byte[history + FlushSize];
    }

    /// <summary>流す内容を見る (チェックサムの計算)。</summary>
    public SpanObserver? Observer { get; set; }

    /// <summary>これまでに書いたバイト数 (展開後の長さ)。</summary>
    public long Total { get; private set; }

    public void WriteByte(byte value)
    {
        if (_pos == _buffer.Length)
        {
            Slide();
        }

        _buffer[_pos++] = value;
        Total++;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_pos == _buffer.Length)
            {
                Slide();
            }

            int n = Math.Min(data.Length, _buffer.Length - _pos);
            data[..n].CopyTo(_buffer.AsSpan(_pos));
            _pos += n;
            Total += n;
            data = data[n..];
        }
    }

    /// <summary>
    /// <paramref name="distance"/> バイト前から <paramref name="length"/> バイトを写す (重なってよい)。距離が書いた量・窓を超えれば false。
    /// </summary>
    public bool Copy(int distance, int length)
    {
        if (distance <= 0 || distance > _history || distance > Total)
        {
            return false;
        }

        while (length > 0)
        {
            if (_pos == _buffer.Length)
            {
                Slide();
            }

            int n = Math.Min(length, _buffer.Length - _pos);
            int from = _pos - distance;
            if (distance >= n)
            {
                _buffer.AsSpan(from, n).CopyTo(_buffer.AsSpan(_pos));
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    _buffer[_pos + i] = _buffer[from + i];
                }
            }

            _pos += n;
            Total += n;
            length -= n;
        }

        return true;
    }

    /// <summary>まだ流していない分を流す。</summary>
    public void Flush()
    {
        if (_pos > _flushed)
        {
            ReadOnlySpan<byte> data = _buffer.AsSpan(_flushed, _pos - _flushed);
            Observer?.Invoke(data);
            _output.Write(data);
            _flushed = _pos;
        }
    }

    private void Slide()
    {
        Flush();
        int keep = Math.Min(_history, _pos);
        _buffer.AsSpan(_pos - keep, keep).CopyTo(_buffer);
        _pos = keep;
        _flushed = keep;
    }
}
