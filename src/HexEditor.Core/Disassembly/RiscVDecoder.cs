using System.Buffers.Binary;
using System.Globalization;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// RISC-V の逆アセンブラ (本体の自前の実装。Capstone 4 にないため)。RV32 / RV64 の I・M・A・F・D・Zicsr・Zifencei と、
/// 圧縮命令 (C 拡張。モード RV32C / RV64C のときだけ) を解釈する。表記は LLVM と同じ ABI のレジスタ名で、疑似命令にしない
/// (<c>jalr zero, 0(ra)</c>、<c>c.jr ra</c>)。分岐・ジャンプのオペランドは分岐先の絶対アドレス。
/// </summary>
internal sealed class RiscVDecoder : IInstructionDecoder
{
    private static readonly string[] X =
    [
        "zero", "ra", "sp", "gp", "tp", "t0", "t1", "t2", "s0", "s1", "a0", "a1", "a2", "a3", "a4", "a5",
        "a6", "a7", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9", "s10", "s11", "t3", "t4", "t5", "t6",
    ];

    private static readonly string[] F =
    [
        "ft0", "ft1", "ft2", "ft3", "ft4", "ft5", "ft6", "ft7", "fs0", "fs1", "fa0", "fa1", "fa2", "fa3", "fa4", "fa5",
        "fa6", "fa7", "fs2", "fs3", "fs4", "fs5", "fs6", "fs7", "fs8", "fs9", "fs10", "fs11", "ft8", "ft9", "ft10", "ft11",
    ];

    private readonly bool _rv64;
    private readonly bool _compressed;
    private readonly DisasmFormat _format;

    public RiscVDecoder(DisasmArchitecture architecture, DisasmFormat format)
    {
        Architecture = architecture;
        _format = format;
        _rv64 = architecture.Id is DisasmArchitectures.RiscV64 or DisasmArchitectures.RiscV64C;
        _compressed = architecture.Id is DisasmArchitectures.RiscV32C or DisasmArchitectures.RiscV64C;
    }

    public DisasmArchitecture Architecture { get; }

    public void Dispose()
    {
    }

