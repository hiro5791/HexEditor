using System.Globalization;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// 6502 系 (6502、65C02、W65C02、65816) の逆アセンブラ (本体の自前の実装。Capstone 4 にないため)。表記は Capstone 5 と同じ
/// (<c>lda #$01</c>、<c>sta $1234,x</c>)。分岐のオペランドは分岐先のアドレス。65816 の即値の長さは 8 bit (M = 1、X = 1) とみなす。
/// </summary>
internal sealed class Mos65xxDecoder : IInstructionDecoder
{
    private enum M
    {
        Imp, Acc, Imm, ImmM, ImmX, Zp, ZpX, ZpY, Abs, AbsX, AbsY, Ind, IndX, IndY, ZpInd, AbsIndX, Rel, RelLong, Long, LongX,
        DpIndLong, DpIndLongY, Sr, SrIndY, Move, AbsIndLong, ZpRel, Imm16, Brk,
    }

    // 65816 の全 256 命令 (行 = 上位 4 bit)。6502 / 65C02 / W65C02 はこの表から使える命令を選ぶ。
    private static readonly (string Name, M Mode)[] Table65816 =
    [
        ("brk", M.Brk), ("ora", M.IndX), ("cop", M.Imm), ("ora", M.Sr), ("tsb", M.Zp), ("ora", M.Zp), ("asl", M.Zp), ("ora", M.DpIndLong),
        ("php", M.Imp), ("ora", M.ImmM), ("asl", M.Acc), ("phd", M.Imp), ("tsb", M.Abs), ("ora", M.Abs), ("asl", M.Abs), ("ora", M.Long),
        ("bpl", M.Rel), ("ora", M.IndY), ("ora", M.ZpInd), ("ora", M.SrIndY), ("trb", M.Zp), ("ora", M.ZpX), ("asl", M.ZpX), ("ora", M.DpIndLongY),
        ("clc", M.Imp), ("ora", M.AbsY), ("inc", M.Acc), ("tcs", M.Imp), ("trb", M.Abs), ("ora", M.AbsX), ("asl", M.AbsX), ("ora", M.LongX),
        ("jsr", M.Abs), ("and", M.IndX), ("jsl", M.Long), ("and", M.Sr), ("bit", M.Zp), ("and", M.Zp), ("rol", M.Zp), ("and", M.DpIndLong),
        ("plp", M.Imp), ("and", M.ImmM), ("rol", M.Acc), ("pld", M.Imp), ("bit", M.Abs), ("and", M.Abs), ("rol", M.Abs), ("and", M.Long),
        ("bmi", M.Rel), ("and", M.IndY), ("and", M.ZpInd), ("and", M.SrIndY), ("bit", M.ZpX), ("and", M.ZpX), ("rol", M.ZpX), ("and", M.DpIndLongY),
        ("sec", M.Imp), ("and", M.AbsY), ("dec", M.Acc), ("tsc", M.Imp), ("bit", M.AbsX), ("and", M.AbsX), ("rol", M.AbsX), ("and", M.LongX),
        ("rti", M.Imp), ("eor", M.IndX), ("wdm", M.Imm), ("eor", M.Sr), ("mvp", M.Move), ("eor", M.Zp), ("lsr", M.Zp), ("eor", M.DpIndLong),
        ("pha", M.Imp), ("eor", M.ImmM), ("lsr", M.Acc), ("phk", M.Imp), ("jmp", M.Abs), ("eor", M.Abs), ("lsr", M.Abs), ("eor", M.Long),
        ("bvc", M.Rel), ("eor", M.IndY), ("eor", M.ZpInd), ("eor", M.SrIndY), ("mvn", M.Move), ("eor", M.ZpX), ("lsr", M.ZpX), ("eor", M.DpIndLongY),
        ("cli", M.Imp), ("eor", M.AbsY), ("phy", M.Imp), ("tcd", M.Imp), ("jml", M.Long), ("eor", M.AbsX), ("lsr", M.AbsX), ("eor", M.LongX),
        ("rts", M.Imp), ("adc", M.IndX), ("per", M.RelLong), ("adc", M.Sr), ("stz", M.Zp), ("adc", M.Zp), ("ror", M.Zp), ("adc", M.DpIndLong),
        ("pla", M.Imp), ("adc", M.ImmM), ("ror", M.Acc), ("rtl", M.Imp), ("jmp", M.Ind), ("adc", M.Abs), ("ror", M.Abs), ("adc", M.Long),
        ("bvs", M.Rel), ("adc", M.IndY), ("adc", M.ZpInd), ("adc", M.SrIndY), ("stz", M.ZpX), ("adc", M.ZpX), ("ror", M.ZpX), ("adc", M.DpIndLongY),
        ("sei", M.Imp), ("adc", M.AbsY), ("ply", M.Imp), ("tdc", M.Imp), ("jmp", M.AbsIndX), ("adc", M.AbsX), ("ror", M.AbsX), ("adc", M.LongX),
        ("bra", M.Rel), ("sta", M.IndX), ("brl", M.RelLong), ("sta", M.Sr), ("sty", M.Zp), ("sta", M.Zp), ("stx", M.Zp), ("sta", M.DpIndLong),
        ("dey", M.Imp), ("bit", M.ImmM), ("txa", M.Imp), ("phb", M.Imp), ("sty", M.Abs), ("sta", M.Abs), ("stx", M.Abs), ("sta", M.Long),
        ("bcc", M.Rel), ("sta", M.IndY), ("sta", M.ZpInd), ("sta", M.SrIndY), ("sty", M.ZpX), ("sta", M.ZpX), ("stx", M.ZpY), ("sta", M.DpIndLongY),
        ("tya", M.Imp), ("sta", M.AbsY), ("txs", M.Imp), ("txy", M.Imp), ("stz", M.Abs), ("sta", M.AbsX), ("stz", M.AbsX), ("sta", M.LongX),
        ("ldy", M.ImmX), ("lda", M.IndX), ("ldx", M.ImmX), ("lda", M.Sr), ("ldy", M.Zp), ("lda", M.Zp), ("ldx", M.Zp), ("lda", M.DpIndLong),
        ("tay", M.Imp), ("lda", M.ImmM), ("tax", M.Imp), ("plb", M.Imp), ("ldy", M.Abs), ("lda", M.Abs), ("ldx", M.Abs), ("lda", M.Long),
        ("bcs", M.Rel), ("lda", M.IndY), ("lda", M.ZpInd), ("lda", M.SrIndY), ("ldy", M.ZpX), ("lda", M.ZpX), ("ldx", M.ZpY), ("lda", M.DpIndLongY),
        ("clv", M.Imp), ("lda", M.AbsY), ("tsx", M.Imp), ("tyx", M.Imp), ("ldy", M.AbsX), ("lda", M.AbsX), ("ldx", M.AbsY), ("lda", M.LongX),
        ("cpy", M.ImmX), ("cmp", M.IndX), ("rep", M.Imm), ("cmp", M.Sr), ("cpy", M.Zp), ("cmp", M.Zp), ("dec", M.Zp), ("cmp", M.DpIndLong),
        ("iny", M.Imp), ("cmp", M.ImmM), ("dex", M.Imp), ("wai", M.Imp), ("cpy", M.Abs), ("cmp", M.Abs), ("dec", M.Abs), ("cmp", M.Long),
        ("bne", M.Rel), ("cmp", M.IndY), ("cmp", M.ZpInd), ("cmp", M.SrIndY), ("pei", M.ZpInd), ("cmp", M.ZpX), ("dec", M.ZpX), ("cmp", M.DpIndLongY),
        ("cld", M.Imp), ("cmp", M.AbsY), ("phx", M.Imp), ("stp", M.Imp), ("jml", M.AbsIndLong), ("cmp", M.AbsX), ("dec", M.AbsX), ("cmp", M.LongX),
        ("cpx", M.ImmX), ("sbc", M.IndX), ("sep", M.Imm), ("sbc", M.Sr), ("cpx", M.Zp), ("sbc", M.Zp), ("inc", M.Zp), ("sbc", M.DpIndLong),
        ("inx", M.Imp), ("sbc", M.ImmM), ("nop", M.Imp), ("xba", M.Imp), ("cpx", M.Abs), ("sbc", M.Abs), ("inc", M.Abs), ("sbc", M.Long),
        ("beq", M.Rel), ("sbc", M.IndY), ("sbc", M.ZpInd), ("sbc", M.SrIndY), ("pea", M.Imm16), ("sbc", M.ZpX), ("inc", M.ZpX), ("sbc", M.DpIndLongY),
        ("sed", M.Imp), ("sbc", M.AbsY), ("plx", M.Imp), ("xce", M.Imp), ("jsr", M.AbsIndX), ("sbc", M.AbsX), ("inc", M.AbsX), ("sbc", M.LongX),
    ];

