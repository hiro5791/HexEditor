namespace HexEditor.Core.Disassembly.Images;

/// <summary>ドキュメントのバイトを読む (戻り値は読めたバイト数)。</summary>
public delegate int ImageReader(long offset, Span<byte> destination);

/// <summary>実行形式の種類 (ANA-24 の仕様 1)。</summary>
public enum ImageFormat
{
    None,
    Pe,
    Elf,
    MachO,

    /// <summary>Mach-O のユニバーサル (fat) バイナリ。<see cref="ExecutableImage.Slices"/> から選ぶ。</summary>
    MachOFat,
}

/// <summary>
/// セクション 1 つ (ANA-24 の仕様 3)。<see cref="VirtualAddress"/> はイメージベースを足した仮想アドレス。
/// <see cref="FileOffset"/> はドキュメントの先頭からの位置 (fat の場合もドキュメント全体での位置)。
/// </summary>
public sealed record ImageSection(string Name, ulong VirtualAddress, ulong VirtualSize, long FileOffset, long FileSize,
    bool Execute, bool Read, bool Write)
{
    /// <summary>ファイル上にバイトがある仮想アドレスの範囲の終わり (仕様 4: 仮想サイズ &gt; ファイル上のサイズの部分は表示しない)。</summary>
    public ulong MappedEnd => VirtualAddress + (ulong)Math.Min((long)Math.Min(VirtualSize == 0 ? (ulong)FileSize : VirtualSize, long.MaxValue), FileSize);

    public bool ContainsAddress(ulong address) => address >= VirtualAddress && address < VirtualAddress + Math.Max(VirtualSize, (ulong)FileSize);

    public bool ContainsMapped(ulong address) => address >= VirtualAddress && address < MappedEnd;

    public bool ContainsOffset(long offset) => FileSize > 0 && offset >= FileOffset && offset < FileOffset + FileSize;
}

/// <summary>シンボルの種類 (ANA-24 の仕様 7)。</summary>
public enum ImageSymbolKind
{
    Function,
    Data,
    Import,
    Export,
}

/// <summary>シンボル 1 つ。<see cref="Name"/> は表示名 (デマングル済み。ANA-30)、<see cref="RawName"/> はファイル上の名前。</summary>
public sealed record ImageSymbol(string Name, string RawName, ulong Address, ImageSymbolKind Kind);

/// <summary>ユニバーサルバイナリのスライス 1 つ (ANA-24 の仕様 1)。<see cref="Name"/> は x86_64・arm64 など。</summary>
public sealed record ImageSlice(string Name, long Offset, long Size, string? ArchitectureId);

/// <summary>
/// ヘッダの解釈の問題 (ANA-24 の「エラー」の「ヘッダの一部を解釈できません: 理由」)。<see cref="Code"/> は理由の種類
/// (リソース <c>Disasm_HeaderIssue_&lt;Code&gt;</c>)。
/// </summary>
public sealed record ImageIssue(string Code, string Detail = "");

/// <summary>
/// 実行形式のヘッダ・セクション・シンボルを解釈した結果 (ANA-24)。ヘッダが壊れていても、解釈できた部分だけを持つ。
/// </summary>
public sealed class ExecutableImage
{
    private readonly ImageSection[] _byAddress;
    private readonly ImageSection[] _byOffset;
    private readonly Dictionary<ulong, ImageSymbol> _symbolAt = [];
    private readonly Dictionary<ulong, string> _imports;
    private readonly ulong[] _symbolAddresses;

    internal ExecutableImage(ImageFormat format, string? architectureId, bool bigEndian, ulong imageBase, ulong? entryPoint,
        IReadOnlyList<ImageSection> sections, IReadOnlyList<ImageSymbol> symbols, Dictionary<ulong, string> imports,
        IReadOnlyList<ulong> functionStarts, IReadOnlyList<ImageIssue> issues, bool isDotNet, IReadOnlyList<ImageSlice> slices, long headerSize = 0)
    {
        Format = format;
        ArchitectureId = architectureId;
        BigEndian = bigEndian;
        ImageBase = imageBase;
        EntryPoint = entryPoint;
        Sections = sections;
        Symbols = symbols;
        _imports = imports;
        FunctionStarts = functionStarts;
        Issues = issues;
        IsDotNet = isDotNet;
        Slices = slices;
        HeaderSize = headerSize;
        _byAddress = [.. sections.Where(s => s.FileSize > 0).OrderBy(s => s.VirtualAddress)];
        _byOffset = [.. sections.Where(s => s.FileSize > 0).OrderBy(s => s.FileOffset)];
        foreach (ImageSymbol s in symbols)
        {
            // 同じアドレスに複数ある場合は、関数・エクスポートを優先する。
            if (!_symbolAt.TryGetValue(s.Address, out ImageSymbol? old) || Rank(s.Kind) < Rank(old.Kind))
            {
                _symbolAt[s.Address] = s;
            }
        }

        _symbolAddresses = [.. _symbolAt.Keys.Order()];
    }

    private static int Rank(ImageSymbolKind kind) => kind switch
    {
        ImageSymbolKind.Export => 0,
        ImageSymbolKind.Function => 1,
        ImageSymbolKind.Import => 2,
        _ => 3,
    };

    /// <summary>形式でないドキュメント (ベースアドレス 0、セクションなし)。</summary>
    public static ExecutableImage Empty { get; } = new(ImageFormat.None, null, false, 0, null, [], [], [], [], [], false, []);

    public ImageFormat Format { get; }

    /// <summary>ヘッダから決まるアーキテクチャ (ANA-24 の仕様 2)。決まらなければ null。</summary>
    public string? ArchitectureId { get; }