    public DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false)
    {
        if (code.Length < 2)
        {
            return null;
        }

        ushort low = BinaryPrimitives.ReadUInt16LittleEndian(code);
        if ((low & 3) != 3)
        {
            return _compressed ? Compressed(low, address) : null;
        }

        if ((low & 0x1C) == 0x1C || code.Length < 4)
        {
            // 48 bit 以上の命令は対象外。
            return null;
        }

        return Standard(BinaryPrimitives.ReadUInt32LittleEndian(code), address);
    }

    private string H(long value) => value < 0 ? "-" + _format.Hex((ulong)(-value)) : _format.Hex((ulong)value);

    private static string D(long value) => value.ToString(CultureInfo.InvariantCulture);

    private DisasmInstruction I(ulong address, int length, string mnemonic, string operands, FlowKind flow = FlowKind.Normal,
        ulong? target = null, bool indirect = false) =>
        new(address, length, _format.UppercaseMnemonic ? mnemonic.ToUpperInvariant() : mnemonic,
            _format.UppercaseMnemonic ? operands.ToUpperInvariant() : operands, flow, target, indirect);

    private static long Sign(long value, int bits) => (value << (64 - bits)) >> (64 - bits);

    private DisasmInstruction? Standard(uint ins, ulong pc)
    {
        uint opcode = ins & 0x7F;
        int rd = (int)((ins >> 7) & 31);
        uint f3 = (ins >> 12) & 7;
        int rs1 = (int)((ins >> 15) & 31);
        int rs2 = (int)((ins >> 20) & 31);
        uint f7 = ins >> 25;
        long immI = Sign(ins >> 20, 12);
        long immS = Sign(((ins >> 25) << 5) | ((ins >> 7) & 31), 12);
        switch (opcode)
        {
            case 0x37:
                return I(pc, 4, "lui", $"{X[rd]}, {H(ins >> 12)}");
            case 0x17:
                return I(pc, 4, "auipc", $"{X[rd]}, {H(ins >> 12)}") with { MemoryTarget = pc + (ulong)Sign(ins & 0xFFFFF000, 32) };
            case 0x6F:
            {
                long imm = Sign(((ins >> 31) << 20) | (((ins >> 12) & 0xFF) << 12) | (((ins >> 20) & 1) << 11) | (((ins >> 21) & 0x3FF) << 1), 21);
                ulong target = pc + (ulong)imm;
                return I(pc, 4, "jal", $"{X[rd]}, {H((long)target)}", rd == 0 ? FlowKind.Jump : FlowKind.Call, target);
            }

            case 0x67 when f3 == 0:
            {
                FlowKind flow = rd == 0 && rs1 == 1 && immI == 0 ? FlowKind.Return : rd == 0 ? FlowKind.Jump : FlowKind.Call;
                return I(pc, 4, "jalr", $"{X[rd]}, {D(immI)}({X[rs1]})", flow, null, flow != FlowKind.Return);
            }

            case 0x63:
            {
                string? m = f3 switch { 0 => "beq", 1 => "bne", 4 => "blt", 5 => "bge", 6 => "bltu", 7 => "bgeu", _ => null };
                if (m is null)
                {
                    return null;
                }

                long imm = Sign(((ins >> 31) << 12) | (((ins >> 7) & 1) << 11) | (((ins >> 25) & 0x3F) << 5) | (((ins >> 8) & 0xF) << 1), 13);
                ulong target = pc + (ulong)imm;
                return I(pc, 4, m, $"{X[rs1]}, {X[rs2]}, {H((long)target)}", FlowKind.ConditionalJump, target);
            }

            case 0x03:
            {
                string? m = f3 switch
                {
                    0 => "lb", 1 => "lh", 2 => "lw", 3 when _rv64 => "ld", 4 => "lbu", 5 => "lhu", 6 when _rv64 => "lwu", _ => null,
                };
                return m is null ? null : I(pc, 4, m, $"{X[rd]}, {D(immI)}({X[rs1]})");
            }

            case 0x23:
            {
                string? m = f3 switch { 0 => "sb", 1 => "sh", 2 => "sw", 3 when _rv64 => "sd", _ => null };
                return m is null ? null : I(pc, 4, m, $"{X[rs2]}, {D(immS)}({X[rs1]})");
            }

            case 0x13:
                return OpImm(ins, pc, rd, f3, rs1, immI, word: false);
            case 0x1B when _rv64:
                return OpImm(ins, pc, rd, f3, rs1, immI, word: true);
            case 0x33:
                return Op(pc, rd, f3, rs1, rs2, f7, word: false);
            case 0x3B when _rv64:
                return Op(pc, rd, f3, rs1, rs2, f7, word: true);
            case 0x0F:
                return f3 switch
                {
                    0 => I(pc, 4, "fence", Fence((ins >> 24) & 0xF, (ins >> 20) & 0xF)),
                    1 => I(pc, 4, "fence.i", string.Empty),
                    _ => null,
                };
            case 0x73:
                return System(ins, pc, rd, f3, rs1);
            case 0x2F:
                return Atomic(ins, pc, rd, f3, rs1, rs2);
            case 0x07:
                return f3 switch
                {
                    2 => I(pc, 4, "flw", $"{F[rd]}, {D(immI)}({X[rs1]})"),
                    3 => I(pc, 4, "fld", $"{F[rd]}, {D(immI)}({X[rs1]})"),
                    _ => null,
                };
            case 0x27:
                return f3 switch
                {
                    2 => I(pc, 4, "fsw", $"{F[rs2]}, {D(immS)}({X[rs1]})"),
                    3 => I(pc, 4, "fsd", $"{F[rs2]}, {D(immS)}({X[rs1]})"),
                    _ => null,
                };
            case 0x43 or 0x47 or 0x4B or 0x4F:
            {
                uint fmt = (ins >> 25) & 3;
                if (fmt > 1)
                {
                    return null;
                }

                string m = opcode switch { 0x43 => "fmadd", 0x47 => "fmsub", 0x4B => "fnmsub", _ => "fnmadd" } + (fmt == 0 ? ".s" : ".d");
                return I(pc, 4, m, $"{F[rd]}, {F[rs1]}, {F[rs2]}, {F[(int)(ins >> 27)]}");
            }

            case 0x53:
                return FloatOp(pc, rd, f3, rs1, rs2, f7);
            default:
                return null;
        }
    }

    private DisasmInstruction? OpImm(uint ins, ulong pc, int rd, uint f3, int rs1, long imm, bool word)
    {
        string w = word ? "w" : string.Empty;
        int shamtBits = word || !_rv64 ? 5 : 6;
        long shamt = (ins >> 20) & ((1u << shamtBits) - 1);
        uint high = ins >> (20 + shamtBits);
        switch (f3)
        {
            case 0:
                return I(pc, 4, "addi" + w, $"{X[rd]}, {X[rs1]}, {D(imm)}");
            case 1:
                return high == 0 ? I(pc, 4, "slli" + w, $"{X[rd]}, {X[rs1]}, {D(shamt)}") : null;
            case 5:
            {
                // 上位のビット: RV64 の 6 bit のシフト量なら ins[31:26]、5 bit なら ins[31:25] (srai は 0x10 / 0x20)。
                string? m = shamtBits == 6
                    ? high switch { 0 => "srli", 0x10 => "srai", _ => null }
                    : high switch { 0 => "srli", 0x20 => "srai", _ => null };
                return m is null ? null : I(pc, 4, m + w, $"{X[rd]}, {X[rs1]}, {D(shamt)}");
            }
        }

        if (word)
        {
            return null;
        }

        string? name = f3 switch { 2 => "slti", 3 => "sltiu", 4 => "xori", 6 => "ori", 7 => "andi", _ => null };
        return name is null ? null : I(pc, 4, name, $"{X[rd]}, {X[rs1]}, {D(imm)}");
    }

    private DisasmInstruction? Op(ulong pc, int rd, uint f3, int rs1, int rs2, uint f7, bool word)
    {
        string? m = (f7, f3) switch
        {
            (0, 0) => "add",
            (0x20, 0) => "sub",
            (0, 1) => "sll",
            (0, 2) when !word => "slt",
            (0, 3) when !word => "sltu",
            (0, 4) when !word => "xor",
            (0, 5) => "srl",
            (0x20, 5) => "sra",
            (0, 6) when !word => "or",
            (0, 7) when !word => "and",
            (1, 0) => "mul",
            (1, 1) when !word => "mulh",
            (1, 2) when !word => "mulhsu",
            (1, 3) when !word => "mulhu",
            (1, 4) => "div",
            (1, 5) => "divu",
            (1, 6) => "rem",
            (1, 7) => "remu",
            _ => null,
        };
        return m is null ? null : I(pc, 4, m + (word ? "w" : string.Empty), $"{X[rd]}, {X[rs1]}, {X[rs2]}");
    }

    private static string Fence(uint pred, uint succ)
    {
        static string Bits(uint v) => v == 0 ? "0" : ((v & 8) != 0 ? "i" : "") + ((v & 4) != 0 ? "o" : "") + ((v & 2) != 0 ? "r" : "") + ((v & 1) != 0 ? "w" : "");
        return Bits(pred) + ", " + Bits(succ);
    }

    private DisasmInstruction? System(uint ins, ulong pc, int rd, uint f3, int rs1)
    {
        if (f3 == 0)
        {
            return ins switch
            {
                0x00000073 => I(pc, 4, "ecall", string.Empty, FlowKind.Interrupt),
                0x00100073 => I(pc, 4, "ebreak", string.Empty, FlowKind.Interrupt),
                0x30200073 => I(pc, 4, "mret", string.Empty, FlowKind.Return),
                0x10200073 => I(pc, 4, "sret", string.Empty, FlowKind.Return),
                0x00200073 => I(pc, 4, "uret", string.Empty, FlowKind.Return),
                0x10500073 => I(pc, 4, "wfi", string.Empty),
                _ when (ins & 0xFE007FFF) == 0x12000073 => I(pc, 4, "sfence.vma", $"{X[rs1]}, {X[(int)((ins >> 20) & 31)]}"),
                _ => null,
            };
        }

        string? m = f3 switch { 1 => "csrrw", 2 => "csrrs", 3 => "csrrc", 5 => "csrrwi", 6 => "csrrsi", 7 => "csrrci", _ => null };
        if (m is null)
        {
            return null;
        }

        uint csr = ins >> 20;
        string name = csr switch
        {
            0x001 => "fflags", 0x002 => "frm", 0x003 => "fcsr", 0xC00 => "cycle", 0xC01 => "time", 0xC02 => "instret",
            0x300 => "mstatus", 0x305 => "mtvec", 0x341 => "mepc", 0x342 => "mcause", 0x343 => "mtval", 0x304 => "mie", 0x344 => "mip",
            0x340 => "mscratch", 0xF14 => "mhartid", 0x100 => "sstatus", 0x105 => "stvec", 0x141 => "sepc", 0x142 => "scause", 0x180 => "satp",
            _ => _format.Hex(csr),
        };
        string source = f3 >= 5 ? D(rs1) : X[rs1];
        return I(pc, 4, m, $"{X[rd]}, {name}, {source}");
    }

    private DisasmInstruction? Atomic(uint ins, ulong pc, int rd, uint f3, int rs1, int rs2)
    {
        string? size = f3 switch { 2 => ".w", 3 when _rv64 => ".d", _ => null };
        if (size is null)
        {
            return null;
        }

        uint f5 = ins >> 27;
        string order = ((ins >> 26) & 1) != 0 ? ((ins >> 25) & 1) != 0 ? ".aqrl" : ".aq" : ((ins >> 25) & 1) != 0 ? ".rl" : string.Empty;
        if (f5 == 0x02)
        {
            return rs2 == 0 ? I(pc, 4, "lr" + size + order, $"{X[rd]}, ({X[rs1]})") : null;
        }

        string? m = f5 switch
        {
            0x03 => "sc", 0x01 => "amoswap", 0x00 => "amoadd", 0x04 => "amoxor", 0x0C => "amoand", 0x08 => "amoor",
            0x10 => "amomin", 0x14 => "amomax", 0x18 => "amominu", 0x1C => "amomaxu", _ => null,
        };
        return m is null ? null : I(pc, 4, m + size + order, $"{X[rd]}, {X[rs2]}, ({X[rs1]})");
    }

    private DisasmInstruction? FloatOp(ulong pc, int rd, uint f3, int rs1, int rs2, uint f7)
    {
        string s = (f7 & 1) == 0 ? ".s" : ".d";
        uint op = f7 & ~1u;
        string Fff(string m) => $"{F[rd]}, {F[rs1]}, {F[rs2]}";
        switch (op)
        {
            case 0x00: return I(pc, 4, "fadd" + s, Fff("fadd"));
            case 0x04: return I(pc, 4, "fsub" + s, Fff("fsub"));
            case 0x08: return I(pc, 4, "fmul" + s, Fff("fmul"));
            case 0x0C: return I(pc, 4, "fdiv" + s, Fff("fdiv"));
            case 0x2C when rs2 == 0: return I(pc, 4, "fsqrt" + s, $"{F[rd]}, {F[rs1]}");
            case 0x10:
                return f3 switch
                {
                    0 => I(pc, 4, "fsgnj" + s, Fff("")),
                    1 => I(pc, 4, "fsgnjn" + s, Fff("")),
                    2 => I(pc, 4, "fsgnjx" + s, Fff("")),
                    _ => null,
                };
            case 0x14:
                return f3 switch
                {
                    0 => I(pc, 4, "fmin" + s, Fff("")),
                    1 => I(pc, 4, "fmax" + s, Fff("")),
                    _ => null,
                };
            case 0x50:
            {
                string? m = f3 switch { 0 => "fle", 1 => "flt", 2 => "feq", _ => null };
                return m is null ? null : I(pc, 4, m + s, $"{X[rd]}, {F[rs1]}, {F[rs2]}");
            }

            case 0x60:
            {
                string? t = rs2 switch { 0 => "w", 1 => "wu", 2 when _rv64 => "l", 3 when _rv64 => "lu", _ => null };
                return t is null ? null : I(pc, 4, "fcvt." + t + s, $"{X[rd]}, {F[rs1]}");
            }

            case 0x68:
            {
                string? t = rs2 switch { 0 => "w", 1 => "wu", 2 when _rv64 => "l", 3 when _rv64 => "lu", _ => null };
                return t is null ? null : I(pc, 4, "fcvt" + s + "." + t, $"{F[rd]}, {X[rs1]}");
            }

            case 0x20 when f7 == 0x20 && rs2 == 1:
                return I(pc, 4, "fcvt.s.d", $"{F[rd]}, {F[rs1]}");
            case 0x20 when f7 == 0x21 && rs2 == 0:
                return I(pc, 4, "fcvt.d.s", $"{F[rd]}, {F[rs1]}");
            case 0x70 when rs2 == 0:
                return f3 switch
                {
                    0 when f7 == 0x70 => I(pc, 4, "fmv.x.w", $"{X[rd]}, {F[rs1]}"),
                    0 when f7 == 0x71 && _rv64 => I(pc, 4, "fmv.x.d", $"{X[rd]}, {F[rs1]}"),
                    1 => I(pc, 4, "fclass" + s, $"{X[rd]}, {F[rs1]}"),
                    _ => null,
                };
            case 0x78 when rs2 == 0 && f3 == 0:
                return f7 == 0x78 ? I(pc, 4, "fmv.w.x", $"{F[rd]}, {X[rs1]}")
                    : _rv64 ? I(pc, 4, "fmv.d.x", $"{F[rd]}, {X[rs1]}") : null;
            default:
                return null;
        }
    }

    // ---- 圧縮命令 (C 拡張) ----

    private static int Bits(uint v, int hi, int lo) => (int)((v >> lo) & ((1u << (hi - lo + 1)) - 1));

    private DisasmInstruction? Compressed(ushort c, ulong pc)
    {
        uint v = c;
        int op = (int)(v & 3);
        int f3 = Bits(v, 15, 13);
        int rdFull = Bits(v, 11, 7);
        int rs2Full = Bits(v, 6, 2);
        string rdP = X[Bits(v, 4, 2) + 8];
        string rs1P = X[Bits(v, 9, 7) + 8];
        if (c == 0)
        {
            return null;
        }

        switch (op)
        {
            case 0:
                switch (f3)
                {
                    case 0:
                    {
                        int imm = (Bits(v, 12, 11) << 4) | (Bits(v, 10, 7) << 6) | (Bits(v, 6, 6) << 2) | (Bits(v, 5, 5) << 3);
                        return imm == 0 ? null : I(pc, 2, "c.addi4spn", $"{rdP}, sp, {D(imm)}");
                    }

                    case 1:
                        return I(pc, 2, "c.fld", $"{F[Bits(v, 4, 2) + 8]}, {D(UimmD(v))}({rs1P})");
                    case 2:
                        return I(pc, 2, "c.lw", $"{rdP}, {D(UimmW(v))}({rs1P})");
                    case 3:
                        return _rv64 ? I(pc, 2, "c.ld", $"{rdP}, {D(UimmD(v))}({rs1P})") : I(pc, 2, "c.flw", $"{F[Bits(v, 4, 2) + 8]}, {D(UimmW(v))}({rs1P})");
                    case 5:
                        return I(pc, 2, "c.fsd", $"{F[Bits(v, 4, 2) + 8]}, {D(UimmD(v))}({rs1P})");
                    case 6:
                        return I(pc, 2, "c.sw", $"{rdP}, {D(UimmW(v))}({rs1P})");
                    case 7:
                        return _rv64 ? I(pc, 2, "c.sd", $"{rdP}, {D(UimmD(v))}({rs1P})") : I(pc, 2, "c.fsw", $"{F[Bits(v, 4, 2) + 8]}, {D(UimmW(v))}({rs1P})");
                    default:
                        return null;
                }

            case 1:
            {
                long imm6 = Sign((Bits(v, 12, 12) << 5) | Bits(v, 6, 2), 6);
                switch (f3)
                {
                    case 0:
                        return rdFull == 0 ? I(pc, 2, "c.nop", string.Empty) : I(pc, 2, "c.addi", $"{X[rdFull]}, {D(imm6)}");
                    case 1 when !_rv64:
                    {
                        ulong target = pc + (ulong)JImm(v);
                        return I(pc, 2, "c.jal", H((long)target), FlowKind.Call, target);
                    }

                    case 1:
                        return rdFull == 0 ? null : I(pc, 2, "c.addiw", $"{X[rdFull]}, {D(imm6)}");
                    case 2:
                        return I(pc, 2, "c.li", $"{X[rdFull]}, {D(imm6)}");
                    case 3 when rdFull == 2:
                    {
                        long imm = Sign((Bits(v, 12, 12) << 9) | (Bits(v, 6, 6) << 4) | (Bits(v, 5, 5) << 6) | (Bits(v, 4, 3) << 7) | (Bits(v, 2, 2) << 5), 10);
                        return imm == 0 ? null : I(pc, 2, "c.addi16sp", $"sp, {D(imm)}");
                    }

                    case 3:
                    {
                        long imm = Sign((Bits(v, 12, 12) << 5) | Bits(v, 6, 2), 6);
                        return imm == 0 || rdFull == 0 ? null : I(pc, 2, "c.lui", $"{X[rdFull]}, {H(imm & 0xFFFFF)}");
                    }

                    case 4:
                    {
                        int shamt = (Bits(v, 12, 12) << 5) | Bits(v, 6, 2);
                        switch (Bits(v, 11, 10))
                        {
                            case 0:
                                return I(pc, 2, "c.srli", $"{rs1P}, {D(shamt)}");
                            case 1:
                                return I(pc, 2, "c.srai", $"{rs1P}, {D(shamt)}");
                            case 2:
                                return I(pc, 2, "c.andi", $"{rs1P}, {D(imm6)}");
                        }

                        string? m = (Bits(v, 12, 12), Bits(v, 6, 5)) switch
                        {
                            (0, 0) => "c.sub", (0, 1) => "c.xor", (0, 2) => "c.or", (0, 3) => "c.and",
                            (1, 0) when _rv64 => "c.subw", (1, 1) when _rv64 => "c.addw", _ => null,
                        };
                        return m is null ? null : I(pc, 2, m, $"{rs1P}, {rdP}");
                    }

                    case 5:
                    {
                        ulong target = pc + (ulong)JImm(v);
                        return I(pc, 2, "c.j", H((long)target), FlowKind.Jump, target);
                    }

                    default:
                    {
                        long imm = Sign((Bits(v, 12, 12) << 8) | (Bits(v, 11, 10) << 3) | (Bits(v, 6, 5) << 6) | (Bits(v, 4, 3) << 1) | (Bits(v, 2, 2) << 5), 9);
                        ulong target = pc + (ulong)imm;
                        return I(pc, 2, f3 == 6 ? "c.beqz" : "c.bnez", $"{rs1P}, {H((long)target)}", FlowKind.ConditionalJump, target);
                    }
                }
            }

            case 2:
                switch (f3)
                {
                    case 0:
                    {
                        int shamt = (Bits(v, 12, 12) << 5) | rs2Full;
                        return rdFull == 0 ? null : I(pc, 2, "c.slli", $"{X[rdFull]}, {D(shamt)}");
                    }

                    case 1:
                        return I(pc, 2, "c.fldsp", $"{F[rdFull]}, {D(UimmLdsp(v))}(sp)");
                    case 2:
                        return rdFull == 0 ? null : I(pc, 2, "c.lwsp", $"{X[rdFull]}, {D(UimmLwsp(v))}(sp)");
                    case 3:
                        return _rv64
                            ? rdFull == 0 ? null : I(pc, 2, "c.ldsp", $"{X[rdFull]}, {D(UimmLdsp(v))}(sp)")
                            : I(pc, 2, "c.flwsp", $"{F[rdFull]}, {D(UimmLwsp(v))}(sp)");
                    case 4:
                        if (Bits(v, 12, 12) == 0)
                        {
                            if (rs2Full == 0)
                            {
                                if (rdFull == 0)
                                {
                                    return null;
                                }

                                FlowKind flow = rdFull == 1 ? FlowKind.Return : FlowKind.Jump;
                                return I(pc, 2, "c.jr", X[rdFull], flow, null, flow != FlowKind.Return);
                            }

                            return rdFull == 0 ? null : I(pc, 2, "c.mv", $"{X[rdFull]}, {X[rs2Full]}");
                        }

                        if (rs2Full == 0)
                        {
                            return rdFull == 0 ? I(pc, 2, "c.ebreak", string.Empty, FlowKind.Interrupt) : I(pc, 2, "c.jalr", X[rdFull], FlowKind.Call, null, true);
                        }

                        return rdFull == 0 ? null : I(pc, 2, "c.add", $"{X[rdFull]}, {X[rs2Full]}");
                    case 5:
                        return I(pc, 2, "c.fsdsp", $"{F[rs2Full]}, {D(UimmSdsp(v))}(sp)");
                    case 6:
                        return I(pc, 2, "c.swsp", $"{X[rs2Full]}, {D(UimmSwsp(v))}(sp)");
                    default:
                        return _rv64 ? I(pc, 2, "c.sdsp", $"{X[rs2Full]}, {D(UimmSdsp(v))}(sp)") : I(pc, 2, "c.fswsp", $"{F[rs2Full]}, {D(UimmSwsp(v))}(sp)");
                }

            default:
                return null;
        }
    }

    private static int UimmW(uint v) => (Bits(v, 12, 10) << 3) | (Bits(v, 6, 6) << 2) | (Bits(v, 5, 5) << 6);

    private static int UimmD(uint v) => (Bits(v, 12, 10) << 3) | (Bits(v, 6, 5) << 6);

    private static int UimmLwsp(uint v) => (Bits(v, 12, 12) << 5) | (Bits(v, 6, 4) << 2) | (Bits(v, 3, 2) << 6);

    private static int UimmLdsp(uint v) => (Bits(v, 12, 12) << 5) | (Bits(v, 6, 5) << 3) | (Bits(v, 4, 2) << 6);

    private static int UimmSwsp(uint v) => (Bits(v, 12, 9) << 2) | (Bits(v, 8, 7) << 6);

    private static int UimmSdsp(uint v) => (Bits(v, 12, 10) << 3) | (Bits(v, 9, 7) << 6);

    private static long JImm(uint v) => Sign((Bits(v, 12, 12) << 11) | (Bits(v, 11, 11) << 4) | (Bits(v, 10, 9) << 8) | (Bits(v, 8, 8) << 10)
        | (Bits(v, 7, 7) << 6) | (Bits(v, 6, 6) << 7) | (Bits(v, 5, 3) << 1) | (Bits(v, 2, 2) << 5), 12);
}
