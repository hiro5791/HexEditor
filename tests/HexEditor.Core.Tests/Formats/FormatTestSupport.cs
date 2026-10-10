using System.Text;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Formats;

namespace HexEditor.Core.Tests.Formats;

/// <summary>形式の変換のテストの補助。</summary>
internal static class FormatTestSupport
{
    public static string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "HexEditorTests", "formats");

    public static ImportResult Import(string format, string text, ImportOptions? options = null) =>
        Import(format, Encoding.UTF8.GetBytes(text), options);

    public static ImportResult Import(string format, byte[] content, ImportOptions? options = null)
    {
        using var stream = new MemoryStream(content);
        return Importer.Decode(stream, (options ?? new ImportOptions()) with { Format = format }, TempDirectory, "test");
    }

    public static ImportResult ImportFile(string format, string path, ImportOptions? options = null) =>
        Importer.DecodeFile(path, (options ?? new ImportOptions()) with { Format = format }, TempDirectory);

    /// <summary>デコードした内容すべて (ギャップは塗りつぶしの値)。</summary>
    public static byte[] Bytes(ImportResult result)
    {
        SparseImage image = result.Image!;
        byte[] bytes = new byte[image.Length];
        image.Read(0, bytes);
        return bytes;
    }

    public static ExportSource Source(byte[] data, long baseAddress = 0) => new()
    {
        Read = (offset, destination) => data.AsSpan((int)offset, destination.Length).CopyTo(destination),
        Length = data.Length,
        BaseAddress = baseAddress,
        FileName = "data.bin",
        Now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
    };

    public static string Export(byte[] data, ExportOptions options, long baseAddress = 0)
    {
        using var stream = new MemoryStream();
        Exporter.Write(Source(data, baseAddress), [(0, data.Length)], options, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static byte[] ExportBytes(byte[] data, ExportOptions options, long baseAddress = 0)
    {
        using var stream = new MemoryStream();
        Exporter.Write(Source(data, baseAddress), [(0, data.Length)], options, stream);
        return stream.ToArray();
    }

    public static ByteReader Reader(byte[] data) => (offset, destination) =>
    {
        destination.Clear();
        if (offset < data.Length)
        {
            data.AsSpan((int)offset, (int)Math.Min(destination.Length, data.Length - offset)).CopyTo(destination);
        }
    };
}