    /// <summary>NMOS 6502 の正式な命令。</summary>
    private static readonly HashSet<byte> Nmos =
    [
        0x00, 0x01, 0x05, 0x06, 0x08, 0x09, 0x0A, 0x0D, 0x0E, 0x10, 0x11, 0x15, 0x16, 0x18, 0x19, 0x1D, 0x1E, 0x20, 0x21, 0x24, 0x25, 0x26,
        0x28, 0x29, 0x2A, 0x2C, 0x2D, 0x2E, 0x30, 0x31, 0x35, 0x36, 0x38, 0x39, 0x3D, 0x3E, 0x40, 0x41, 0x45, 0x46, 0x48, 0x49, 0x4A, 0x4C,
        0x4D, 0x4E, 0x50, 0x51, 0x55, 0x56, 0x58, 0x59, 0x5D, 0x5E, 0x60, 0x61, 0x65, 0x66, 0x68, 0x69, 0x6A, 0x6C, 0x6D, 0x6E, 0x70, 0x71,
        0x75, 0x76, 0x78, 0x79, 0x7D, 0x7E, 0x81, 0x84, 0x85, 0x86, 0x88, 0x8A, 0x8C, 0x8D, 0x8E, 0x90, 0x91, 0x94, 0x95, 0x96, 0x98, 0x99,
        0x9A, 0x9D, 0xA0, 0xA1, 0xA2, 0xA4, 0xA5, 0xA6, 0xA8, 0xA9, 0xAA, 0xAC, 0xAD, 0xAE, 0xB0, 0xB1, 0xB4, 0xB5, 0xB6, 0xB8, 0xB9, 0xBA,
        0xBC, 0xBD, 0xBE, 0xC0, 0xC1, 0xC4, 0xC5, 0xC6, 0xC8, 0xC9, 0xCA, 0xCC, 0xCD, 0xCE, 0xD0, 0xD1, 0xD5, 0xD6, 0xD8, 0xD9, 0xDD, 0xDE,
        0xE0, 0xE1, 0xE4, 0xE5, 0xE6, 0xE8, 0xE9, 0xEA, 0xEC, 0xED, 0xEE, 0xF0, 0xF1, 0xF5, 0xF6, 0xF8, 0xF9, 0xFD, 0xFE,
    ];

