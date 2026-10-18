using System.Buffers.Binary;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// BPF (従来の BPF と eBPF) の逆アセンブラ (本体の自前の実装。Capstone 4 にないため)。表記は Capstone 5 に近い形
/// (eBPF: <c>mov64 r0, 0x1</c>、<c>ldxw r0, [r1+0x4]</c>、<c>jeq r1, 0x0, +0x3</c>、<c>exit</c>。従来の BPF: <c>ldh [0xc]</c>、<c>ret 0xffff</c>)。
/// 分岐先は命令の位置から計算する (1 命令 8 バイト)。
/// </summary>
internal sealed class BpfDecoder : IInstructionDecoder
{
    private readonly DisasmFormat _format;
    private readonly bool _extended;
    private readonly bool _big;

    public BpfDecoder(DisasmArchitecture architecture, DisasmFormat format, bool bigEndian)
    {
        Architecture = architecture;
        _format = format;
        _extended = architecture.Id == DisasmArchitectures.Ebpf;
        _big = bigEndian;
    }

    public DisasmArchitecture Architecture { get; }

    public void Dispose()
    {
    }

    private string H(long v) => v < 0 ? "-" + _format.Hex((ulong)(-v)) : _format.Hex((ulong)v);

    private string Signed(long v) => (v < 0 ? "-" : "+") + _format.Hex((ulong)Math.Abs(v));

    private DisasmInstruction Make(ulong address, int length, string mnemonic, string operands, FlowKind flow = FlowKind.Normal, ulong? target = null,
        bool indirect = false) =>
        new(address, length, _format.UppercaseMnemonic ? mnemonic.ToUpperInvariant() : mnemonic,
            _format.UppercaseMnemonic ? operands.ToUpperInvariant() : operands, flow, target, indirect);

