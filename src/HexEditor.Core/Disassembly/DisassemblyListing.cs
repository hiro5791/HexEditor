using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Disassembly;

/// <summary>リスティングの行の種類。</summary>
public enum ListingLineKind
{
    /// <summary>見出し (<c>main:</c>、<c>sub_140001040:</c>。ANA-24 の仕様 9、ANA-25 の仕様 6)。</summary>
    Heading,
    Instruction,
}

/// <summary>
/// リスティングの 1 行 (ANA-23 の仕様 4)。<see cref="Offset"/> と <see cref="Length"/> はドキュメント上の命令の範囲 (見出しは命令と同じ位置)。
/// <see cref="TargetOffset"/> は分岐先・参照先のドキュメント上の位置 (ドキュメントに含まれなければ null)。
/// </summary>
public sealed record ListingLine(
    ListingLineKind Kind,
    long Offset,
    int Length,
    ulong Address,
    DisasmInstruction? Instruction,
    string? Label,
    string Bytes,
    string Comment,
    long? TargetOffset)
{
    /// <summary>列をタブ区切りにした文字列 (コピー。ANA-23 の仕様 8)。</summary>
    public string ToTabSeparated(string address) => Kind == ListingLineKind.Heading
        ? Label + ":"
        : string.Join('\t', address, Bytes, Instruction!.Mnemonic, Instruction.Operands, Comment).TrimEnd('\t');
}

/// <summary>
/// 逆アセンブルの設定 (ツールバーの値。ANA-23 の画面、ANA-24 の仕様 2)。<see cref="BaseAddress"/> は手入力したベースアドレス
/// (null なら実行形式で決まる値、実行形式でなければ 0)。
/// </summary>
public sealed record DisasmViewOptions
{
    public string ArchitectureId { get; init; } = DisasmArchitectures.Default;

    /// <summary>エンディアン (null ならアーキテクチャの既定)。</summary>
    public bool? BigEndian { get; init; }

    public ulong? BaseAddress { get; init; }

    public DisasmFormat Format { get; init; } = DisasmFormat.Default;
}

/// <summary>
/// 逆アセンブルのリスティング (ANA-23)。表示に必要な範囲だけを解釈する: 表示位置の命令は、その位置を含むチェックポイント (4 KB ごと、または
/// セクションの先頭) から線形スイープで解釈して求め、チェックポイントの命令境界は解釈した結果を記憶して再利用する (「巨大ファイル」)。
/// 記憶するのはチェックポイントごとの境界と、見た分岐先 (ラベルの自動生成) だけで、ファイルサイズに比例しない。
/// ドキュメントを編集したら <see cref="Invalidate"/> で編集位置以降の記憶を捨てる (ANA-23 の仕様 9)。
/// </summary>
public sealed class DisassemblyListing : IDisposable
{
    /// <summary>チェックポイントの間隔。</summary>
    public const int CheckpointSize = 4096;

    /// <summary>ラベルの自動生成で、バックグラウンドで先に解釈する実行可能なセクションの合計の上限。</summary>
    public const long LabelScanLimit = 64L * 1024 * 1024;

    private readonly Func<DocumentSnapshot> _snapshot;
    private readonly ConcurrentDictionary<long, long> _checkpoints = new();
    private readonly ConcurrentDictionary<ulong, bool> _targets = new();
    private readonly object _decoderLock = new();
    private IInstructionDecoder? _decoder;
    private string? _decoderError;

    public DisassemblyListing(Func<DocumentSnapshot> snapshot, ExecutableImage image, DisasmViewOptions options)
    {
        _snapshot = snapshot;
        Image = image;
        Options = options;
        Architecture = DisasmArchitectures.Find(options.ArchitectureId) ?? DisasmArchitectures.Get(DisasmArchitectures.Default);
        foreach (ulong f in image.FunctionStarts)
        {
            _targets.TryAdd(f, true);
        }
    }

    public ExecutableImage Image { get; }

    public DisasmViewOptions Options { get; }

    public DisasmArchitecture Architecture { get; }

    public DisasmFormat Format => Options.Format;

    /// <summary>エンジンを読み込めないときの理由 (ANA-23 の「エラー」)。読み込めれば null。</summary>
    public string? EngineError
    {
        get
        {
            Decoder();
            return _decoderError;
        }
    }

