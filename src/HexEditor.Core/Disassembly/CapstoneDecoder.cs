using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// Capstone 4 のネイティブライブラリ (capstone.dll) の呼び出し。必要な関数だけを自前で宣言する (cs_insn・cs_detail の配置は 4.0 のもの)。
/// </summary>
internal static unsafe partial class CapstoneNative
{
    private const string Library = "capstone";

    public const int ArchArm = 0;
    public const int ArchArm64 = 1;
    public const int ArchMips = 2;
    public const int ArchPpc = 4;
    public const int ArchSparc = 5;
    public const int ArchSysZ = 6;
    public const int ArchXCore = 7;
    public const int ArchM68K = 8;
    public const int ArchTms320C64x = 9;
    public const int ArchM680X = 10;
    public const int ArchEvm = 11;

    public const uint ModeBigEndian = 1u << 31;

    public const int OptSyntax = 1;
    public const int OptDetail = 2;
    public const int OptOn = 3;
    public const int SyntaxNoRegName = 3;

    public const int GroupJump = 1;
    public const int GroupCall = 2;
    public const int GroupRet = 3;
    public const int GroupInt = 4;
    public const int GroupIret = 5;

    // cs_insn (4.0): id 4、(詰め物 4)、address 8、size 2、bytes 16、mnemonic 32、op_str 160、(詰め物 6)、detail 8。
    public const int InsnSize = 16;
    public const int InsnMnemonic = 34;
    public const int InsnOpStr = 66;
    public const int InsnDetail = 232;

    // cs_detail (4.0): regs_read[12] (u16)、regs_read_count、regs_write[20]、regs_write_count、groups[8]、groups_count。
    public const int DetailRegsRead = 0;
    public const int DetailRegsReadCount = 24;
    public const int DetailRegsWrite = 26;
    public const int DetailRegsWriteCount = 66;
    public const int DetailGroups = 67;
    public const int DetailGroupsCount = 75;

    [LibraryImport(Library, EntryPoint = "cs_version")]
    public static partial uint Version(out int major, out int minor);

    [LibraryImport(Library, EntryPoint = "cs_open")]
    public static partial int Open(int arch, uint mode, out nint handle);

    [LibraryImport(Library, EntryPoint = "cs_close")]
    public static partial int Close(ref nint handle);

    [LibraryImport(Library, EntryPoint = "cs_option")]
    public static partial int Option(nint handle, int type, nuint value);

    [LibraryImport(Library, EntryPoint = "cs_malloc")]
    public static partial nint Malloc(nint handle);

    [LibraryImport(Library, EntryPoint = "cs_free")]
    public static partial void Free(nint insn, nuint count);

    [LibraryImport(Library, EntryPoint = "cs_disasm_iter")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool DisasmIter(nint handle, ref byte* code, ref nuint size, ref ulong address, nint insn);

    [LibraryImport(Library, EntryPoint = "cs_reg_name")]
    public static partial nint RegName(nint handle, uint reg);

    [LibraryImport(Library, EntryPoint = "cs_group_name")]
    public static partial nint GroupName(nint handle, uint group);

    private static readonly Lazy<string?> LoadError = new(() =>
    {
        try
        {
            Version(out int major, out _);
            return major == 4 ? null : $"capstone {major}.x is not supported";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return ex.Message;
        }
    });

    /// <summary>ネイティブライブラリを読み込めないときの理由 (読み込めれば null)。</summary>
    public static string? UnavailableReason => LoadError.Value;
}

/// <summary>
/// Capstone 4 による逆アセンブラ (ARM、ARM64、MIPS、PowerPC、SPARC、SystemZ、XCore、68K、TMS320C64x、M680X、EVM)。
/// 分岐の種類は命令グループ (jump / call / ret) とニーモニックから、即値の分岐先はオペランドの文字列の最後の即値から求める。
/// </summary>
internal sealed unsafe partial class CapstoneDecoder : IInstructionDecoder
{
    private readonly DisasmFormat _format;
    private nint _handle;
    private nint _insn;