    public DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false)
    {
        if (code.Length < 8)
        {
            return null;
        }

        return _extended ? Extended(code, address) : Classic(code, address);
    }

    private static readonly string[] AluNames = ["add", "sub", "mul", "div", "or", "and", "lsh", "rsh", "neg", "mod", "xor", "mov", "arsh", "end"];

    private static readonly string[] JumpNames =
        ["ja", "jeq", "jgt", "jge", "jset", "jne", "jsgt", "jsge", "call", "exit", "jlt", "jle", "jslt", "jsle"];

    private static readonly string[] Sizes = ["w", "h", "b", "dw"];

    private DisasmInstruction? Extended(ReadOnlySpan<byte> code, ulong address)
    {
        byte op = code[0];
        int dst = _big ? code[1] >> 4 : code[1] & 0xF;
        int src = _big ? code[1] & 0xF : code[1] >> 4;
        short off = _big ? BinaryPrimitives.ReadInt16BigEndian(code[2..]) : BinaryPrimitives.ReadInt16LittleEndian(code[2..]);
        int imm = _big ? BinaryPrimitives.ReadInt32BigEndian(code[4..]) : BinaryPrimitives.ReadInt32LittleEndian(code[4..]);
        if (dst > 10 || src > 10)
        {
            return null;
        }

        string rd = "r" + dst;
        string rs = "r" + src;
        int cls = op & 7;
        switch (cls)
        {
            case 4 or 7:
            {
                int code4 = op >> 4;
                if (code4 >= AluNames.Length)
                {
                    return null;
                }

                string suffix = cls == 7 ? "64" : string.Empty;
                bool x = (op & 8) != 0;
                if (code4 == 13)
                {
                    // バイト順の変換 (le / be)。
                    return imm is 16 or 32 or 64 ? Make(address, 8, (x ? "be" : "le") + imm, rd) : null;
                }

                if (code4 == 8)
                {
                    return Make(address, 8, "neg" + suffix, rd);
                }

                return Make(address, 8, AluNames[code4] + suffix, rd + ", " + (x ? rs : H(imm)));
            }

            case 5 or 6:
            {
                int code4 = op >> 4;
                if (code4 >= JumpNames.Length)
                {
                    return null;
                }

                string name = JumpNames[code4] + (cls == 6 && code4 is not (0 or 8 or 9) ? "32" : string.Empty);
                ulong target = address + 8 + (ulong)((long)off * 8);
                switch (code4)
                {
                    case 0:
                        return Make(address, 8, name, Signed(off), FlowKind.Jump, target);
                    case 8:
                        return Make(address, 8, "call", H(imm), FlowKind.Call, null, true);
                    case 9:
                        return Make(address, 8, "exit", string.Empty, FlowKind.Return);
                }

                bool x = (op & 8) != 0;
                return Make(address, 8, name, rd + ", " + (x ? rs : H(imm)) + ", " + Signed(off), FlowKind.ConditionalJump, target);
            }

            case 1:
                return (op & 0xE0) == 0x60 ? Make(address, 8, "ldx" + Sizes[(op >> 3) & 3], $"{rd}, [{rs}{Signed(off)}]") : null;
            case 3:
                return (op & 0xE0) switch
                {
                    0x60 => Make(address, 8, "stx" + Sizes[(op >> 3) & 3], $"[{rd}{Signed(off)}], {rs}"),
                    0xC0 => Make(address, 8, "xadd" + Sizes[(op >> 3) & 3], $"[{rd}{Signed(off)}], {rs}"),
                    _ => null,
                };
            case 2:
                return (op & 0xE0) == 0x60 ? Make(address, 8, "st" + Sizes[(op >> 3) & 3], $"[{rd}{Signed(off)}], {H(imm)}") : null;
            default:
            {
                // LD: lddw (16 バイト)、ldabs / ldind。
                if (op == 0x18)
                {
                    if (code.Length < 16)
                    {
                        return null;
                    }

                    uint high = _big ? BinaryPrimitives.ReadUInt32BigEndian(code[12..]) : BinaryPrimitives.ReadUInt32LittleEndian(code[12..]);
                    ulong value = ((ulong)high << 32) | (uint)imm;
                    return Make(address, 16, "lddw", rd + ", " + _format.Hex(value));
                }

                string size = Sizes[(op >> 3) & 3];
                return (op & 0xE0) switch
                {
                    0x20 => Make(address, 8, "ldabs" + size, "[" + H(imm) + "]"),
                    0x40 => Make(address, 8, "ldind" + size, $"[{rs}{Signed(imm)}]"),
                    _ => null,
                };
            }
        }
    }

    private DisasmInstruction? Classic(ReadOnlySpan<byte> code, ulong address)
    {
        ushort op = _big ? BinaryPrimitives.ReadUInt16BigEndian(code) : BinaryPrimitives.ReadUInt16LittleEndian(code);
        byte jt = code[2];
        byte jf = code[3];
        uint k = _big ? BinaryPrimitives.ReadUInt32BigEndian(code[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(code[4..]);
        int cls = op & 7;
        string size = ((op >> 3) & 3) switch { 0 => "", 1 => "h", 2 => "b", _ => "?" };
        int mode = op & 0xE0;
        string K = _format.Hex(k);
        switch (cls)
        {
            case 0:
            case 1:
            {
                string name = cls == 0 ? "ld" : "ldx";
                if (size == "?")
                {
                    return null;
                }

                return mode switch
                {
                    0x00 => Make(address, 8, name, K),
                    0x20 when cls == 0 => Make(address, 8, name + size, "[" + K + "]"),
                    0x40 when cls == 0 => Make(address, 8, name + size, "[x+" + K + "]"),
                    0x60 => Make(address, 8, name, "m[" + k + "]"),
                    0x80 => Make(address, 8, name, "#len"),
                    0xA0 when cls == 1 => Make(address, 8, "ldxb", "4*([" + K + "]&0xf)"),
                    _ => null,
                };
            }

            case 2:
                return Make(address, 8, "st", "m[" + k + "]");
            case 3:
                return Make(address, 8, "stx", "m[" + k + "]");
            case 4:
            {
                int code4 = op >> 4;
                if (code4 >= 11)
                {
                    return null;
                }

                if (code4 == 8)
                {
                    return Make(address, 8, "neg", string.Empty);
                }

                return Make(address, 8, AluNames[code4], (op & 8) != 0 ? "x" : K);
            }

            case 5:
            {
                int code4 = op >> 4;
                ulong next = address + 8;
                if (code4 == 0)
                {
                    ulong target = next + (ulong)k * 8;
                    return Make(address, 8, "ja", H((long)target), FlowKind.Jump, target);
                }

                string? name = code4 switch { 1 => "jeq", 2 => "jgt", 3 => "jge", 4 => "jset", _ => null };
                if (name is null)
                {
                    return null;
                }

                ulong t = next + (ulong)jt * 8;
                ulong f = next + (ulong)jf * 8;
                return Make(address, 8, name, ((op & 8) != 0 ? "x" : K) + ", " + H((long)t) + ", " + H((long)f), FlowKind.ConditionalJump, t);
            }

            case 6:
                return Make(address, 8, "ret", (op & 0x18) switch { 0x10 => "a", 0x08 => "x", _ => K }, FlowKind.Return);
            default:
                return (op & 0xF8) switch
                {
                    0x00 => Make(address, 8, "tax", string.Empty),
                    0x80 => Make(address, 8, "txa", string.Empty),
                    _ => null,
                };
        }
    }
}