    public long Length => _snapshot().Length;

    /// <summary>アドレスを 16 桁で表示するか。</summary>
    public bool WideAddress => Image.IsExecutable ? Image.Wide : Architecture.WideAddress;

    private IInstructionDecoder? Decoder()
    {
        if (_decoder is null && _decoderError is null)
        {
            try
            {
                _decoder = Disassembler.Create(Architecture, Format, Options.BigEndian ?? (Architecture.HasEndian && Image.IsExecutable ? Image.BigEndian : null));
            }
            catch (DisassemblerUnavailableException ex)
            {
                _decoderError = ex.Message;
            }
        }

        return _decoder;
    }

    // ---- アドレスの変換 (ANA-23 の仕様 3、ANA-24 の仕様 4) ----

    /// <summary>オフセットの表示上のアドレス。</summary>
    public ulong AddressOf(long offset)
    {
        if (Image.IsExecutable && Image.ToAddress(offset) is { } va)
        {
            return Options.BaseAddress is { } manual ? va - Image.ImageBase + manual : va;
        }

        return (ulong)offset + (Options.BaseAddress ?? 0);
    }

    /// <summary>表示上のアドレスのオフセット。ドキュメントに含まれなければ null。</summary>
    public long? OffsetOf(ulong address)
    {
        if (Image.IsExecutable)
        {
            ulong va = Options.BaseAddress is { } manual ? address - manual + Image.ImageBase : address;
            if (Image.ToOffset(va) is { } offset && offset < Length)
            {
                return offset;
            }

            return null;
        }

        ulong b = Options.BaseAddress ?? 0;
        return address >= b && address - b < (ulong)Length ? (long)(address - b) : null;
    }

    /// <summary>アドレス列の文字列 (ANA-26 の仕様 6)。</summary>
    public string AddressText(ListingLine line) => Format.AddressColumn switch
    {
        AddressColumn.Offset => line.Offset.ToString(Format.UppercaseHex ? "X8" : "x8", CultureInfo.InvariantCulture),
        AddressColumn.Both => Format.Address(line.Address, WideAddress) + " " + line.Offset.ToString(Format.UppercaseHex ? "X8" : "x8", CultureInfo.InvariantCulture),
        _ => Format.Address(line.Address, WideAddress),
    };

    // ---- 解釈 ----

    /// <summary>オフセットを含む解釈の起点 (チェックポイントかセクションの先頭)。</summary>
    private long CheckpointFor(long offset)
    {
        long checkpoint = offset / CheckpointSize * CheckpointSize;
        if (Image.SectionOfOffset(offset) is { } section && section.FileOffset > checkpoint)
        {
            return section.FileOffset;
        }

        return checkpoint;
    }

    /// <summary>次のセクションの先頭 (命令がまたがらないように切る位置)。なければ long.MaxValue。</summary>
    private long NextSectionStart(long offset)
    {
        long next = long.MaxValue;
        foreach (ImageSection s in Image.Sections)
        {
            if (s.FileSize > 0 && s.FileOffset > offset && s.FileOffset < next)
            {
                next = s.FileOffset;
            }
        }

        return next;
    }

    private readonly record struct Decoded(long Offset, DisasmInstruction Instruction);

    /// <summary><paramref name="offset"/> から 1 命令を解釈する。データがなければ null。</summary>
    private Decoded? DecodeAt(DocumentSnapshot snapshot, long offset, Span<byte> buffer, long limit)
    {
        long available = Math.Min(snapshot.Length, limit) - offset;
        if (available <= 0)
        {
            return null;
        }

        int count = (int)Math.Min(buffer.Length, available);
        int read = snapshot.Read(offset, buffer[..count]).BytesReturned;
        if (read <= 0)
        {
            return null;
        }

        IInstructionDecoder? decoder = Decoder();
        DisasmInstruction instruction;
        ulong address = AddressOf(offset);
        if (decoder is null || AlignedStart(offset) != offset)
        {
            instruction = DisasmInstruction.Data(address, buffer[0], Format);
        }
        else
        {
            lock (_decoderLock)
            {
                instruction = Disassembler.DecodeOrData(decoder, buffer[..read], address, Format);
            }
        }

        return new Decoded(offset, instruction);
    }

