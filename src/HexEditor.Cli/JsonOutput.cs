using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HexEditor.Cli;

/// <summary>
/// JSON の出力 (AUTO-36 の仕様 5)。先頭に <c>"schema": "hexeditor.&lt;サブコマンド&gt;/1"</c> を書き、キーは訳さない。
/// <see cref="TextWriter"/> に UTF-8 のまま流す (件数に比例するメモリを使わない)。
/// </summary>
internal sealed class JsonOutput : IDisposable
{
    private readonly TextWriter _writer;

    public JsonOutput(TextWriter writer, string command)
    {
        _writer = writer;
        W = new Utf8JsonWriter(new TextWriterStream(writer), new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = "\n",
        });
        W.WriteStartObject();
        W.WriteString("schema", $"hexeditor.{command}/1");
    }

    public Utf8JsonWriter W { get; }

    /// <summary>オブジェクトを閉じて、末尾に改行を書く。</summary>
    public void Finish()
    {
        W.WriteEndObject();
        W.Flush();
        _writer.Write('\n');
    }

    public void Dispose() => W.Dispose();

    /// <summary>UTF-8 のバイト列を <see cref="TextWriter"/> の文字にして渡すだけの書き込み専用のストリーム。</summary>
    private sealed class TextWriterStream(TextWriter writer) : Stream
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly char[] _chars = new char[4096];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => writer.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                _decoder.Convert(buffer, _chars, flush: false, out int used, out int produced, out _);
                writer.Write(_chars, 0, produced);
                buffer = buffer[used..];
            }
        }
    }
}
