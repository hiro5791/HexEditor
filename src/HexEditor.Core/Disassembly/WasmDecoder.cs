using System.Buffers.Binary;
using System.Globalization;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// WebAssembly の命令列の逆アセンブラ (本体の自前の実装。Capstone 4 にないため)。関数本体の命令 (MVP と、符号拡張・飽和変換・
/// 一括メモリ操作・参照型) を解釈する。表記はテキスト形式のニーモニック (<c>i32.const 42</c>、<c>end</c>)。
/// 分岐 (<c>br</c> など) の先はブロックの深さなので、分岐先のアドレスは求めない。
/// </summary>
internal sealed class WasmDecoder : IInstructionDecoder
{
    private static readonly Dictionary<byte, string> Simple = BuildSimple();

    private readonly DisasmFormat _format;

    public WasmDecoder(DisasmArchitecture architecture, DisasmFormat format)
    {
        Architecture = architecture;
        _format = format;
    }

    public DisasmArchitecture Architecture { get; }

    public void Dispose()
    {
    }

    private static Dictionary<byte, string> BuildSimple()
    {
        var d = new Dictionary<byte, string>
        {
            [0x00] = "unreachable", [0x01] = "nop", [0x05] = "else", [0x0B] = "end", [0x0F] = "return", [0x1A] = "drop", [0x1B] = "select",
            [0xD1] = "ref.is_null",
        };
        string[] numeric =
        [
            "i32.eqz", "i32.eq", "i32.ne", "i32.lt_s", "i32.lt_u", "i32.gt_s", "i32.gt_u", "i32.le_s", "i32.le_u", "i32.ge_s", "i32.ge_u",
            "i64.eqz", "i64.eq", "i64.ne", "i64.lt_s", "i64.lt_u", "i64.gt_s", "i64.gt_u", "i64.le_s", "i64.le_u", "i64.ge_s", "i64.ge_u",
            "f32.eq", "f32.ne", "f32.lt", "f32.gt", "f32.le", "f32.ge", "f64.eq", "f64.ne", "f64.lt", "f64.gt", "f64.le", "f64.ge",
            "i32.clz", "i32.ctz", "i32.popcnt", "i32.add", "i32.sub", "i32.mul", "i32.div_s", "i32.div_u", "i32.rem_s", "i32.rem_u",
            "i32.and", "i32.or", "i32.xor", "i32.shl", "i32.shr_s", "i32.shr_u", "i32.rotl", "i32.rotr",
            "i64.clz", "i64.ctz", "i64.popcnt", "i64.add", "i64.sub", "i64.mul", "i64.div_s", "i64.div_u", "i64.rem_s", "i64.rem_u",
            "i64.and", "i64.or", "i64.xor", "i64.shl", "i64.shr_s", "i64.shr_u", "i64.rotl", "i64.rotr",
            "f32.abs", "f32.neg", "f32.ceil", "f32.floor", "f32.trunc", "f32.nearest", "f32.sqrt", "f32.add", "f32.sub", "f32.mul", "f32.div",
            "f32.min", "f32.max", "f32.copysign",
            "f64.abs", "f64.neg", "f64.ceil", "f64.floor", "f64.trunc", "f64.nearest", "f64.sqrt", "f64.add", "f64.sub", "f64.mul", "f64.div",
            "f64.min", "f64.max", "f64.copysign",
            "i32.wrap_i64", "i32.trunc_f32_s", "i32.trunc_f32_u", "i32.trunc_f64_s", "i32.trunc_f64_u", "i64.extend_i32_s", "i64.extend_i32_u",
            "i64.trunc_f32_s", "i64.trunc_f32_u", "i64.trunc_f64_s", "i64.trunc_f64_u", "f32.convert_i32_s", "f32.convert_i32_u",
            "f32.convert_i64_s", "f32.convert_i64_u", "f32.demote_f64", "f64.convert_i32_s", "f64.convert_i32_u", "f64.convert_i64_s",
            "f64.convert_i64_u", "f64.promote_f32", "i32.reinterpret_f32", "i64.reinterpret_f64", "f32.reinterpret_i32", "f64.reinterpret_i64",
            "i32.extend8_s", "i32.extend16_s", "i64.extend8_s", "i64.extend16_s", "i64.extend32_s",
        ];
        for (int i = 0; i < numeric.Length; i++)
        {
            d[(byte)(0x45 + i)] = numeric[i];
        }

        return d;
    }