    /// <summary>
    /// 固定長の命令のアーキテクチャで解釈できない語は、語の各バイトを <c>db</c> にする (次の命令は語の境界から。ANA-23 の TC-ANA-23-05)。
    /// </summary>
    private int StepOf(Decoded d) => d.Instruction.IsInvalid && !Architecture.VariableLength ? 1 : d.Instruction.Length;

    private long AlignedStart(long offset)
    {
        if (Architecture.VariableLength || Architecture.Alignment <= 1)
        {
            return offset;
        }

        long baseOffset = Image.SectionOfOffset(offset)?.FileOffset ?? 0;
        return baseOffset + (offset - baseOffset) / Architecture.Alignment * Architecture.Alignment;
    }

    /// <summary>
    /// <paramref name="offset"/> のバイトを含む命令の先頭 (ANA-23 の仕様 6)。チェックポイントから解釈して求める。
    /// </summary>
    public long InstructionStart(long offset)
    {
        DocumentSnapshot snapshot = _snapshot();
        offset = Math.Clamp(offset, 0, Math.Max(0, snapshot.Length - 1));
        if (!Architecture.VariableLength)
        {
            return AlignedStart(offset);
        }

        long checkpoint = CheckpointFor(offset);
        long at;
        if (_checkpoints.TryGetValue(checkpoint, out long known) && known <= offset)
        {
            at = known;
        }
        else if (checkpoint > 0 && (Image.SectionOfOffset(offset) is not { } section || section.FileOffset < checkpoint))
        {
            // 境界をまだ知らない: 1 つ前のチェックポイントから解釈して境界を求める (前の命令がまたがっている場合も含む)。
            long previous = CheckpointFor(checkpoint - 1);
            at = _checkpoints.TryGetValue(previous, out long p) ? p : previous;
        }
        else
        {
            at = checkpoint;
        }

        Span<byte> buffer = stackalloc byte[Math.Max(16, Architecture.MaxLength)];
        long limit = NextSectionStart(at);
        while (true)
        {
            if (DecodeAt(snapshot, at, buffer, limit) is not { } d)
            {
                return at;
            }

            long next = at + StepOf(d);
            Remember(at, next);
            if (next > offset)
            {
                return at;
            }

            at = next;
        }
    }

    /// <summary>チェックポイントをまたいだ命令の後ろの境界を記憶する。</summary>
    private void Remember(long at, long next)
    {
        long boundary = (at / CheckpointSize + 1) * CheckpointSize;
        if (next >= boundary && at < boundary)
        {
            _checkpoints[boundary] = next;
        }
    }

    /// <summary>
    /// <paramref name="offset"/> を含む命令から <paramref name="count"/> 行 (見出しを含む) を作る。<paramref name="skip"/> は最初の命令の
    /// 見出しのうち飛ばす数 (スクロールの位置)。
    /// </summary>
    public IReadOnlyList<ListingLine> Lines(long offset, int count, int skip = 0)
    {
        var lines = new List<ListingLine>(count + 4);
        DocumentSnapshot snapshot = _snapshot();
        if (snapshot.Length == 0)
        {
            return lines;
        }

        long at = InstructionStart(offset);
        Span<byte> buffer = stackalloc byte[Math.Max(16, Architecture.MaxLength)];
        long limit = NextSectionStart(at);
        bool first = true;
        while (lines.Count < count)
        {
            if (at >= limit)
            {
                limit = NextSectionStart(at);
            }

            if (DecodeAt(snapshot, at, buffer, limit) is not { } d)
            {
                break;
            }

            int step = StepOf(d);
            DisasmInstruction instruction = d.Instruction;
            if (step != instruction.Length)
            {
                instruction = instruction with { Length = step };
            }

            if (instruction.BranchTarget is { } t)
            {
                _targets.AddOrUpdate(t, instruction.Flow == FlowKind.Call, (_, old) => old || instruction.Flow == FlowKind.Call);
            }

            List<string> headings = Headings(instruction.Address);
            for (int i = first ? skip : 0; i < headings.Count && lines.Count < count; i++)
            {
                lines.Add(new ListingLine(ListingLineKind.Heading, at, step, instruction.Address, null, headings[i], string.Empty, string.Empty, null));
            }

            if (lines.Count < count)
            {
                lines.Add(MakeLine(snapshot, at, instruction));
            }

            Remember(at, at + step);
            first = false;
            at += step;
        }

        return lines;
    }