    public CapstoneDecoder(DisasmArchitecture architecture, DisasmFormat format, bool bigEndian)
    {
        Architecture = architecture;
        _format = format;
        if (CapstoneNative.UnavailableReason is { } reason)
        {
            throw new DisassemblerUnavailableException(reason);
        }

        (int arch, uint mode) = ModeOf(architecture.Id);
        if (bigEndian && architecture.Id is not (DisasmArchitectures.Evm))
        {
            mode |= CapstoneNative.ModeBigEndian;
        }

        int err = CapstoneNative.Open(arch, mode, out _handle);
        if (err != 0)
        {
            throw new DisassemblerUnavailableException($"cs_open failed ({err})");
        }

        CapstoneNative.Option(_handle, CapstoneNative.OptDetail, CapstoneNative.OptOn);
        if (architecture.IsArm32 && !format.ArmRegisterAlias)
        {
            CapstoneNative.Option(_handle, CapstoneNative.OptSyntax, CapstoneNative.SyntaxNoRegName);
        }

        _insn = CapstoneNative.Malloc(_handle);
    }

    public DisasmArchitecture Architecture { get; }

    private static (int Arch, uint Mode) ModeOf(string id) => id switch
    {
        DisasmArchitectures.Arm => (CapstoneNative.ArchArm, 0),
        DisasmArchitectures.Thumb => (CapstoneNative.ArchArm, 16),
        DisasmArchitectures.ArmV8 => (CapstoneNative.ArchArm, 64),
        DisasmArchitectures.CortexM => (CapstoneNative.ArchArm, 16 | 32),
        DisasmArchitectures.Arm64 => (CapstoneNative.ArchArm64, 0),
        DisasmArchitectures.Mips32 => (CapstoneNative.ArchMips, 4),
        DisasmArchitectures.Mips64 => (CapstoneNative.ArchMips, 8),
        DisasmArchitectures.MicroMips => (CapstoneNative.ArchMips, 4 | 16),
        DisasmArchitectures.Mips32R6 => (CapstoneNative.ArchMips, 4 | 64),
        DisasmArchitectures.Ppc32 => (CapstoneNative.ArchPpc, 4),
        DisasmArchitectures.Ppc64 => (CapstoneNative.ArchPpc, 8),
        DisasmArchitectures.PpcQpx => (CapstoneNative.ArchPpc, 8 | 16),
        DisasmArchitectures.Sparc => (CapstoneNative.ArchSparc, 0),
        DisasmArchitectures.SparcV9 => (CapstoneNative.ArchSparc, 16),
        DisasmArchitectures.SystemZ => (CapstoneNative.ArchSysZ, 0),
        DisasmArchitectures.XCore => (CapstoneNative.ArchXCore, 0),
        "m68k-000" => (CapstoneNative.ArchM68K, 2),
        "m68k-010" => (CapstoneNative.ArchM68K, 4),
        "m68k-020" => (CapstoneNative.ArchM68K, 8),
        "m68k-030" => (CapstoneNative.ArchM68K, 16),
        "m68k-040" => (CapstoneNative.ArchM68K, 32),
        "m68k-060" => (CapstoneNative.ArchM68K, 64),
        DisasmArchitectures.Tms320C64x => (CapstoneNative.ArchTms320C64x, 0),
        "m680x-6301" => (CapstoneNative.ArchM680X, 2),
        "m680x-6309" => (CapstoneNative.ArchM680X, 4),
        "m680x-6800" => (CapstoneNative.ArchM680X, 8),
        "m680x-6801" => (CapstoneNative.ArchM680X, 16),
        "m680x-6805" => (CapstoneNative.ArchM680X, 32),
        "m680x-6808" => (CapstoneNative.ArchM680X, 64),
        "m680x-6809" => (CapstoneNative.ArchM680X, 128),
        "m680x-6811" => (CapstoneNative.ArchM680X, 256),
        "m680x-cpu12" => (CapstoneNative.ArchM680X, 512),
        "m680x-hcs08" => (CapstoneNative.ArchM680X, 1024),
        DisasmArchitectures.Evm => (CapstoneNative.ArchEvm, 0),
        _ => throw new DisassemblerUnavailableException("Unknown architecture: " + id),
    };