    private static readonly string[] Memory =
    [
        "i32.load", "i64.load", "f32.load", "f64.load", "i32.load8_s", "i32.load8_u", "i32.load16_s", "i32.load16_u", "i64.load8_s",
        "i64.load8_u", "i64.load16_s", "i64.load16_u", "i64.load32_s", "i64.load32_u", "i32.store", "i64.store", "f32.store", "f64.store",
        "i32.store8", "i32.store16", "i64.store8", "i64.store16", "i64.store32",
    ];

    private static readonly string[] Prefixed =
    [
        "i32.trunc_sat_f32_s", "i32.trunc_sat_f32_u", "i32.trunc_sat_f64_s", "i32.trunc_sat_f64_u", "i64.trunc_sat_f32_s",
        "i64.trunc_sat_f32_u", "i64.trunc_sat_f64_s", "i64.trunc_sat_f64_u", "memory.init", "data.drop", "memory.copy", "memory.fill",
        "table.init", "elem.drop", "table.copy", "table.grow", "table.size", "table.fill",
    ];

    /// <summary>符号なしの LEB128 を読む。読めなければ false。</summary>
    private static bool Uleb(ReadOnlySpan<byte> s, ref int at, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (at >= s.Length)
            {
                return false;
            }

            byte b = s[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Sleb(ReadOnlySpan<byte> s, ref int at, int bits, out long value)
    {
        value = 0;
        int shift = 0;
        byte b;
        do
        {
            if (at >= s.Length || shift >= bits + 7)
            {
                return false;
            }

            b = s[at++];
            value |= (long)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        if (shift < 64 && (b & 0x40) != 0)
        {
            value |= -1L << shift;
        }

        return true;
    }

    private static string D(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string D(ulong v) => v.ToString(CultureInfo.InvariantCulture);

    private DisasmInstruction Make(ulong address, int length, string mnemonic, string operands = "", FlowKind flow = FlowKind.Normal,
        bool indirect = false) =>
        new(address, length, _format.UppercaseMnemonic ? mnemonic.ToUpperInvariant() : mnemonic, operands, flow, null, indirect);

    /// <summary>ブロックの型 (空 0x40、値の型、型の添字)。</summary>
    private static bool BlockType(ReadOnlySpan<byte> s, ref int at, out string text)
    {
        text = string.Empty;
        if (at >= s.Length)
        {
            return false;
        }

        string? value = ValueType(s[at]);
        if (s[at] == 0x40)
        {
            at++;
            return true;
        }

        if (value is not null)
        {
            at++;
            text = "(result " + value + ")";
            return true;
        }

        if (!Sleb(s, ref at, 33, out long index))
        {
            return false;
        }

        text = "(type " + D(index) + ")";
        return true;
    }

    private static string? ValueType(byte b) => b switch
    {
        0x7F => "i32", 0x7E => "i64", 0x7D => "f32", 0x7C => "f64", 0x7B => "v128", 0x70 => "funcref", 0x6F => "externref", _ => null,
    };

    public DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false)
    {
        if (code.IsEmpty)
        {
            return null;
        }

        byte op = code[0];
        int at = 1;
        if (Simple.TryGetValue(op, out string? simple))
        {
            FlowKind flow = op switch { 0x0F => FlowKind.Return, 0x00 => FlowKind.Interrupt, _ => FlowKind.Normal };
            return Make(address, 1, simple, flow: flow);
        }

        switch (op)
        {
            case 0x02 or 0x03 or 0x04:
                return BlockType(code, ref at, out string bt) ? Make(address, at, op switch { 0x02 => "block", 0x03 => "loop", _ => "if" }, bt) : null;
            case 0x0C or 0x0D:
                return Uleb(code, ref at, out ulong depth)
                    ? Make(address, at, op == 0x0C ? "br" : "br_if", D(depth), op == 0x0C ? FlowKind.Jump : FlowKind.ConditionalJump, true)
                    : null;
            case 0x0E:
            {
                if (!Uleb(code, ref at, out ulong count) || count > 4096)
                {
                    return null;
                }

                var labels = new List<string>();
                for (ulong i = 0; i <= count; i++)
                {
                    if (!Uleb(code, ref at, out ulong l))
                    {
                        return null;
                    }

                    labels.Add(D(l));
                }

                return Make(address, at, "br_table", string.Join(" ", labels), FlowKind.Jump, true);
            }

            case 0x10:
                return Uleb(code, ref at, out ulong f) ? Make(address, at, "call", D(f), FlowKind.Call, true) : null;
            case 0x11:
                return Uleb(code, ref at, out ulong type) && Uleb(code, ref at, out ulong table)
                    ? Make(address, at, "call_indirect", (table == 0 ? string.Empty : D(table) + " ") + "(type " + D(type) + ")", FlowKind.Call, true)
                    : null;
            case 0x1C:
            {
                if (!Uleb(code, ref at, out ulong n) || n > 16 || at + (int)n > code.Length)
                {
                    return null;
                }

                var types = new List<string>();
                for (int i = 0; i < (int)n; i++)
                {
                    types.Add(ValueType(code[at++]) ?? "?");
                }

                return Make(address, at, "select", "(result " + string.Join(" ", types) + ")");
            }

            case >= 0x20 and <= 0x26:
            {
                string name = op switch
                {
                    0x20 => "local.get", 0x21 => "local.set", 0x22 => "local.tee", 0x23 => "global.get", 0x24 => "global.set", 0x25 => "table.get",
                    _ => "table.set",
                };
                return Uleb(code, ref at, out ulong index) ? Make(address, at, name, D(index)) : null;
            }

            case >= 0x28 and <= 0x3E:
            {
                if (!Uleb(code, ref at, out ulong align) || !Uleb(code, ref at, out ulong offset))
                {
                    return null;
                }

                string operands = (offset != 0 ? "offset=" + D(offset) : string.Empty)
                    + (align < 64 ? (offset != 0 ? " " : string.Empty) + "align=" + D(1UL << (int)align) : string.Empty);
                return Make(address, at, Memory[op - 0x28], operands);
            }

            case 0x3F or 0x40:
                return Uleb(code, ref at, out _) ? Make(address, at, op == 0x3F ? "memory.size" : "memory.grow") : null;
            case 0x41:
                return Sleb(code, ref at, 32, out long i32) ? Make(address, at, "i32.const", D((int)i32)) : null;
            case 0x42:
                return Sleb(code, ref at, 64, out long i64) ? Make(address, at, "i64.const", D(i64)) : null;
            case 0x43:
                return code.Length >= 5
                    ? Make(address, 5, "f32.const", BinaryPrimitives.ReadSingleLittleEndian(code[1..]).ToString("R", CultureInfo.InvariantCulture))
                    : null;
            case 0x44:
                return code.Length >= 9
                    ? Make(address, 9, "f64.const", BinaryPrimitives.ReadDoubleLittleEndian(code[1..]).ToString("R", CultureInfo.InvariantCulture))
                    : null;
            case 0xD0:
                return code.Length >= 2 && ValueType(code[1]) is { } rt ? Make(address, 2, "ref.null", rt == "funcref" ? "func" : "extern") : null;
            case 0xD2:
                return Uleb(code, ref at, out ulong func) ? Make(address, at, "ref.func", D(func)) : null;
            case 0xFC:
            {
                if (!Uleb(code, ref at, out ulong sub) || sub >= (ulong)Prefixed.Length)
                {
                    return null;
                }

                var args = new List<string>();
                int count = sub switch { 8 or 12 or 14 => 2, 9 or 11 or 13 or 15 or 16 or 17 => 1, 10 => 2, _ => 0 };
                for (int i = 0; i < count; i++)
                {
                    if (!Uleb(code, ref at, out ulong a))
                    {
                        return null;
                    }

                    // memory.init の 2 つ目と memory.copy / memory.fill のメモリの添字 (0) は表示しない。
                    if (!(sub is 8 && i == 1 || sub is 10 || sub is 11))
                    {
                        args.Add(D(a));
                    }
                }

                return Make(address, at, Prefixed[(int)sub], string.Join(" ", args));
            }

            default:
                return null;
        }
    }
}