    /// <summary>65C02 で加わった命令。</summary>
    private static readonly HashSet<byte> Cmos =
    [
        0x04, 0x0C, 0x12, 0x14, 0x1A, 0x1C, 0x32, 0x34, 0x3A, 0x3C, 0x52, 0x5A, 0x64, 0x72, 0x74, 0x7A, 0x7C, 0x80, 0x89, 0x92, 0x9C, 0x9E,
        0xB2, 0xD2, 0xDA, 0xF2, 0xFA,
    ];

    private readonly DisasmFormat _format;
    private readonly int _level;

    public Mos65xxDecoder(DisasmArchitecture architecture, DisasmFormat format)
    {
        Architecture = architecture;
        _format = format;
        _level = architecture.Id switch
        {
            DisasmArchitectures.Mos6502 => 0,
            DisasmArchitectures.Mos65C02 => 1,
            DisasmArchitectures.W65C02 => 2,
            _ => 3,
        };
    }

    public DisasmArchitecture Architecture { get; }

    public void Dispose()
    {
    }

    private string Hex(uint value, int digits) =>
        "$" + value.ToString((_format.UppercaseHex ? "X" : "x") + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false)
    {
        if (code.IsEmpty)
        {
            return null;
        }

        byte op = code[0];
        string name;
        M mode;
        if (_level == 2 && (op & 0x0F) == 0x07)
        {
            // W65C02 の RMB / SMB (ビット番号はオペコードの上位 3 bit)。
            name = ((op & 0x80) == 0 ? "rmb" : "smb") + ((op >> 4) & 7).ToString(CultureInfo.InvariantCulture);
            mode = M.Zp;
        }
        else if (_level == 2 && (op & 0x0F) == 0x0F)
        {
            name = ((op & 0x80) == 0 ? "bbr" : "bbs") + ((op >> 4) & 7).ToString(CultureInfo.InvariantCulture);
            mode = M.ZpRel;
        }
        else
        {
            bool legal = _level switch
            {
                0 => Nmos.Contains(op),
                1 => Nmos.Contains(op) || Cmos.Contains(op),
                2 => Nmos.Contains(op) || Cmos.Contains(op) || op is 0xCB or 0xDB,
                _ => true,
            };
            if (!legal)
            {
                return null;
            }

            (name, mode) = Table65816[op];
            if (_level < 3 && mode == M.Brk)
            {
                mode = M.Imp;
            }
        }

        int length = mode switch
        {
            M.Imp or M.Acc => 1,
            M.Imm or M.ImmM or M.ImmX or M.Zp or M.ZpX or M.ZpY or M.IndX or M.IndY or M.ZpInd or M.Rel or M.DpIndLong or M.DpIndLongY
                or M.Sr or M.SrIndY or M.Brk => 2,
            M.Abs or M.AbsX or M.AbsY or M.Ind or M.AbsIndX or M.RelLong or M.Move or M.AbsIndLong or M.ZpRel or M.Imm16 => 3,
            _ => 4,
        };
        if (code.Length < length)
        {
            return null;
        }

        uint b1 = length > 1 ? code[1] : 0u;
        uint w = length > 2 ? b1 | ((uint)code[2] << 8) : b1;
        uint l = length > 3 ? w | ((uint)code[3] << 16) : w;
        ulong next = address + (ulong)length;
        ulong? target = null;
        string operand = mode switch
        {
            M.Imp => string.Empty,
            M.Acc => "a",
            M.Imm or M.ImmM or M.ImmX or M.Brk => "#" + Hex(b1, 2),
            M.Imm16 => "#" + Hex(w, 4),
            M.Zp => Hex(b1, 2),
            M.ZpX => Hex(b1, 2) + ",x",
            M.ZpY => Hex(b1, 2) + ",y",
            M.Abs => Hex(w, 4),
            M.AbsX => Hex(w, 4) + ",x",
            M.AbsY => Hex(w, 4) + ",y",
            M.Ind => "(" + Hex(w, 4) + ")",
            M.IndX => "(" + Hex(b1, 2) + ",x)",
            M.IndY => "(" + Hex(b1, 2) + "),y",
            M.ZpInd => "(" + Hex(b1, 2) + ")",
            M.AbsIndX => "(" + Hex(w, 4) + ",x)",
            M.AbsIndLong => "[" + Hex(w, 4) + "]",
            M.Long => Hex(l, 6),
            M.LongX => Hex(l, 6) + ",x",
            M.DpIndLong => "[" + Hex(b1, 2) + "]",
            M.DpIndLongY => "[" + Hex(b1, 2) + "],y",
            M.Sr => Hex(b1, 2) + ",s",
            M.SrIndY => "(" + Hex(b1, 2) + ",s),y",
            M.Move => "#" + Hex(code[2], 2) + ", #" + Hex(b1, 2),
            _ => string.Empty,
        };
        if (mode == M.Rel)
        {
            target = next + (ulong)(long)(sbyte)(byte)b1;
            operand = Hex((uint)(target.Value & 0xFFFF), 4);
        }
        else if (mode == M.RelLong)
        {
            target = next + (ulong)(long)(short)(ushort)w;
            operand = Hex((uint)(target.Value & 0xFFFF), 4);
        }
        else if (mode == M.ZpRel)
        {
            target = next + (ulong)(long)(sbyte)code[2];
            operand = Hex(b1, 2) + ", " + Hex((uint)(target.Value & 0xFFFF), 4);
        }

        FlowKind flow = name switch
        {
            "jmp" or "jml" or "bra" or "brl" => FlowKind.Jump,
            "jsr" or "jsl" => FlowKind.Call,
            "rts" or "rti" or "rtl" => FlowKind.Return,
            "brk" or "cop" => FlowKind.Interrupt,
            _ when mode is M.Rel or M.ZpRel => FlowKind.ConditionalJump,
            _ => FlowKind.Normal,
        };
        bool indirect = false;
        if (flow is FlowKind.Jump or FlowKind.Call && target is null)
        {
            if (mode is M.Abs)
            {
                target = w;
            }
            else if (mode is M.Long)
            {
                target = l;
            }
            else
            {
                indirect = true;
            }
        }

        if (_format.UppercaseMnemonic)
        {
            name = name.ToUpperInvariant();
            operand = operand.ToUpperInvariant();
        }

        return new DisasmInstruction(address, length, name, operand, flow, target, indirect);
    }
}
