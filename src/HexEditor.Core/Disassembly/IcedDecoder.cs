using Iced.Intel;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// x86 16 / 32 / 64 bit の逆アセンブラ (Iced)。構文は Intel / AT&amp;T / MASM (ANA-26 の仕様 1)。
/// 分岐先は Iced の解釈 (近い分岐の即値)、メモリ参照の先は RIP 相対と、ベース・インデックスのない絶対アドレス (32 bit の IAT の呼び出し)。
/// </summary>
internal sealed class IcedDecoder : IInstructionDecoder
{
    private readonly int _bitness;
    private readonly Formatter _formatter;
    private readonly StringOutput _output = new();
    private readonly InstructionInfoFactory _info = new();
    private readonly SpanReader _reader = new();

    public IcedDecoder(DisasmArchitecture architecture, DisasmFormat format)
    {
        Architecture = architecture;
        _bitness = architecture.Bitness;
        _formatter = CreateFormatter(format);
    }

    public DisasmArchitecture Architecture { get; }

    /// <summary>表示設定に合わせた Iced の書式化 (アセンブルの結果の表示にも使う)。</summary>
    internal static Formatter CreateFormatter(DisasmFormat format)
    {
        Formatter formatter = format.Syntax switch
        {
            DisasmSyntax.Att => new GasFormatter(),
            DisasmSyntax.Masm => new MasmFormatter(),
            _ => new IntelFormatter(),
        };
        FormatterOptions o = formatter.Options;
        o.SpaceAfterOperandSeparator = true;
        o.BranchLeadingZeros = false;
        o.ShowBranchSize = false;
        o.UppercaseHex = format.UppercaseHex;
        o.SignedImmediateOperands = format.SignedImmediates;
        o.UppercaseMnemonics = format.UppercaseMnemonic;
        o.UppercaseRegisters = format.UppercaseMnemonic;
        o.UppercasePrefixes = format.UppercaseMnemonic;
        o.UppercaseKeywords = format.UppercaseMnemonic;
        o.UppercaseDecorators = format.UppercaseMnemonic;
        if (format.HexStyle == HexStyle.Prefix)
        {
            o.HexPrefix = "0x";
            o.HexSuffix = null;
        }
        else
        {
            o.HexPrefix = null;
            o.HexSuffix = "h";
            o.AddLeadingZeroToHexNumbers = true;
        }

        if (format.Syntax == DisasmSyntax.Att)
        {
            o.GasShowMnemonicSizeSuffix = true;
        }
        else if (format.Syntax == DisasmSyntax.Intel)
        {
            // Capstone の Intel 構文と同じく、メモリのオペランドにはサイズを付け、+ の前後に空白を入れる (INSP-16 の仕様 3 の例)。
            o.MemorySizeOptions = MemorySizeOptions.Always;
            o.SpaceBetweenMemoryAddOperators = true;
            o.RipRelativeAddresses = true;
        }

        return formatter;
    }

    public DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false)
    {
        if (code.IsEmpty)
        {
            return null;
        }

        _reader.Set(code);
        var decoder = Decoder.Create(_bitness, _reader, address);
        decoder.Decode(out Instruction instr);
        if (instr.IsInvalid || decoder.LastError != DecoderError.None || instr.Length <= 0 || instr.Length > code.Length)
        {
            return null;
        }

        return Convert(in instr, detail);
    }

    /// <summary>Iced の命令を変換する (アセンブルの結果の表示にも使う)。</summary>
    internal DisasmInstruction Convert(in Instruction instr, bool detail)
    {
        _formatter.FormatMnemonic(instr, _output);
        string mnemonic = _output.ToStringAndReset();
        _formatter.FormatAllOperands(instr, _output);
        string operands = _output.ToStringAndReset();

        (FlowKind flow, bool indirect) = instr.FlowControl switch
        {
            FlowControl.UnconditionalBranch => (FlowKind.Jump, false),
            FlowControl.IndirectBranch => (FlowKind.Jump, true),
            FlowControl.ConditionalBranch => (FlowKind.ConditionalJump, false),
            FlowControl.Call => (FlowKind.Call, false),
            FlowControl.IndirectCall => (FlowKind.Call, true),
            FlowControl.Return => (FlowKind.Return, false),
            FlowControl.Interrupt or FlowControl.Exception => (FlowKind.Interrupt, false),
            FlowControl.XbeginXabortXend => (instr.Mnemonic == Mnemonic.Xbegin ? FlowKind.ConditionalJump : FlowKind.Normal, false),
            _ => (FlowKind.Normal, false),
        };

        ulong? branch = null;
        if (flow is FlowKind.Jump or FlowKind.ConditionalJump or FlowKind.Call && !indirect && instr.OpCount > 0
            && instr.GetOpKind(0) is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
        {
            branch = instr.NearBranchTarget;
        }
        else if (flow is FlowKind.Jump or FlowKind.Call && !indirect)
        {
            // far の分岐 (jmp far ptr) は分岐先を求めない。
            indirect = true;
        }

        ulong? memory = null;
        if (instr.IsIPRelativeMemoryOperand)
        {
            memory = instr.IPRelativeMemoryAddress;
        }
        else
        {
            for (int i = 0; i < instr.OpCount; i++)
            {
                if (instr.GetOpKind(i) == OpKind.Memory && instr.MemoryBase == Register.None && instr.MemoryIndex == Register.None)
                {
                    memory = instr.MemoryDisplacement64;
                    break;
                }
            }
        }

        return new DisasmInstruction(instr.IP, instr.Length, mnemonic, operands, flow, branch, indirect, memory,
            detail ? Details(in instr) : null);
    }

    private InstructionDetail Details(in Instruction instr)
    {
        InstructionInfo info = _info.GetInfo(instr);
        var operands = new List<string>();
        for (int i = 0; i < instr.OpCount; i++)
        {
            operands.Add($"{instr.GetOpKind(i)} ({info.GetOpAccess(i)})");
        }

        var read = new List<string>();
        var written = new List<string>();
        foreach (UsedRegister r in info.GetUsedRegisters())
        {
            string name = r.Register.ToString().ToLowerInvariant();
            if (r.Access is OpAccess.Read or OpAccess.ReadWrite or OpAccess.CondRead or OpAccess.ReadCondWrite)
            {
                read.Add(name);
            }

            if (r.Access is OpAccess.Write or OpAccess.ReadWrite or OpAccess.CondWrite or OpAccess.ReadCondWrite)
            {
                written.Add(name);
            }
        }

        var groups = new List<string> { instr.FlowControl.ToString() };
        groups.AddRange(instr.CpuidFeatures.Select(f => f.ToString()));
        return new InstructionDetail(operands, read.Distinct().ToList(), written.Distinct().ToList(), groups);
    }

    public void Dispose()
    {
    }

    /// <summary>Span を読む CodeReader (呼び出しのたびに作らない)。</summary>
    private sealed class SpanReader : CodeReader
    {
        private readonly byte[] _buffer = new byte[16];
        private int _length;
        private int _position;

        public void Set(ReadOnlySpan<byte> code)
        {
            _length = Math.Min(code.Length, _buffer.Length);
            code[.._length].CopyTo(_buffer);
            _position = 0;
        }

        public override int ReadByte() => _position < _length ? _buffer[_position++] : -1;
    }
}
