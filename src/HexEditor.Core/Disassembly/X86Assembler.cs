using System.Globalization;
using System.Reflection;
using Iced.Intel;

namespace HexEditor.Core.Disassembly;

/// <summary>アセンブルの誤り (ANA-29 の「エラー」: 構文エラー、そのアーキテクチャにない命令)。</summary>
public sealed class AssembleException(string reason, string detail) : Exception(reason + ": " + detail)
{
    /// <summary>理由の種類 (リソース <c>Disasm_AssembleError_&lt;Reason&gt;</c>): Syntax、UnknownMnemonic、Operands、Encode。</summary>
    public string Reason { get; } = reason;

    public string Detail { get; } = detail;
}

/// <summary>アセンブルの結果 (ANA-29 の仕様 2)。<see cref="Instructions"/> は生成したバイト列を逆アセンブルし直したもの。</summary>
public sealed record AssembleResult(byte[] Bytes, IReadOnlyList<DisasmInstruction> Instructions);

/// <summary>
/// x86 (16 / 32 bit) と x64 のインプレースアセンブル (ANA-29)。Intel 構文の文字列 (<c>;</c> 区切りで複数) を読み、Iced のアセンブラ
/// (<see cref="Assembler"/>) でエンコードする。符号化の選び方は Iced に従う (仕様 7)。分岐の即値は分岐先の絶対アドレス。
/// RIP 相対 (<c>[rip + 0x10]</c>) は、次の命令からの相対として扱う。
/// </summary>
public static class X86Assembler
{
    private static readonly ILookup<string, MethodInfo> Methods = typeof(Assembler).GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.ReturnType == typeof(void) && m.DeclaringType == typeof(Assembler))
        .ToLookup(m => m.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, FieldInfo> Registers = typeof(AssemblerRegisters).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => !f.Name.StartsWith("__", StringComparison.Ordinal))
        .ToDictionary(f => f.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, FieldInfo> Sizes = typeof(AssemblerRegisters).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.Name.StartsWith("__", StringComparison.Ordinal) && f.Name.EndsWith("_ptr", StringComparison.Ordinal))
        .ToDictionary(f => f.Name[2..^4], StringComparer.Ordinal);

    private static readonly HashSet<string> Prefixes = new(StringComparer.Ordinal) { "lock", "rep", "repe", "repz", "repne", "repnz", "xacquire", "xrelease", "bnd", "notrack" };

    private abstract record Operand;

    private sealed record RegisterOperand(string Name, object Value) : Operand;

    private sealed record ImmediateOperand(long Value) : Operand;

    private sealed record MemoryOperand(string? Size, string? Segment, string? Base, string? Index, int Scale, long Displacement, bool RipRelative) : Operand;

    /// <summary>
    /// <paramref name="text"/> を <paramref name="address"/> (行の仮想アドレス) に置く命令としてアセンブルする。
    /// <paramref name="bitness"/> は 16 / 32 / 64。
    /// </summary>
    public static AssembleResult Assemble(string text, ulong address, int bitness, DisasmFormat? format = null)
    {
        format ??= DisasmFormat.Default;
        string[] statements = [.. text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        if (statements.Length == 0)
        {
            throw new AssembleException("Syntax", text);
        }

        var parsed = statements.Select(s => Parse(s, bitness)).ToList();

        // RIP 相対は、命令の長さが決まるまで繰り返す (分岐先と同じく絶対アドレスにしてから符号化する)。
        int[] lengths = new int[parsed.Count];
        byte[] bytes = [];
        for (int round = 0; round < 4; round++)
        {
            var assembler = new Assembler(bitness);
            for (int i = 0; i < parsed.Count; i++)
            {
                Emit(assembler, parsed[i], bitness);
            }

            ulong ip = address;
            var instructions = assembler.Instructions.ToList();
            if (instructions.Count != parsed.Count)
            {
                throw new AssembleException("Encode", text);
            }

            for (int i = 0; i < parsed.Count; i++)
            {
                if (parsed[i].Operands.OfType<MemoryOperand>().FirstOrDefault(m => m.RipRelative) is { } rip)
                {
                    Instruction instr = instructions[i];
                    instr.MemoryBase = bitness == 64 ? Register.RIP : Register.EIP;
                    instr.MemoryIndex = Register.None;
                    instr.MemoryDisplacement64 = ip + (ulong)lengths[i] + (ulong)rip.Displacement;
                    instr.MemoryDisplSize = 8;
                    instructions[i] = instr;
                }

                ip += (ulong)lengths[i];
            }

            var writer = new ByteWriter();
            var block = new InstructionBlock(writer, instructions, address);
            if (!BlockEncoder.TryEncode(bitness, block, out string? error, out BlockEncoderResult result, BlockEncoderOptions.ReturnNewInstructionOffsets))
            {
                throw new AssembleException("Encode", error ?? text);
            }

            bytes = writer.ToArray();
            int[] newLengths = new int[parsed.Count];
            for (int i = 0; i < parsed.Count; i++)
            {
                uint start = result.NewInstructionOffsets[i];
                uint end = i + 1 < parsed.Count ? result.NewInstructionOffsets[i + 1] : (uint)bytes.Length;
                newLengths[i] = (int)(end - start);
            }

            bool stable = newLengths.SequenceEqual(lengths);
            lengths = newLengths;
            if (stable || !parsed.Any(p => p.Operands.OfType<MemoryOperand>().Any(m => m.RipRelative)))
            {
                break;
            }
        }

        string arch = bitness switch { 16 => DisasmArchitectures.X86_16, 32 => DisasmArchitectures.X86_32, _ => DisasmArchitectures.X86_64 };
        using IInstructionDecoder decoder = Disassembler.Create(arch, format);
        return new AssembleResult(bytes, Disassembler.Sweep(decoder, bytes, address, format));
    }

    private sealed class ByteWriter : CodeWriter
    {
        private readonly List<byte> _bytes = [];

        public override void WriteByte(byte value) => _bytes.Add(value);

        public byte[] ToArray() => [.. _bytes];
    }

    private sealed record Statement(List<string> Prefixes, string Mnemonic, List<Operand> Operands, string Text);

    // ---- 文字列の解釈 ----

    private static Statement Parse(string text, int bitness)
    {
        string s = text.Trim().ToLowerInvariant();
        var prefixes = new List<string>();
        while (true)
        {
            int space = s.IndexOf(' ', StringComparison.Ordinal);
            string first = space < 0 ? s : s[..space];
            if (!Prefixes.Contains(first) || space < 0)
            {
                break;
            }

            prefixes.Add(first);
            s = s[(space + 1)..].TrimStart();
        }

        int split = s.IndexOfAny([' ', '\t']);
        string mnemonic = split < 0 ? s : s[..split];
        string rest = split < 0 ? string.Empty : s[(split + 1)..].Trim();
        if (mnemonic.Length == 0 || !mnemonic.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
        {
            throw new AssembleException("Syntax", text);
        }

        var operands = new List<Operand>();
        foreach (string op in SplitOperands(rest, text))
        {
            operands.Add(ParseOperand(op, text, bitness));
        }

        return new Statement(prefixes, mnemonic, operands, text);
    }

    private static IEnumerable<string> SplitOperands(string rest, string text)
    {
        if (rest.Length == 0)
        {
            yield break;
        }

        int depth = 0;
        int start = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth < 0)
                    {
                        throw new AssembleException("Syntax", text);
                    }

                    break;
                case ',' when depth == 0:
                    yield return rest[start..i].Trim();
                    start = i + 1;
                    break;
            }
        }

        if (depth != 0)
        {
            throw new AssembleException("Syntax", text);
        }

        yield return rest[start..].Trim();
    }

    private static Operand ParseOperand(string op, string text, int bitness)
    {
        if (op.Length == 0)
        {
            throw new AssembleException("Syntax", text);
        }

        if (op.Contains('['))
        {
            return ParseMemory(op, text);
        }

        if (Registers.TryGetValue(op, out FieldInfo? field))
        {
            return new RegisterOperand(op, field.GetValue(null)!);
        }

        if (ParseNumber(op) is { } number)
        {
            return new ImmediateOperand(number);
        }

        _ = bitness;
        throw new AssembleException("Operands", op);
    }

    /// <summary>数 (10 進、<c>0x</c> の 16 進、<c>h</c> の接尾辞の 16 進、負の数)。</summary>
    private static long? ParseNumber(string text)
    {
        string t = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        bool negative = false;
        if (t.StartsWith('-'))
        {
            negative = true;
            t = t[1..];
        }
        else if (t.StartsWith('+'))
        {
            t = t[1..];
        }

        ulong value;
        if (t.StartsWith("0x", StringComparison.Ordinal) && ulong.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
        }
        else if (t.EndsWith('h') && t.Length > 1 && char.IsAsciiDigit(t[0])
            && ulong.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
        }
        else if (!ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        return negative ? -(long)value : (long)value;
    }

    private static MemoryOperand ParseMemory(string op, string text)
    {
        string? size = null;
        string s = op;
        int bracket = s.IndexOf('[', StringComparison.Ordinal);
        string before = s[..bracket].Trim();
        if (!s.EndsWith(']'))
        {
            throw new AssembleException("Syntax", text);
        }

        string? segment = null;
        if (before.Length > 0)
        {
            // 「dword ptr」「qword ptr fs:」など。
            string[] words = before.Replace(":", " : ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i] == "ptr" || words[i] == ":")
                {
                    continue;
                }

                if (Sizes.ContainsKey(words[i]))
                {
                    size = words[i];
                }
                else if (words[i] is "cs" or "ds" or "es" or "fs" or "gs" or "ss")
                {
                    segment = words[i];
                }
                else
                {
                    throw new AssembleException("Syntax", text);
                }
            }
        }

        string inner = s[(bracket + 1)..^1].Replace(" ", string.Empty, StringComparison.Ordinal);
        int colon = inner.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            segment = inner[..colon];
            inner = inner[(colon + 1)..];
        }

        string? baseReg = null;
        string? index = null;
        int scale = 1;
        long displacement = 0;
        bool rip = false;
        int i0 = 0;
        while (i0 < inner.Length)
        {
            char sign = '+';
            if (inner[i0] is '+' or '-')
            {
                sign = inner[i0];
                i0++;
            }

            int end = i0;
            while (end < inner.Length && inner[end] is not ('+' or '-'))
            {
                end++;
            }

            string term = inner[i0..end];
            i0 = end;
            if (term.Length == 0)
            {
                throw new AssembleException("Syntax", text);
            }

            if (term == "rip" || term == "eip")
            {
                rip = true;
                continue;
            }

            int star = term.IndexOf('*', StringComparison.Ordinal);
            if (star > 0)
            {
                string a = term[..star];
                string b = term[(star + 1)..];
                (string reg, string factor) = Registers.ContainsKey(a) ? (a, b) : (b, a);
                if (!Registers.ContainsKey(reg) || ParseNumber(factor) is not { } f || f is not (1 or 2 or 4 or 8) || sign == '-' || index is not null)
                {
                    throw new AssembleException("Syntax", text);
                }

                index = reg;
                scale = (int)f;
                continue;
            }

            if (Registers.ContainsKey(term))
            {
                if (sign == '-')
                {
                    throw new AssembleException("Syntax", text);
                }

                if (baseReg is null)
                {
                    baseReg = term;
                }
                else if (index is null)
                {
                    index = term;
                }
                else
                {
                    throw new AssembleException("Syntax", text);
                }

                continue;
            }

            if (ParseNumber(term) is not { } n)
            {
                throw new AssembleException("Syntax", text);
            }

            displacement += sign == '-' ? -n : n;
        }

        return new MemoryOperand(size, segment, baseReg, index, scale, displacement, rip);
    }

    // ---- Iced のアセンブラの呼び出し ----

    private static void Emit(Assembler assembler, Statement statement, int bitness)
    {
        Assembler target = assembler;
        foreach (string prefix in statement.Prefixes)
        {
            PropertyInfo? p = typeof(Assembler).GetProperty(prefix, BindingFlags.Public | BindingFlags.Instance);
            target = p?.GetValue(target) as Assembler ?? throw new AssembleException("Syntax", statement.Text);
        }

        string name = statement.Mnemonic;
        var candidates = Methods[name].Where(m => m.GetParameters().Length == statement.Operands.Count).ToList();
        if (!Methods.Contains(name))
        {
            throw new AssembleException("UnknownMnemonic", name);
        }

        Exception? last = null;
        foreach (MethodInfo method in candidates.OrderBy(Rank))
        {
            ParameterInfo[] parameters = method.GetParameters();
            object?[] args = new object?[parameters.Length];
            bool ok = true;
            for (int i = 0; i < parameters.Length && ok; i++)
            {
                ok = Convert(statement.Operands[i], parameters[i].ParameterType, bitness, out args[i]);
            }

            if (!ok)
            {
                continue;
            }

            int before = target.Instructions.Count;
            try
            {
                method.Invoke(target, args);
                if (assembler.Instructions.Count > before || target.Instructions.Count > before)
                {
                    return;
                }
            }
            catch (TargetInvocationException ex)
            {
                last = ex.InnerException;
            }
        }

        throw new AssembleException("Operands", last?.Message ?? statement.Text);
    }

    /// <summary>候補の順 (即値は小さい型から。分岐は絶対アドレスの ulong を先に)。</summary>
    private static int Rank(MethodInfo method)
    {
        int rank = 0;
        foreach (ParameterInfo p in method.GetParameters())
        {
            rank = rank * 10 + (p.ParameterType == typeof(ulong) ? 0
                : p.ParameterType == typeof(sbyte) || p.ParameterType == typeof(byte) ? 1
                : p.ParameterType == typeof(short) || p.ParameterType == typeof(ushort) ? 2
                : p.ParameterType == typeof(int) || p.ParameterType == typeof(uint) ? 3
                : p.ParameterType == typeof(long) ? 4
                : 0);
        }

        return rank;
    }

    private static bool Convert(Operand operand, Type type, int bitness, out object? value)
    {
        value = null;
        switch (operand)
        {
            case RegisterOperand r:
                if (r.Value.GetType() == type)
                {
                    value = r.Value;
                    return true;
                }

                return false;
            case ImmediateOperand imm:
                return ConvertImmediate(imm.Value, type, out value);
            case MemoryOperand m when type == typeof(AssemblerMemoryOperand):
                value = Memory(m, bitness);
                return value is not null;
            default:
                return false;
        }
    }

    private static bool ConvertImmediate(long v, Type type, out object? value)
    {
        value = null;
        if (type == typeof(ulong))
        {
            value = (ulong)v;
            return true;
        }

        if (type == typeof(long))
        {
            value = v;
            return true;
        }

        if (type == typeof(int) && v is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)v;
            return true;
        }

        if (type == typeof(uint) && v is >= 0 and <= uint.MaxValue)
        {
            value = (uint)v;
            return true;
        }

        if (type == typeof(short) && v is >= short.MinValue and <= short.MaxValue)
        {
            value = (short)v;
            return true;
        }

        if (type == typeof(ushort) && v is >= 0 and <= ushort.MaxValue)
        {
            value = (ushort)v;
            return true;
        }

        if (type == typeof(sbyte) && v is >= sbyte.MinValue and <= sbyte.MaxValue)
        {
            value = (sbyte)v;
            return true;
        }

        if (type == typeof(byte) && v is >= 0 and <= byte.MaxValue)
        {
            value = (byte)v;
            return true;
        }

        return false;
    }

    /// <summary>メモリのオペランドを Iced の演算子で組み立てる (サイズの指定は __dword_ptr などの添字、セグメントは .fs などのプロパティ)。</summary>
    private static AssemblerMemoryOperand? Memory(MemoryOperand m, int bitness)
    {
        object factory = m.Size is { } size && Sizes.TryGetValue(size, out FieldInfo? f) ? f.GetValue(null)! : typeof(AssemblerRegisters).GetField("__")!.GetValue(null)!;
        if (m.Segment is { } segment)
        {
            factory = factory.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)?.GetValue(factory)
                ?? throw new AssembleException("Operands", segment);
        }

        dynamic? expr = null;
        if (m.RipRelative)
        {
            // 仮の形 (後で RIP 相対に書き換える)。
            expr = bitness == 64 ? (dynamic)AssemblerRegisters.rax + 0L : AssemblerRegisters.eax + 0L;
        }
        else
        {
            if (m.Base is { } b)
            {
                expr = Registers[b].GetValue(null);
            }

            if (m.Index is { } i)
            {
                dynamic index = Registers[i].GetValue(null)!;
                dynamic scaled = index * m.Scale;
                expr = expr is null ? scaled : expr + scaled;
            }

            if (expr is not null && m.Displacement != 0)
            {
                expr = expr + m.Displacement;
            }
        }

        dynamic d = factory;
        try
        {
            return expr is null ? d[(ulong)m.Displacement] : d[expr];
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }
}
