using HexEditor.Core.Disassembly;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Disassembly;

/// <summary>INSP-16 1 命令の逆アセンブル (インスペクタの行)。</summary>
public sealed class InspectorDisassemblyTests
{
    private static readonly InspectorOptions Japanese = new() { Text = InspectorText.English with { Bytes = "({0} バイト)", InvalidInstruction = "不正な命令" } };

    private static InspectorValue Decode(string arch, string hex, Endianness endian = Endianness.Little) =>
        InspectorDecoder.Decode(InspectorTypes.DisasmPrefix + arch, DecoderTests.Hex(hex), [], endian, Japanese);

    [Fact]
    [Trait(TC, "TC-INSP-16-01")]
    public void OneInstruction()
    {
        InspectorValue mov = Decode(DisasmArchitectures.X86_64, "48 89 E5");
        Assert.Equal("mov rbp, rsp (3 バイト)", mov.Text);
        Assert.Equal(3, mov.ByteCount);

        InspectorValue stp = Decode(DisasmArchitectures.Arm64, "FD 7B BF A9");
        Assert.Equal("stp x29, x30, [sp, #-0x10]! (4 バイト)", stp.Text);
        Assert.Equal(4, stp.ByteCount);

        Assert.Equal("ud2 (2 バイト)", Decode(DisasmArchitectures.X86_64, "0F 0B FF").Text);
        InspectorValue bad = Decode(DisasmArchitectures.X86_64, "FF FF");
        Assert.Equal(InspectorStatus.Invalid, bad.Status);
        Assert.Equal("不正な命令", bad.Text);
    }

    [Fact]
    [Trait(TC, "TC-INSP-16-02")]
    public void BranchTargetIsAnOffset()
    {
        InspectorValue jmp = InspectorDecoder.Decode(InspectorTypes.Disasm, DecoderTests.Hex("EB 10"), [], Endianness.Little, Japanese with { DisasmAddress = 0xA0 });
        Assert.Equal(0xB2, jmp.BranchTarget);
        Assert.StartsWith("jmp 0xb2", jmp.Text, StringComparison.Ordinal);
        Assert.Equal(0x12, Decode(DisasmArchitectures.X86_64, "EB 10").BranchTarget);
    }

    [Fact]
    public void EndianFollowsInspector()
    {
        Assert.Equal("ret (4 バイト)", Decode(DisasmArchitectures.Arm64, "C0 03 5F D6").Text);
        Assert.Equal("blr (4 バイト)", Decode(DisasmArchitectures.Ppc32, "4E 80 00 20", Endianness.Big).Text);
    }

    [Fact]
    public void LayoutKeepsOneDisassemblyRow()
    {
        InspectorLayout layout = InspectorLayout.Default;
        Assert.False(layout.Row(InspectorTypes.Disasm)!.Visible);
        InspectorLayout changed = layout.WithVisible(InspectorTypes.Disasm, true).WithDisasmArchitecture(InspectorTypes.Disasm, DisasmArchitectures.Arm64);
        Assert.Null(changed.Row(InspectorTypes.Disasm));
        Assert.True(changed.Row(InspectorTypes.DisasmPrefix + DisasmArchitectures.Arm64)!.Visible);
        InspectorLayout parsed = InspectorLayout.Parse(changed.Serialize());
        Assert.Single(parsed.Rows, r => InspectorTypes.IsDisasm(r.TypeId));
        Assert.Equal(changed, parsed);
    }

    [Fact]
    public void EvaluatesThroughDataInspector()
    {
        using var doc = new Document(new HexEditor.Core.Sources.MemoryByteSource(DecoderTests.Hex("55 48 89 E5")), Options());
        InspectorLayout layout = InspectorLayout.Default.WithVisible(InspectorTypes.Disasm, true);
        ReadForDisplayWhenLoaded(doc.Current, 0, 4);
        InspectorRowResult row = DataInspector.Evaluate(doc.Current, 1, layout, Endianness.Little, Japanese)
            .SelectMany(g => g.Rows).Single(r => r.TypeId == InspectorTypes.Disasm);
        Assert.Equal("mov rbp, rsp (3 バイト)", row.Value.Text);
    }
}
