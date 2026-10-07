namespace HexEditor.Core.Sources;

/// <summary>メモリ上のバイト列を元データにするデータソース。テストと、元データのない新規ドキュメントで使う。</summary>
public sealed class MemoryByteSource : ByteSourceBase
{
    private readonly byte[] _data;

    public MemoryByteSource(byte[] data, string displayName = "", SourceCapabilities capabilities =
        SourceCapabilities.CanWrite | SourceCapabilities.CanResize)
    {
        _data = data;
        DisplayName = displayName;
        Capabilities = capabilities;
        Identity = "memory:" + Guid.NewGuid().ToString("N");
    }

    /// <summary>元データのない新規ドキュメント用の空のデータソース。</summary>
    public static MemoryByteSource CreateEmpty(string displayName) => new([], displayName);

    public override string DisplayName { get; }

    public override string Identity { get; }

    public override long Length => _data.Length;

    public override SourceCapabilities Capabilities { get; }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        _data.AsSpan((int)offset, count).CopyTo(buffer);
        return new ReadResult(count);
    }

    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (!Capabilities.HasFlag(SourceCapabilities.CanWrite))
        {
            base.Write(offset, data);
        }

        data.CopyTo(_data.AsSpan(checked((int)offset)));
    }
}
