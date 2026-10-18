namespace HexEditor.Core.Disassembly;

/// <summary>逆アセンブルのエンジン。</summary>
public enum DisasmEngine
{
    /// <summary>Iced (x86 16 / 32 / 64 bit。マネージド、MIT)。</summary>
    Iced,

    /// <summary>Capstone 4 のネイティブライブラリ (BSD-3-Clause)。</summary>
    Capstone,

    /// <summary>本体の自前の逆アセンブラ (RISC-V、6502 系、BPF、WebAssembly。Capstone 4 にないもの)。</summary>
    Managed,

    /// <summary>プラグインの逆アセンブラ (08 の AUTO-31 の <c>IDisassembler</c>)。</summary>
    Plugin,
}

/// <summary>
/// 逆アセンブルのアーキテクチャとモード 1 つ (ANA-23 の仕様 1)。<see cref="Id"/> は設定・インスペクタの型 (INSP-16) に保存する ID。
/// <see cref="Family"/> はツールバーの「アーキテクチャ」、<see cref="Mode"/> は「モード」に出す名前 (技術用語なので翻訳しない)。
/// <see cref="HasEndian"/> はエンディアンを選べるアーキテクチャ。<see cref="Alignment"/> は命令の境界の単位 (固定長なら命令長)、
/// <see cref="VariableLength"/> は命令境界が分からない (チェックポイントから解釈し直す) アーキテクチャ。
/// </summary>
public sealed record DisasmArchitecture(
    string Id,
    string Family,
    string Mode,
    DisasmEngine Engine,
    bool HasEndian = false,
    bool BigEndianDefault = false,
    int Alignment = 1,
    bool VariableLength = true,
    int MaxLength = 16)
{
    /// <summary>一覧の「その他」に出す (ANA-23 の仕様 1 の表にないもの)。</summary>
    public bool IsOther { get; init; }

    /// <summary>インプレースアセンブル (ANA-29) ができる (x86 16 / 32 / 64 bit だけ。仕様 6)。</summary>
    public bool CanAssemble => Engine == DisasmEngine.Iced;

    /// <summary>x86 か (構文の設定 (ANA-26 の仕様 8) を使う)。</summary>
    public bool IsX86 => Engine == DisasmEngine.Iced;

    /// <summary>ARM の 32 bit 系か (レジスタ名の設定 (ANA-26 の仕様 4) を使う)。</summary>
    public bool IsArm32 => Family == "ARM";

    /// <summary>x86 のビット数 (x86 でなければ 0)。</summary>
    public int Bitness => Id switch
    {
        DisasmArchitectures.X86_16 => 16,
        DisasmArchitectures.X86_32 => 32,
        DisasmArchitectures.X86_64 => 64,
        _ => 0,
    };

    /// <summary>アドレスを 16 桁で表示するか (64 bit のアーキテクチャ)。</summary>
    public bool WideAddress { get; init; }

    /// <summary>プラグインの逆アセンブラ (Engine が Plugin のとき)。</summary>
    public HexEditor.Sdk.Plugins.IDisassembler? Plugin { get; init; }

    /// <summary>プラグイン名 (ツールチップに出す。AUTO-31 の仕様 2)。</summary>
    public string? PluginName { get; init; }

    public string DisplayName => Mode.Length == 0 ? Family : Family + " (" + Mode + ")";
}

/// <summary>
/// 逆アセンブルのアーキテクチャの一覧 (ANA-23 の仕様 1)。Capstone 4 にない RISC-V・6502 系・BPF・WebAssembly は本体の
/// 自前の逆アセンブラで解釈する。プラグインの逆アセンブラ (AUTO-31) は <see cref="WithPlugins"/> で一覧の末尾に加える。
/// </summary>
public static class DisasmArchitectures
{
    public const string X86_16 = "x86-16";
    public const string X86_32 = "x86-32";
    public const string X86_64 = "x86-64";
    public const string Arm = "arm";
    public const string Thumb = "thumb";
    public const string ArmV8 = "armv8";
    public const string CortexM = "cortex-m";
    public const string Arm64 = "arm64";
    public const string Mips32 = "mips32";
    public const string Mips64 = "mips64";
    public const string MicroMips = "micromips";
    public const string Mips32R6 = "mips32r6";
    public const string Ppc32 = "ppc32";
    public const string Ppc64 = "ppc64";
    public const string PpcQpx = "ppc-qpx";
    public const string Sparc = "sparc";
    public const string SparcV9 = "sparcv9";
    public const string SystemZ = "systemz";
    public const string XCore = "xcore";
    public const string RiscV32 = "riscv32";
    public const string RiscV64 = "riscv64";
    public const string RiscV32C = "riscv32c";
    public const string RiscV64C = "riscv64c";
    public const string Mos6502 = "6502";
    public const string Mos65C02 = "65c02";
    public const string W65C02 = "w65c02";
    public const string W65C816 = "65816";
    public const string Tms320C64x = "tms320c64x";
    public const string Bpf = "bpf";
    public const string Ebpf = "ebpf";
    public const string Wasm = "wasm";
    public const string Evm = "evm";