    public bool BigEndian { get; }

    /// <summary>イメージベース (PE の ImageBase、ELF の最初の PT_LOAD、Mach-O の __TEXT の vmaddr)。</summary>
    public ulong ImageBase { get; }

    /// <summary>エントリポイントの仮想アドレス。</summary>
    public ulong? EntryPoint { get; }

    public IReadOnlyList<ImageSection> Sections { get; }

    /// <summary>シンボル (エクスポート・インポート・シンボル表)。</summary>
    public IReadOnlyList<ImageSymbol> Symbols { get; }

    /// <summary>名前のない関数の先頭 (PE の例外表の関数の範囲。ラベルの自動生成 (ANA-25 の仕様 6) に使う)。</summary>
    public IReadOnlyList<ulong> FunctionStarts { get; }

    public IReadOnlyList<ImageIssue> Issues { get; }

    /// <summary>.NET の PE (CLI ヘッダを持つ。ANA-24 の仕様 10)。</summary>
    public bool IsDotNet { get; }

    /// <summary>ユニバーサルバイナリのスライス (fat でなければ空)。</summary>
    public IReadOnlyList<ImageSlice> Slices { get; }

    /// <summary>ヘッダの大きさ (PE の SizeOfHeaders。ヘッダの範囲はオフセット = RVA として変換する)。</summary>
    public long HeaderSize { get; }

    public bool IsExecutable => Format is ImageFormat.Pe or ImageFormat.Elf or ImageFormat.MachO;

    /// <summary>アドレスを 16 桁で表示する (64 bit の形式)。</summary>
    public bool Wide => ImageBase > uint.MaxValue || Sections.Any(s => s.VirtualAddress > uint.MaxValue);

    /// <summary>実行可能なセクション (相互参照・命令パターン検索の対象。ANA-25 の仕様 7、ANA-28 の仕様 5)。</summary>
    public IEnumerable<ImageSection> ExecutableSections => Sections.Where(s => s.Execute && s.FileSize > 0);

    /// <summary>仮想アドレスのセクション (ファイル上にバイトがない部分も含む)。どれにも含まれなければ null。</summary>
    public ImageSection? SectionOfAddress(ulong address) => Sections.FirstOrDefault(s => s.ContainsAddress(address));

    /// <summary>オフセットを含むセクション。</summary>
    public ImageSection? SectionOfOffset(long offset)
    {
        int lo = 0;
        int hi = _byOffset.Length - 1;
        ImageSection? found = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_byOffset[mid].FileOffset <= offset)
            {
                found = _byOffset[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found is not null && found.ContainsOffset(offset))
        {
            return found;
        }

        // 重なったセクションがあれば線形に探す。
        return _byOffset.FirstOrDefault(s => s.ContainsOffset(offset));
    }

    /// <summary>
    /// 仮想アドレスをオフセットにする (ANA-24 の仕様 4・5)。ファイル上にバイトがない (.bss など) か、どのセクションにも含まれなければ null。
    /// PE のヘッダの範囲 (ImageBase から SizeOfHeaders まで) も変換する。
    /// </summary>
    public long? ToOffset(ulong address)
    {
        foreach (ImageSection s in _byAddress)
        {
            if (s.ContainsMapped(address))
            {
                return s.FileOffset + (long)(address - s.VirtualAddress);
            }
        }

        if (HeaderSize > 0 && address >= ImageBase && address - ImageBase < (ulong)HeaderSize)
        {
            return (long)(address - ImageBase) + BaseOffset;
        }

        return null;
    }

    /// <summary>fat のスライスの開始位置 (ヘッダの変換に使う)。</summary>
    public long BaseOffset { get; init; }

    /// <summary>オフセットを仮想アドレスにする。セクションの外は null。</summary>
    public ulong? ToAddress(long offset)
    {
        if (SectionOfOffset(offset) is { } s && (ulong)(offset - s.FileOffset) < Math.Max(s.VirtualSize, (ulong)s.FileSize))
        {
            return s.VirtualAddress + (ulong)(offset - s.FileOffset);
        }

        if (HeaderSize > 0 && offset >= BaseOffset && offset - BaseOffset < HeaderSize)
        {
            return ImageBase + (ulong)(offset - BaseOffset);
        }

        return null;
    }

    /// <summary>その仮想アドレスに付いたシンボル (なければ null)。</summary>
    public ImageSymbol? SymbolAt(ulong address) => _symbolAt.GetValueOrDefault(address);

    /// <summary>インポートの項目 (IAT・GOT・PLT・スタブ) の名前 (<c>DLL名!関数名</c>、<c>関数名@plt</c>)。なければ null。</summary>
    public string? ImportAt(ulong address) => _imports.GetValueOrDefault(address);

    /// <summary>インポートの項目の全部 (アドレスと名前)。</summary>
    public IReadOnlyDictionary<ulong, string> AllImports => _imports;

    /// <summary>インポートの項目のアドレスか (分岐先へ移動しない。ANA-25 の仕様 2)。</summary>
    public bool IsImport(ulong address) => _imports.ContainsKey(address);

    /// <summary>範囲 [start, end) にあるシンボルのアドレス (昇順)。</summary>
    public IEnumerable<ulong> SymbolAddressesIn(ulong start, ulong end)
    {
        int i = Array.BinarySearch(_symbolAddresses, start);
        if (i < 0)
        {
            i = ~i;
        }

        for (; i < _symbolAddresses.Length && _symbolAddresses[i] < end; i++)
        {
            yield return _symbolAddresses[i];
        }
    }
}