    public DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false)
    {
        if (code.IsEmpty || _handle == 0)
        {
            return null;
        }

        fixed (byte* start = code)
        {
            byte* p = start;
            nuint size = (nuint)code.Length;
            ulong a = address;
            if (!CapstoneNative.DisasmIter(_handle, ref p, ref size, ref a, _insn))
            {
                return null;
            }
        }

        byte* insn = (byte*)_insn;
        int length = *(ushort*)(insn + CapstoneNative.InsnSize);
        string mnemonic = Marshal.PtrToStringUTF8((nint)(insn + CapstoneNative.InsnMnemonic)) ?? string.Empty;
        string operands = Marshal.PtrToStringUTF8((nint)(insn + CapstoneNative.InsnOpStr)) ?? string.Empty;
        byte* d = *(byte**)(insn + CapstoneNative.InsnDetail);
        var groups = new List<int>();
        if (d is not null)
        {
            int n = Math.Min((int)d[CapstoneNative.DetailGroupsCount], 8);
            for (int i = 0; i < n; i++)
            {
                groups.Add(d[CapstoneNative.DetailGroups + i]);
            }
        }

        (FlowKind flow, ulong? branch, bool indirect) = Flow(mnemonic, operands, groups);
        ulong? memory = PcRelative(mnemonic, operands, address);
        InstructionDetail? info = detail && d is not null ? Details(d, groups) : null;
        return new DisasmInstruction(address, length, FormatMnemonic(mnemonic), FormatOperands(operands), flow, branch, indirect, memory, info);
    }

    // ---- 分岐の種類 ----

    private static readonly HashSet<string> Unconditional = new(StringComparer.Ordinal)
    {
        "b", "b.w", "b.n", "j", "jmp", "jr", "br", "ba", "bra", "bra.s", "bra.w", "bra.l", "jmpl", "bx", "bxj", "brl", "bc", "jrc", "jic",
        "bctr", "bal", "jalr", "jal", "b.al", "braa", "brab", "jg", "bru",
    };

    private static readonly HashSet<string> Returns = new(StringComparer.Ordinal)
    {
        "ret", "retl", "rts", "rtd", "rtr", "rte", "blr", "eret", "retaa", "retab", "iret", "reti", "rti", "retsp", "deret",
    };

    private (FlowKind Flow, ulong? Target, bool Indirect) Flow(string mnemonic, string operands, List<int> groups)
    {
        bool jump = groups.Contains(CapstoneNative.GroupJump);
        bool call = groups.Contains(CapstoneNative.GroupCall);
        string m = mnemonic.ToLowerInvariant();
        string ops = operands.ToLowerInvariant();
        if (groups.Contains(CapstoneNative.GroupRet) || groups.Contains(CapstoneNative.GroupIret) || Returns.Contains(m) || IsReturn(m, ops))
        {
            return (FlowKind.Return, null, false);
        }

        if (groups.Contains(CapstoneNative.GroupInt))
        {
            return (FlowKind.Interrupt, null, false);
        }

        if (!jump && !call)
        {
            // グループの付かない分岐 (古い表の命令) はニーモニックで補う。
            if (m is "bl" or "blx" or "jal" or "jalr" or "bsr" or "bsr.w" or "bsr.s" or "bsr.l" or "jsr" or "call" or "bas" or "basr" or "brasl" or "bras")
            {
                call = true;
            }
            else if (Unconditional.Contains(m))
            {
                jump = true;
            }
            else
            {
                return (FlowKind.Normal, null, false);
            }
        }

        ulong? target = LastImmediate(ops);
        bool indirect = target is null;
        if (call)
        {
            return (FlowKind.Call, target, indirect);
        }

        // ARM の bl / blx は call のグループが付かないことがある。
        if (m is "bl" or "blx" or "jal" or "jalr" or "bsr" or "jsr" or "brasl" or "bras" or "basr" or "bas")
        {
            return (FlowKind.Call, target, indirect);
        }

        return (Unconditional.Contains(m) ? FlowKind.Jump : FlowKind.ConditionalJump, target, indirect);
    }

    private static bool IsReturn(string m, string ops) =>
        m is "bx" && ops == "lr"
        || m is "jr" && ops == "$ra"
        || m is "br" && ops is "%r14" or "x30"
        || m is "pop" or "pop.w" or "ldm" or "ldmia" or "ldmia.w" && ops.Contains("pc", StringComparison.Ordinal) && ops.Contains('{');