    /// <summary>MSIL (.NET。ANA-27)。命令列の逆アセンブラではなく、メソッド単位で解釈する。</summary>
    public const string Msil = "msil";

    public const string PluginPrefix = "plugin:";

    /// <summary>既定のアーキテクチャ (INSP-16 の仕様 1、実行形式でないドキュメント)。</summary>
    public const string Default = X86_64;

    /// <summary>組み込みのアーキテクチャ (一覧の順)。</summary>
    public static IReadOnlyList<DisasmArchitecture> BuiltIn { get; } =
    [
        new(X86_16, "x86", "16 bit", DisasmEngine.Iced, MaxLength: 15),
        new(X86_32, "x86", "32 bit", DisasmEngine.Iced, MaxLength: 15),
        new(X86_64, "x86", "64 bit", DisasmEngine.Iced, MaxLength: 15) { WideAddress = true },
        new(Arm, "ARM", "ARM", DisasmEngine.Capstone, HasEndian: true, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(Thumb, "ARM", "Thumb", DisasmEngine.Capstone, HasEndian: true, Alignment: 2, MaxLength: 4),
        new(ArmV8, "ARM", "ARMv8 (AArch32)", DisasmEngine.Capstone, HasEndian: true, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(CortexM, "ARM", "Cortex-M", DisasmEngine.Capstone, HasEndian: true, Alignment: 2, MaxLength: 4),
        new(Arm64, "ARM64", string.Empty, DisasmEngine.Capstone, HasEndian: true, Alignment: 4, VariableLength: false, MaxLength: 4) { WideAddress = true },
        new(Mips32, "MIPS", "MIPS32", DisasmEngine.Capstone, HasEndian: true, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(Mips64, "MIPS", "MIPS64", DisasmEngine.Capstone, HasEndian: true, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4) { WideAddress = true },
        new(MicroMips, "MIPS", "microMIPS", DisasmEngine.Capstone, HasEndian: true, BigEndianDefault: true, Alignment: 2, MaxLength: 4),
        new(Mips32R6, "MIPS", "MIPS32R6", DisasmEngine.Capstone, HasEndian: true, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(Ppc32, "PowerPC", "32 bit", DisasmEngine.Capstone, HasEndian: true, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(Ppc64, "PowerPC", "64 bit", DisasmEngine.Capstone, HasEndian: true, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4) { WideAddress = true },
        new(PpcQpx, "PowerPC", "QPX", DisasmEngine.Capstone, HasEndian: true, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4) { WideAddress = true },
        new(Sparc, "SPARC", "SPARC", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(SparcV9, "SPARC", "SPARC V9", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4) { WideAddress = true },
        new(SystemZ, "SystemZ", string.Empty, DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 6) { WideAddress = true },
        new(XCore, "XCore", string.Empty, DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 4),
        new("m68k-000", "68K", "68000", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 22),
        new("m68k-010", "68K", "68010", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 22),
        new("m68k-020", "68K", "68020", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 22),
        new("m68k-030", "68K", "68030", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 22),
        new("m68k-040", "68K", "68040", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 22),
        new("m68k-060", "68K", "68060", DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 2, MaxLength: 22),
        new(RiscV32, "RISC-V", "RV32", DisasmEngine.Managed, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(RiscV64, "RISC-V", "RV64", DisasmEngine.Managed, Alignment: 4, VariableLength: false, MaxLength: 4) { WideAddress = true },
        new(RiscV32C, "RISC-V", "RV32C", DisasmEngine.Managed, Alignment: 2, MaxLength: 4),
        new(RiscV64C, "RISC-V", "RV64C", DisasmEngine.Managed, Alignment: 2, MaxLength: 4) { WideAddress = true },
        new(Mos6502, "6502", "6502", DisasmEngine.Managed, MaxLength: 3),
        new(Mos65C02, "6502", "65C02", DisasmEngine.Managed, MaxLength: 3),
        new(W65C02, "6502", "W65C02", DisasmEngine.Managed, MaxLength: 3),
        new(W65C816, "6502", "65816", DisasmEngine.Managed, MaxLength: 4),
        new(Tms320C64x, "TMS320C64x", string.Empty, DisasmEngine.Capstone, BigEndianDefault: true, Alignment: 4, VariableLength: false, MaxLength: 4),
        new(Bpf, "BPF", "BPF", DisasmEngine.Managed, HasEndian: true, Alignment: 8, VariableLength: false, MaxLength: 8),
        new(Ebpf, "BPF", "eBPF", DisasmEngine.Managed, HasEndian: true, Alignment: 8, MaxLength: 16) { WideAddress = true },
        new(Wasm, "WebAssembly", string.Empty, DisasmEngine.Managed, MaxLength: 64),

        // ---- その他 (Capstone 4 が対応するもの) ----
        new("m680x-6800", "M680X", "6800", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-6801", "M680X", "6801", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-6805", "M680X", "6805", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-6808", "M680X", "6808", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-6809", "M680X", "6809", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-6811", "M680X", "68HC11", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-cpu12", "M680X", "CPU12", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 6) { IsOther = true },
        new("m680x-hcs08", "M680X", "HCS08", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-6301", "M680X", "6301", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new("m680x-6309", "M680X", "6309", DisasmEngine.Capstone, BigEndianDefault: true, MaxLength: 5) { IsOther = true },
        new(Evm, "EVM", string.Empty, DisasmEngine.Capstone, MaxLength: 33) { IsOther = true },
    ];

    private static readonly Dictionary<string, DisasmArchitecture> ById = BuiltIn.ToDictionary(a => a.Id, StringComparer.Ordinal);

    private static IReadOnlyList<DisasmArchitecture> s_plugins = [];

    /// <summary>プラグインの逆アセンブラ (AUTO-31)。<see cref="SetPlugins"/> で差し替える。</summary>
    public static IReadOnlyList<DisasmArchitecture> Plugins => Volatile.Read(ref s_plugins);

    /// <summary>組み込みとプラグインの全部。</summary>
    public static IReadOnlyList<DisasmArchitecture> All => [.. BuiltIn, .. Plugins];

    /// <summary>
    /// プラグインの逆アセンブラを登録し直す (プラグインの読み込み・無効化のたびに呼ぶ)。ID は <c>plugin:&lt;拡張ポイントの ID&gt;</c>。
    /// </summary>
    public static void SetPlugins(IEnumerable<(string ExtensionId, HexEditor.Sdk.Plugins.IDisassembler Disassembler, string PluginName)> plugins) =>
        Volatile.Write(ref s_plugins, [.. plugins.Select(p => new DisasmArchitecture(PluginPrefix + p.ExtensionId, SafeName(p.Disassembler),
            string.Empty, DisasmEngine.Plugin, MaxLength: 64) { Plugin = p.Disassembler, PluginName = p.PluginName, IsOther = true })]);

    private static string SafeName(HexEditor.Sdk.Plugins.IDisassembler d)
    {
        try
        {
            return d.Architecture;
        }
        catch (Exception)
        {
            return d.Id;
        }
    }

    public static DisasmArchitecture? Find(string? id) =>
        id is null ? null : ById.TryGetValue(id, out DisasmArchitecture? a) ? a : Plugins.FirstOrDefault(p => p.Id == id);

    public static DisasmArchitecture Get(string id) => Find(id) ?? throw new ArgumentException($"未知のアーキテクチャです: {id}", nameof(id));

    /// <summary>アーキテクチャの名前 (ツールバーの「アーキテクチャ」) の一覧。表の順。</summary>
    public static IReadOnlyList<string> Families(bool includeOther = true) =>
        [.. All.Where(a => includeOther || !a.IsOther).Select(a => a.Family).Distinct()];

    /// <summary>アーキテクチャの中のモードの一覧。</summary>
    public static IReadOnlyList<DisasmArchitecture> ModesOf(string family) => [.. All.Where(a => a.Family == family)];
}
