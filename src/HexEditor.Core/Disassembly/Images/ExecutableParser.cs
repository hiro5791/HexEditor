using HexEditor.Core.Disassembly.Demangling;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Disassembly.Images;

/// <summary>
/// 実行形式の解釈の入口 (ANA-24)。先頭が PE・ELF・Mach-O (fat を含む) なら解釈し、そうでなければ <see cref="ExecutableImage.Empty"/>。
/// ヘッダが壊れていても例外にせず、解釈できた部分と <see cref="ExecutableImage.Issues"/> を返す。シンボルの表示名はデマングルする (ANA-30)。
/// ヘッダとシンボル表の大きさにだけ比例し、ファイルサイズには比例しない。
/// </summary>
public static class ExecutableParser
{
    public static ExecutableImage Parse(DocumentSnapshot snapshot, ImageSlice? slice = null, CancellationToken token = default) =>
        Parse((offset, destination) => snapshot.Read(offset, destination).BytesReturned, snapshot.Length, slice, token);

    public static ExecutableImage Parse(byte[] data, ImageSlice? slice = null) =>
        Parse((offset, destination) =>
        {
            int n = (int)Math.Clamp(data.Length - offset, 0, destination.Length);
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }, data.Length, slice);

    /// <summary>
    /// 解釈する。<paramref name="slice"/> はユニバーサルバイナリのスライス (null なら最初のスライス。ANA-24 の仕様 1)。
    /// </summary>
    public static ExecutableImage Parse(ImageReader read, long length, ImageSlice? slice = null, CancellationToken token = default)
    {
        var d = new ImageData(read, length) { Token = token };
        try
        {
            ExecutableImage image;
            if (PeParser.IsPe(d))
            {
                image = PeParser.Parse(d);
            }
            else if (ElfParser.IsElf(d))
            {
                image = ElfParser.Parse(d);
            }
            else if (MachOParser.IsFat(d))
            {
                ExecutableImage fat = MachOParser.ParseFat(d);
                ImageSlice? chosen = slice ?? fat.Slices.FirstOrDefault();
                if (chosen is null || !MachOParser.IsMachO(d, chosen.Offset))
                {
                    return fat;
                }

                image = MachOParser.Parse(d, chosen.Offset, fat.Slices);
            }
            else if (MachOParser.IsMachO(d, 0))
            {
                image = MachOParser.Parse(d, 0, []);
            }
            else
            {
                return ExecutableImage.Empty;
            }

            return WithDemangledNames(image);
        }
        catch (ImageFormatException ex)
        {
            return new ExecutableImage(ImageFormat.None, null, false, 0, null, [], [], [], [], [new ImageIssue(ex.Code, ex.Detail)], false, []);
        }
    }

    /// <summary>シンボルとインポートの表示名をデマングルする (ANA-24 の仕様 7、ANA-30 の仕様 7)。</summary>
    private static ExecutableImage WithDemangledNames(ExecutableImage image)
    {
        var symbols = new List<ImageSymbol>(image.Symbols.Count);
        foreach (ImageSymbol s in image.Symbols)
        {
            symbols.Add(s with { Name = DisplayName(s.RawName, image.Format) });
        }

        var imports = new Dictionary<ulong, string>();
        foreach ((ulong address, string name) in image.AllImports)
        {
            imports[address] = DisplayName(name, image.Format);
        }

        return new ExecutableImage(image.Format, image.ArchitectureId, image.BigEndian, image.ImageBase, image.EntryPoint, image.Sections, symbols,
            imports, image.FunctionStarts, image.Issues, image.IsDotNet, image.Slices, image.HeaderSize) { BaseOffset = image.BaseOffset };
    }

    /// <summary>
    /// シンボル名の表示名。Mach-O の C の名前は先頭の <c>_</c> を 1 つ外してからデマングルする (<c>__ZN3foo3barEi</c>)。
    /// <c>@plt</c> などの接尾辞は残す。
    /// </summary>
    public static string DisplayName(string raw, ImageFormat format)
    {
        string suffix = string.Empty;
        string name = raw;
        int at = name.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            suffix = name[at..];
            name = name[..at];
        }

        int bang = name.IndexOf('!', StringComparison.Ordinal);
        string prefix = string.Empty;
        if (bang > 0)
        {
            prefix = name[..(bang + 1)];
            name = name[(bang + 1)..];
        }

        if (format is ImageFormat.MachO or ImageFormat.MachOFat && name.StartsWith("__Z", StringComparison.Ordinal))
        {
            name = name[1..];
        }

        DemangleResult r = Demangler.Demangle(name);
        return prefix + (r.Success ? r.Text : name) + suffix;
    }
}