    /// <summary>見出しの数 (スクロールの位置の計算に使う)。</summary>
    public int HeadingCount(long instructionOffset) => Headings(AddressOf(instructionOffset)).Count;

    /// <summary>
    /// 直前の命令の先頭 (上方向のスクロール)。<paramref name="offset"/> が先頭なら null。
    /// </summary>
    public long? PreviousInstruction(long offset)
    {
        if (offset <= 0)
        {
            return null;
        }

        return InstructionStart(offset - 1);
    }

    private ListingLine MakeLine(DocumentSnapshot snapshot, long offset, DisasmInstruction instruction)
    {
        int shown = Math.Min(instruction.Length, Format.MaxBytes);
        Span<byte> bytes = stackalloc byte[Math.Max(shown, 1)];
        int read = snapshot.Read(offset, bytes[..shown]).BytesReturned;
        string hex = Convert.ToHexString(bytes[..read]);
        var sb = new StringBuilder(hex.Length * 3 / 2 + 2);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Format.UppercaseHex ? hex.AsSpan(i, 2) : hex.Substring(i, 2).ToLowerInvariant());
        }

        if (instruction.Length > Format.MaxBytes)
        {
            sb.Append(" …");
        }

        ulong? target = instruction.Target;
        long? targetOffset = target is { } tt ? OffsetOf(tt) : null;
        return new ListingLine(ListingLineKind.Instruction, offset, instruction.Length, instruction.Address, instruction, null, sb.ToString(),
            Comment(snapshot, instruction), targetOffset);
    }

    // ---- 見出しとコメント (ANA-24 の仕様 8・9、ANA-25 の仕様 6) ----

    private List<string> Headings(ulong address)
    {
        var list = new List<string>(1);
        if (Image.EntryPoint is { } entry && ImageAddress(address) == entry && Image.SymbolAt(entry) is null)
        {
            list.Add("entry");
        }

        if (Image.SymbolAt(ImageAddress(address)) is { } symbol)
        {
            list.Add(symbol.Name);
        }
        else if (_targets.TryGetValue(address, out bool called))
        {
            list.Add(LabelName(address, called));
        }

        return list;
    }

    /// <summary>表示上のアドレスを実行形式の仮想アドレスにする (手入力のベースアドレスを外す)。</summary>
    private ulong ImageAddress(ulong address) =>
        Image.IsExecutable && Options.BaseAddress is { } manual ? address - manual + Image.ImageBase : address;

    /// <summary>自動のラベル名 (<c>sub_</c> は call の先、<c>loc_</c> は jmp・条件分岐の先)。</summary>
    public string LabelName(ulong address, bool called) =>
        (called ? "sub_" : "loc_") + address.ToString(Format.UppercaseHex ? "X" : "x", CultureInfo.InvariantCulture);

    /// <summary>アドレスの名前 (シンボル、インポート、自動のラベル)。なければ null。</summary>
    public string? NameOf(ulong address)
    {
        ulong image = ImageAddress(address);
        if (Image.ImportAt(image) is { } import)
        {
            return import;
        }

        if (Image.SymbolAt(image) is { } symbol)
        {
            return symbol.Name;
        }

        return _targets.TryGetValue(address, out bool called) ? LabelName(address, called) : null;
    }

    private string Comment(DocumentSnapshot snapshot, DisasmInstruction instruction)
    {
        if (!Format.ShowComments || instruction.Target is not { } target)
        {
            return string.Empty;
        }

        if (NameOf(target) is { } name)
        {
            return name;
        }

        return instruction.MemoryTarget is { } memory && OffsetOf(memory) is { } offset ? StringAt(snapshot, offset) ?? string.Empty : string.Empty;
    }

    /// <summary>参照先の文字列 (ASCII か UTF-16 で 4 文字以上。ANA-24 の仕様 8)。</summary>
    internal static string? StringAt(DocumentSnapshot snapshot, long offset)
    {
        Span<byte> b = stackalloc byte[128];
        int n = snapshot.Read(offset, b).BytesReturned;
        int ascii = 0;
        while (ascii < n && b[ascii] is >= 0x20 and < 0x7F)
        {
            ascii++;
        }

        if (ascii >= 4 && (ascii == n || b[ascii] == 0))
        {
            return "\"" + Encoding.ASCII.GetString(b[..Math.Min(ascii, 64)]) + "\"";
        }

        int chars = 0;
        while (chars * 2 + 1 < n && b[chars * 2] is >= 0x20 and < 0x7F && b[chars * 2 + 1] == 0)
        {
            chars++;
        }

        if (chars >= 4)
        {
            return "L\"" + Encoding.Unicode.GetString(b[..(Math.Min(chars, 64) * 2)]) + "\"";
        }

        return null;
    }

    // ---- ラベルの自動生成のための先読み ----

    /// <summary>見た分岐先の数 (テスト用)。</summary>
    public int KnownTargetCount => _targets.Count;

    /// <summary>
    /// 実行可能なセクション (合計 <see cref="LabelScanLimit"/> まで) を線形スイープで解釈して、分岐先を集める (バックグラウンドで呼ぶ)。
    /// 実行形式でなければ何もしない。
    /// </summary>
    public void ScanLabels(CancellationToken token)
    {
        if (!Image.IsExecutable || Decoder() is null)
        {
            return;
        }

        IInstructionDecoder decoder;
        try
        {
            decoder = Disassembler.Create(Architecture, Format, Options.BigEndian ?? (Architecture.HasEndian ? Image.BigEndian : null));
        }
        catch (DisassemblerUnavailableException)
        {
            return;
        }

        using (decoder)
        {
            DocumentSnapshot snapshot = _snapshot();
            long budget = LabelScanLimit;
            foreach (ImageSection section in Image.ExecutableSections)
            {
                long end = Math.Min(section.FileOffset + section.FileSize, snapshot.Length);
                foreach ((_, DisasmInstruction i) in SweepRange(snapshot, decoder, section.FileOffset, Math.Min(end, section.FileOffset + budget), token))
                {
                    if (i.BranchTarget is { } t && OffsetOf(t) is not null)
                    {
                        _targets.AddOrUpdate(t, i.Flow == FlowKind.Call, (_, old) => old || i.Flow == FlowKind.Call);
                    }
                }

                budget -= section.FileSize;
                if (budget <= 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>範囲を線形スイープで解釈する (相互参照・命令パターン検索の共通の処理)。</summary>
    internal IEnumerable<(long Offset, DisasmInstruction Instruction)> SweepRange(DocumentSnapshot snapshot, IInstructionDecoder decoder, long start, long end,
        CancellationToken token, Action<long>? progress = null)
    {
        const int Chunk = 64 * 1024;
        byte[] buffer = new byte[Chunk + 64];
        long at = start;
        while (at < end)
        {
            token.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, end - at);
            int read = snapshot.Read(at, buffer.AsSpan(0, want)).BytesReturned;
            if (read <= 0)
            {
                yield break;
            }

            int pos = 0;
            int stop = at + read >= end ? read : Math.Max(1, read - 64);
            while (pos < stop)
            {
                DisasmInstruction i = AlignedStart(at + pos) != at + pos
                    ? DisasmInstruction.Data(AddressOf(at + pos), buffer[pos], Format)
                    : Disassembler.DecodeOrData(decoder, buffer.AsSpan(pos, read - pos), AddressOf(at + pos), Format);
                int step = i.IsInvalid && !Architecture.VariableLength ? 1 : i.Length;
                yield return (at + pos, step == i.Length ? i : i with { Length = step });
                pos += step;
            }

            at += pos;
            progress?.Invoke(at - start);
        }
    }

    /// <summary>オフセット以降の記憶を捨てる (ドキュメントの編集。ANA-23 の仕様 9)。</summary>
    public void Invalidate(long offset)
    {
        foreach (long key in _checkpoints.Keys)
        {
            if (key + CheckpointSize > offset)
            {
                _checkpoints.TryRemove(key, out _);
            }
        }
    }

    /// <summary>表示範囲のうち <c>db</c> の行の割合が 50% 以上か (「このアーキテクチャのコードではない可能性があります」)。</summary>
    public static bool MostlyData(IReadOnlyList<ListingLine> lines)
    {
        int instructions = lines.Count(l => l.Kind == ListingLineKind.Instruction);
        int data = lines.Count(l => l.Instruction?.IsInvalid == true);
        return instructions >= 4 && data * 2 >= instructions;
    }

    public void Dispose()
    {
        lock (_decoderLock)
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }
}
