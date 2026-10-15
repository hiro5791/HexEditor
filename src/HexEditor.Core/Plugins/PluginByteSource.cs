using HexEditor.Core.Scripting;
using HexEditor.Core.Sources;
using HexEditor.Sdk.Plugins;

namespace HexEditor.Core.Plugins;

/// <summary>
/// プラグインのデータソース (AUTO-31 の <c>IByteSourceProvider</c>) を本体のデータソースにする (01 の ENG-01 の形)。
/// 読み込みの例外は「読めない範囲」として返し (ENG の規則)、プラグインの見張りが記録する。読み取り専用か・長さを変えられるかは宣言に従う。
/// </summary>
public sealed class PluginByteSource : ByteSourceBase
{
    private readonly RegisteredExtension _registration;
    private readonly IPluginByteSource _source;
    private readonly SourceCapabilities _capabilities;

    private PluginByteSource(RegisteredExtension registration, IPluginByteSource source, bool readOnly)
    {
        _registration = registration;
        _source = source;
        _capabilities = readOnly ? SourceCapabilities.None : SourceCapabilities.CanWrite;
        DisplayName = registration.Guard.Invoke("DisplayName", () => source.DisplayName, registration.DisplayName);

        // 長さを変えられないデータソースとして扱うため、開いたときの長さに固定する (読み込みのたびにプラグインを呼ばない)。
        _length = Math.Max(0, registration.Guard.Invoke("Length", () => source.Length, 0L));
        Identity = "plugin:" + registration.Id + ":" + Guid.NewGuid().ToString("N");
    }

    /// <summary>開く。プラグインの例外は <see cref="PluginCallException"/> になる。</summary>
    public static PluginByteSource Open(RegisteredExtension registration, IByteSourceProvider provider, string? argument)
    {
        IPluginByteSource source = registration.Guard.InvokeOrThrow("Open", () => provider.Open(argument))
            ?? throw new PluginCallException(Msg.T("データソースを開けませんでした。", "The data source could not be opened."));
        bool readOnly = registration.Guard.Invoke("IsReadOnly", () => provider.IsReadOnly, true);
        return new PluginByteSource(registration, source, readOnly);
    }

    public override string DisplayName { get; }

    public override string Identity { get; }

    private readonly long _length;

    public override long Length => _length;

    public override SourceCapabilities Capabilities => _capabilities;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        if (count == 0)
        {
            return new ReadResult(0);
        }

        if (_registration.Guard.IsStopped)
        {
            return new ReadResult(count, [new UnreadableRange(offset, count, UnreadableReason.IoError)]);
        }

        byte[] temp = new byte[count];
        int n;
        try
        {
            n = Math.Clamp(_source.Read(offset, temp), 0, count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _registration.Guard.Fault("Read", ex);
            buffer[..count].Clear();
            return new ReadResult(count, [new UnreadableRange(offset, count, UnreadableReason.IoError)]);
        }

        temp.AsSpan(0, n).CopyTo(buffer);
        if (n < count)
        {
            buffer[n..count].Clear();
            return new ReadResult(count, [new UnreadableRange(offset + n, count - n, UnreadableReason.IoError)]);
        }

        return new ReadResult(count);
    }

    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (!_capabilities.HasFlag(SourceCapabilities.CanWrite))
        {
            base.Write(offset, data);
            return;
        }

        byte[] copy = data.ToArray();
        _registration.Guard.InvokeOrThrow("Write", () => _source.Write(offset, copy));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _registration.Guard.Invoke("Dispose", () =>
            {
                _source.Dispose();
                return true;
            }, false);
        }

        base.Dispose(disposing);
    }
}
