using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>
/// 組み込みのカービング定義 (12 の FOR-18 の仕様 3)。各定義はフッターだけに頼らず、構造の検証 (<see cref="StructureValidators"/>) か
/// サイズの取得を使う。編集はできない (無効にすることと、複製して編集することはできる)。
/// </summary>
public static class BuiltInSignatures
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    private static CarvingSignature Def(string name, string extension, string[] headers, long maxSize, long minSize = 0, long headerOffset = 0, string? structure = null,
        SizeFieldRule? sizeField = null, string? footer = null) =>
        new()
        {
            Name = name,
            Extension = extension,
            Headers = headers,
            HeaderOffset = headerOffset,
            MaxSize = maxSize,
            MinSize = minSize,
            Structure = structure,
            SizeField = sizeField,
            Footer = footer,
            BuiltIn = true,
        };

    public static IReadOnlyList<CarvingSignature> All { get; } =
    [
        Def("JPEG", "jpg", ["FF D8 FF"], 100 * MiB, 125, structure: "jpeg"),
        Def("PNG", "png", ["89 50 4E 47 0D 0A 1A 0A"], 100 * MiB, 67, structure: "png"),
        Def("GIF", "gif", ["47 49 46 38 37 61", "47 49 46 38 39 61"], 50 * MiB, 20, structure: "gif"),
        Def("BMP", "bmp", ["42 4D"], 256 * MiB, 58, structure: "bmp"),
        Def("TIFF", "tif", ["49 49 2A 00", "4D 4D 00 2A"], 512 * MiB, 26, structure: "tiff"),
        Def("PDF", "pdf", ["25 50 44 46 2D"], 512 * MiB, 64, structure: "pdf"),
        Def("ZIP", "zip", ["50 4B 03 04"], 4 * GiB, 22, structure: "zip"),
        Def("RAR", "rar", ["52 61 72 21 1A 07 00", "52 61 72 21 1A 07 01 00"], 4 * GiB, 20, structure: "rar"),
        Def("7z", "7z", ["37 7A BC AF 27 1C"], 4 * GiB, 32, structure: "7z"),
        Def("GZIP", "gz", ["1F 8B 08"], 4 * GiB, 18, structure: "gzip"),
        Def("BZIP2", "bz2", ["42 5A 68 ?? 31 41 59 26 53 59"], 4 * GiB, 14, structure: "bzip2"),
        Def("MP3", "mp3", ["49 44 33"], 200 * MiB, 1024, structure: "mp3"),
        Def("MP4", "mp4", ["66 74 79 70"], 8 * GiB, 32, headerOffset: 4, structure: "mp4"),
        Def("AVI", "avi", ["52 49 46 46 ?? ?? ?? ?? 41 56 49 20"], 4 * GiB, 64, sizeField: new SizeFieldRule { Offset = 4, Bits = 32, Add = 8 }),
        Def("WAV", "wav", ["52 49 46 46 ?? ?? ?? ?? 57 41 56 45"], GiB, 44, sizeField: new SizeFieldRule { Offset = 4, Bits = 32, Add = 8 }),
        Def("SQLite", "sqlite", ["53 51 4C 69 74 65 20 66 6F 72 6D 61 74 20 33 00"], 2 * GiB, 512, structure: "sqlite"),
        Def("PE", "exe", ["4D 5A"], 512 * MiB, 512, structure: "pe"),
        Def("ELF", "elf", ["7F 45 4C 46"], 512 * MiB, 52, structure: "elf"),
        Def("OLE", "doc", ["D0 CF 11 E0 A1 B1 1A E1"], 512 * MiB, 1536, structure: "ole"),
        Def("Registry hive", "hiv", ["72 65 67 66"], 512 * MiB, 4096, sizeField: new SizeFieldRule { Offset = 0x28, Bits = 32, Add = 4096 }),
        Def("EVTX", "evtx", ["45 6C 66 46 69 6C 65 00"], GiB, 69632, structure: "evtx"),
        Def("LNK", "lnk", ["4C 00 00 00 01 14 02 00 00 00 00 00 C0 00 00 00 00 00 00 46"], MiB, 76, structure: "lnk"),
        Def("RTF", "rtf", ["7B 5C 72 74 66 31"], 64 * MiB, 10, structure: "rtf"),
        Def("HTML", "html", ["3C 68 74 6D 6C", "3C 48 54 4D 4C", "3C 21 44 4F 43 54 59 50 45 20 68 74 6D 6C", "3C 21 44 4F 43 54 59 50 45 20 48 54 4D 4C"], 16 * MiB, 20,
            structure: "html"),
        Def("XML", "xml", ["3C 3F 78 6D 6C 20"], 64 * MiB, 10, structure: "xml"),
    ];

    private static readonly HashSet<string> Names = new(All.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

    public static bool IsBuiltInName(string name) => Names.Contains(name);
}

/// <summary>構造の検証の名前から検証を引く (組み込みの検証が先、なければ構造体テンプレート)。</summary>
public interface IStructureResolver
{
    IStructureValidator? Resolve(string name);
}

/// <summary>組み込みの検証だけを引く。</summary>
public sealed class NativeStructureResolver : IStructureResolver
{
    public static NativeStructureResolver Instance { get; } = new();

    public IStructureValidator? Resolve(string name) => StructureValidators.Find(name);
}

/// <summary>組み込みの検証にない名前を、構造体テンプレート (07) の名前として引く。テンプレートの解析が成功したものだけを結果に含め、解析結果の終端を長さとする。</summary>
public sealed class TemplateStructureResolver(TemplateCatalog catalog) : IStructureResolver
{
    private readonly Dictionary<string, IStructureValidator?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IStructureValidator? Resolve(string name)
    {
        if (StructureValidators.Find(name) is { } native)
        {
            return native;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(name, out IStructureValidator? cached))
            {
                return cached;
            }

            IStructureValidator? validator = null;
            if (catalog.Find(name) is { } entry)
            {
                try
                {
                    TemplateProgram program = catalog.Load(entry);
                    if (!program.HasErrors)
                    {
                        validator = new TemplateValidator(name, program);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    validator = null;
                }
            }

            _cache[name] = validator;
            return validator;
        }
    }

    private sealed class TemplateValidator(string name, TemplateProgram program) : IStructureValidator
    {
        public string Name { get; } = name;

        public long? Measure(ByteWindow window, long start)
        {
            long available = window.Limit - start;
            if (available <= 0)
            {
                return null;
            }

            using TemplateResult result = TemplateEngine.ApplyAndWait(program, new WindowData(window.Data, start, available), new TemplateApplyOptions { StartOffset = 0 },
                window.CancellationToken);
            if (result.State != TemplateRunState.Completed || result.ErrorCount > 0)
            {
                return null;
            }

            long end = 0;
            long seen = 0;
            foreach (TemplateNode child in result.Root.Children)
            {
                end = Math.Max(end, child.End);
                if (++seen >= 1_000_000)
                {
                    break;
                }
            }

            return end > 0 && end <= available ? start + end : null;
        }
    }

    private sealed class WindowData(ICarveData data, long baseOffset, long length) : TemplateDataSource
    {
        public override long Length => length;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            unreadableAt = -1;
            if (offset >= length)
            {
                return 0;
            }

            int count = (int)Math.Min(destination.Length, length - offset);
            Sources.ReadResult r = data.Read(baseOffset + offset, destination[..count]);
            if (r.Unreadable.Count > 0)
            {
                unreadableAt = r.Unreadable.Min(u => u.Offset) - baseOffset;
            }

            return r.BytesReturned;
        }
    }
}
