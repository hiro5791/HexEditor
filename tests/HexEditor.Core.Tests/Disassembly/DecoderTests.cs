using HexEditor.Core.Disassembly;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Disassembly;

/// <summary>ANA-23 の逆アセンブラ (アーキテクチャごとの解釈、db、構文と数値の書式 (ANA-26))。</summary>
public sealed class DecoderTests
{
    internal static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal));

    internal static IReadOnlyList<DisasmInstruction> Sweep(string arch, string hex, bool? bigEndian = null, DisasmFormat? format = null,
        ulong address = 0)
    {
        format ??= DisasmFormat.Default;
        using IInstructionDecoder decoder = Disassembler.Create(arch, format, bigEndian);
        return Disassembler.Sweep(decoder, Hex(hex), address, format);
    }

    private static string Normalize(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [Trait(TC, "TC-ANA-23-02")]
    [InlineData(DisasmArchitectures.X86_16, "B8 34 12", null, "mov ax, 0x1234")]
    [InlineData(DisasmArchitectures.X86_32, "55 89 E5", null, "push ebp|mov ebp, esp")]
    [InlineData(DisasmArchitectures.X86_64, "48 89 E5 C3", null, "mov rbp, rsp|ret")]
    [InlineData(DisasmArchitectures.Arm, "1E FF 2F E1", false, "bx lr")]
    [InlineData(DisasmArchitectures.Thumb, "70 47", false, "bx lr")]
    [InlineData(DisasmArchitectures.Arm64, "C0 03 5F D6", null, "ret")]
    [InlineData(DisasmArchitectures.Mips32, "03 E0 00 08", true, "jr $ra")]
    [InlineData(DisasmArchitectures.Ppc32, "4E 80 00 20", true, "blr")]
    [InlineData(DisasmArchitectures.Sparc, "81 C3 E0 08", null, "retl")]
    [InlineData(DisasmArchitectures.SystemZ, "07 FE", null, "br %r14")]
    [InlineData("m68k-000", "4E 75", null, "rts")]
    [InlineData(DisasmArchitectures.RiscV32, "67 80 00 00", null, "jalr zero, 0(ra)")]
    [InlineData(DisasmArchitectures.RiscV32C, "82 80", null, "c.jr ra")]
    [InlineData(DisasmArchitectures.Mos6502, "A9 01 60", null, "lda #$01|rts")]
    [InlineData(DisasmArchitectures.Ebpf, "95 00 00 00 00 00 00 00", false, "exit")]
    [InlineData(DisasmArchitectures.Wasm, "41 2A 0B", null, "i32.const 42|end")]
    public void KnownInstructions(string arch, string hex, bool? big, string expected)
    {
        IReadOnlyList<DisasmInstruction> list = Sweep(arch, hex, big);
        Assert.Equal(expected.Split('|'), list.Select(i => Normalize(i.Text)));
        Assert.Equal(Hex(hex).Length, list.Sum(i => i.Length));
        Assert.DoesNotContain(list, i => i.IsInvalid);
    }

    public static TheoryData<string, string, string> ManagedFixture()
    {
        var data = new TheoryData<string, string, string>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "disasm", "managed.txt")))
        {
            if (line.Length > 0 && !line.StartsWith('#'))
            {
                string[] p = line.Split('\t');
                data.Add(p[0], p[1], p[2]);
            }
        }

        return data;
    }

    /// <summary>TC-ANA-23-02 の手順 2: 本体の自前の逆アセンブラ (Capstone 4 にないアーキテクチャ) のフィクスチャ。</summary>
    [Theory]
    [Trait(TC, "TC-ANA-23-02")]
    [MemberData(nameof(ManagedFixture))]
    public void ManagedDecodersMatchTheFixture(string arch, string hex, string expected)
    {
        IReadOnlyList<DisasmInstruction> list = Sweep(arch, hex);
        Assert.Equal(expected.Split('|'), list.Select(i => i.Text));
        Assert.Equal(Hex(hex).Length, list.Sum(i => i.Length));
    }

    /// <summary>TC-ANA-23-02 の手順 2: Capstone 4 のアーキテクチャの代表的な命令 (各モードで解釈でき、長さが合う)。</summary>
    [Theory]
    [Trait(TC, "TC-ANA-23-02")]
    [InlineData(DisasmArchitectures.ArmV8, "1E FF 2F E1", false, "bx lr")]
    [InlineData(DisasmArchitectures.CortexM, "70 47", false, "bx lr")]
    [InlineData(DisasmArchitectures.Mips64, "03 E0 00 08", true, "jr $ra")]
    [InlineData(DisasmArchitectures.MicroMips, "45 9F", true, "jr16 $ra")]
    [InlineData(DisasmArchitectures.Mips32R6, "03 E0 00 09", true, "jr $ra")]
    [InlineData(DisasmArchitectures.Ppc64, "4E 80 00 20", true, "blr")]
    [InlineData(DisasmArchitectures.SparcV9, "81 C3 E0 08", null, "retl")]
    [InlineData(DisasmArchitectures.Tms320C64x, "00 00 00 00", null, "NOP")]
    [InlineData("m68k-040", "4E 75", null, "rts")]
    [InlineData(DisasmArchitectures.Arm64, "FD 7B BF A9", null, "stp x29, x30, [sp, #-0x10]!")]
    public void CapstoneModesDecode(string arch, string hex, bool? big, string expected)
    {
        IReadOnlyList<DisasmInstruction> list = Sweep(arch, hex, big);
        Assert.Equal(expected, Normalize(list[0].Text));
        Assert.Equal(Hex(hex).Length, list.Sum(i => i.Length));
    }

    [Fact]
    [Trait(TC, "TC-ANA-23-05")]
    public void InvalidBytesBecomeDbAndDecodingContinues()
    {
        IReadOnlyList<DisasmInstruction> x64 = Sweep(DisasmArchitectures.X86_64, "06 C3");
        Assert.Equal(["db 0x06", "ret"], x64.Select(i => i.Text));
        Assert.Equal(1, x64[0].Length);
        Assert.True(x64[0].IsInvalid);

        // Capstone 4 は 00 00 00 00 を ARM64 の命令にしないので、db が 4 行 (1 バイトずつ) になる。
        IReadOnlyList<DisasmInstruction> arm64 = Sweep(DisasmArchitectures.Arm64, "00 00 00 00 C0 03 5F D6");
        Assert.Equal(5, arm64.Count);
        Assert.All(arm64.Take(4), i => Assert.True(i.IsInvalid && i.Length == 1));
        Assert.Equal("ret", arm64[4].Text);
    }

    [Fact]
    [Trait(TC, "TC-ANA-26-01")]
    public void AttSyntaxAndHexSuffix()
    {
        Assert.Equal("movq %rsp, %rbp", Sweep(DisasmArchitectures.X86_64, "48 89 E5", format: new DisasmFormat { Syntax = DisasmSyntax.Att })[0].Text);
        Assert.Equal("mov eax, 10h", Sweep(DisasmArchitectures.X86_32, "B8 10 00 00 00", format: new DisasmFormat { HexStyle = HexStyle.Suffix })[0].Text);
        Assert.Equal("mov eax, 0x10", Sweep(DisasmArchitectures.X86_32, "B8 10 00 00 00")[0].Text);
    }
}
