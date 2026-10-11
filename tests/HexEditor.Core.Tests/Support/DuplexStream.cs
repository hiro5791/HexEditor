using System.IO.Pipelines;

namespace HexEditor.Core.Tests.Support;

/// <summary>テスト用の双方向ストリームの組 (補助プロセスの通信を、パイプや別プロセスなしでそのまま確かめる)。</summary>
public static class DuplexStreams
{
    /// <summary>互いにつながった 2 本のストリームを作る。片方に書くともう片方で読める。</summary>
    public static (Stream A, Stream B) CreatePair()
    {
        var toB = new Pipe();
        var toA = new Pipe();
        return (new DuplexStream(toA.Reader, toB.Writer), new DuplexStream(toB.Reader, toA.Writer));
    }

    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _read = reader.AsStream();
        private readonly Stream _write = writer.AsStream();

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _write.WriteAsync(buffer, cancellationToken);

        public override void Flush() => _write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
