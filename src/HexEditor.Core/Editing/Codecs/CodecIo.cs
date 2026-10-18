using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>
/// スナップショットの範囲を先頭から順に読む (暗号化・圧縮の入力。EDIT-40・EDIT-41 の「巨大ファイル」: 1 MiB 単位のストリーム処理)。
/// 読めない位置があれば <see cref="DataReadException"/>。読むたびにキャンセルを確かめ、進捗 (読んだバイト数) を報告する。
/// </summary>
public sealed class RangeReader
{
    /// <summary>1 回に読む大きさ。</summary>
    public const int ChunkSize = 1024 * 1024;

    private readonly DocumentSnapshot _snapshot;
    private readonly LongRunningOperation? _operation;
    private readonly long _progressBase;

    public RangeReader(DocumentSnapshot snapshot, long offset, long length, LongRunningOperation? operation = null, long progressBase = 0)
    {
        if (offset < 0 || length < 0 || offset + length > snapshot.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _snapshot = snapshot;
        Offset = offset;
        Length = length;
        _operation = operation;
        _progressBase = progressBase;
    }

    /// <summary>範囲の先頭 (ドキュメント上のオフセット)。</summary>
    public long Offset { get; }

    public long Length { get; }

    /// <summary>これまでに読んだバイト数 (範囲の先頭から)。</summary>
    public long Position { get; private set; }

    public long Remaining => Length - Position;

    /// <summary>続きを <paramref name="destination"/> に読む。読んだバイト数 (範囲の終わりでは 0)。</summary>
    public int Read(Span<byte> destination)
    {
        _operation?.CancellationToken.ThrowIfCancellationRequested();
        int n = (int)Math.Min(destination.Length, Remaining);
        if (n == 0)
        {
            return 0;
        }

        ReadExact(_snapshot, Offset + Position, destination[..n]);
        Position += n;
        _operation?.Report(_progressBase + Position);
        return n;
    }

    /// <summary>範囲の中の <paramref name="position"/> (範囲の先頭からの位置) から読む (位置は変えない)。</summary>
    public void ReadAt(long position, Span<byte> destination)
    {
        if (position < 0 || position + destination.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        ReadExact(_snapshot, Offset + position, destination);
    }

    /// <summary>範囲の中の <paramref name="position"/> (範囲の先頭からの位置) へ移る。</summary>
    public void Seek(long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Length);
        Position = position;
    }

    /// <summary>ちょうど <paramref name="destination"/> の長さを読む (読めなければ <see cref="DataReadException"/>)。</summary>
    public static void ReadExact(DocumentSnapshot snapshot, long offset, Span<byte> destination)
    {
        ReadResult result = snapshot.Read(offset, destination);
        if (result.Unreadable.Count > 0)
        {
            throw new DataReadException(result.Unreadable[0].Offset);
        }

        if (result.BytesReturned < destination.Length)
        {
            throw new DataReadException(offset + result.BytesReturned);
        }
    }

    /// <summary>範囲の先頭 <paramref name="count"/> バイト (範囲が短ければその長さ)。</summary>
    public static byte[] Head(DocumentSnapshot snapshot, long offset, long length, int count)
    {
        byte[] head = new byte[(int)Math.Min(count, length)];
        ReadExact(snapshot, offset, head);
        return head;
    }
}

/// <summary>範囲を読む <see cref="Stream"/> (圧縮のライブラリに渡す)。</summary>
internal sealed class RangeReaderStream(RangeReader reader) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => reader.Length;

    public override long Position
    {
        get => reader.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => reader.Read(buffer);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 結果の書き込み先。<see cref="ContentSink"/> に書く (null なら数えるだけ。「圧縮データの範囲を選択」)。書いた量が
/// <see cref="Limit"/> を超えたら <see cref="LimitReached"/> を 1 回呼び、false が返れば <see cref="OutputLimitAbortedException"/> で止める
/// (EDIT-41 の仕様 7: 圧縮爆弾への対策)。
/// </summary>
public sealed class CodecOutput(ContentSink? sink)
{
    private bool _asked;

    public ContentSink? Sink { get; } = sink;

    /// <summary>書いたバイト数。</summary>
    public long Length { get; private set; }

    /// <summary>確認を出す大きさ (null なら確認しない)。</summary>
    public long? Limit { get; init; }

    /// <summary>上限を超えたときに呼ぶ (引数は書いたバイト数。true なら続ける)。処理のスレッドから呼ぶ。</summary>
    public Func<long, bool>? LimitReached { get; init; }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (!_asked && Limit is long limit && Length + data.Length > limit)
        {
            _asked = true;
            if (LimitReached is { } ask && !ask(Length + data.Length))
            {
                throw new OutputLimitAbortedException(Length + data.Length);
            }
        }

        Sink?.Write(data);
        Length += data.Length;
    }

    /// <summary>書き込みを受ける <see cref="Stream"/> (圧縮のライブラリに渡す)。</summary>
    public Stream AsStream() => new OutputStream(this);

    private sealed class OutputStream(CodecOutput output) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => output.Length;

        public override long Position
        {
            get => output.Length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => output.Write(buffer);

        public override void WriteByte(byte value) => output.Write([value]);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>展開の大きさの確認で「中止する」を選んだ (EDIT-41 の仕様 7)。</summary>
public sealed class OutputLimitAbortedException(long length) : OperationCanceledException("展開を中止しました。")
{
    public long Length { get; } = length;
}