    [GeneratedRegex(@"(?:#|\$)?(-?)(0x[0-9a-f]+|\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImmediateAtEnd();

    [GeneratedRegex(@"^\$([0-9a-f]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex MotorolaHex();

    /// <summary>オペランドの文字列の最後の即値 (分岐先)。M68K の <c>$1234</c> も読む。</summary>
    private static ulong? LastImmediate(string ops)
    {
        if (ops.Length == 0 || ops.EndsWith(']') || ops.EndsWith(')'))
        {
            return null;
        }

        string last = ops[(ops.LastIndexOf(',') + 1)..].Trim();
        if (MotorolaHex().Match(last) is { Success: true } mh)
        {
            return ulong.Parse(mh.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        Match match = ImmediateAtEnd().Match(last);
        if (!match.Success || match.Index != 0)
        {
            return null;
        }

        string digits = match.Groups[2].Value;
        ulong value = digits.StartsWith("0x", StringComparison.Ordinal)
            ? ulong.Parse(digits.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(digits, CultureInfo.InvariantCulture);
        return match.Groups[1].Value == "-" ? unchecked(0 - value) : value;
    }

    /// <summary>PC 相対の参照 (ARM64 の adr / adrp / ldr のリテラル、ARM の [pc, #imm])。</summary>
    private ulong? PcRelative(string mnemonic, string operands, ulong address)
    {
        string m = mnemonic.ToLowerInvariant();
        string ops = operands.ToLowerInvariant();
        if (Architecture.Id == DisasmArchitectures.Arm64 && m is "adr" or "adrp" or "ldr" or "ldrsw" or "prfm" && !ops.Contains('['))
        {
            return LastImmediate(ops);
        }

        if (Architecture.IsArm32 && ops.Contains("[pc, #", StringComparison.Ordinal) && m.StartsWith("ldr", StringComparison.Ordinal))
        {
            int at = ops.IndexOf("[pc, #", StringComparison.Ordinal) + 6;
            int end = ops.IndexOf(']', at);
            if (end > at && LastImmediate(ops[at..end]) is { } offset)
            {
                ulong pc = Architecture.Id is DisasmArchitectures.Thumb or DisasmArchitectures.CortexM ? (address + 4) & ~3UL : address + 8;
                return pc + offset;
            }
        }

        return null;
    }

    // ---- 書式 (ANA-26 の仕様 2・3) ----

    [GeneratedRegex(@"0[xX]([0-9a-fA-F]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HexNumber();

    private string FormatMnemonic(string mnemonic) => _format.UppercaseMnemonic ? mnemonic.ToUpperInvariant() : mnemonic;

    private string FormatOperands(string operands)
    {
        if (_format.UppercaseMnemonic)
        {
            operands = operands.ToUpperInvariant();
        }

        if (_format.HexStyle == HexStyle.Prefix && !_format.UppercaseHex && !_format.UppercaseMnemonic)
        {
            return operands;
        }

        return HexNumber().Replace(operands, m => ulong.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v)
            ? _format.Hex(v)
            : m.Value);
    }

    private InstructionDetail Details(byte* d, List<int> groups)
    {
        var read = new List<string>();
        int nr = Math.Min((int)d[CapstoneNative.DetailRegsReadCount], 12);
        for (int i = 0; i < nr; i++)
        {
            read.Add(Name(CapstoneNative.RegName(_handle, ((ushort*)(d + CapstoneNative.DetailRegsRead))[i])));
        }

        var written = new List<string>();
        int nw = Math.Min((int)d[CapstoneNative.DetailRegsWriteCount], 20);
        for (int i = 0; i < nw; i++)
        {
            written.Add(Name(CapstoneNative.RegName(_handle, ((ushort*)(d + CapstoneNative.DetailRegsWrite))[i])));
        }

        return new InstructionDetail([], read, written, [.. groups.Select(g => Name(CapstoneNative.GroupName(_handle, (uint)g)))]);
    }

    private static string Name(nint p) => p == 0 ? "?" : Marshal.PtrToStringUTF8(p) ?? "?";

    public void Dispose()
    {
        if (_insn != 0)
        {
            CapstoneNative.Free(_insn, 1);
            _insn = 0;
        }

        if (_handle != 0)
        {
            CapstoneNative.Close(ref _handle);
            _handle = 0;
        }
    }
}
