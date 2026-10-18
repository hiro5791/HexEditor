using HexEditor.Core.Disassembly;
using HexEditor.Core.Plugins;
using HexEditor.Core.Tests.Plugins;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Disassembly;

/// <summary>プラグインの逆アセンブラ (08 の AUTO-31 の IDisassembler) を逆アセンブルの一覧の「その他」に出して使う。</summary>
[Collection(SerialCollection.Name)]
public sealed class PluginDisassemblerTests
{
    [Fact]
    [Trait("TC", "TC-AUTO-31-01")]
    public async Task Sample_disassembler_is_listed_and_decodes()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sample), "test.sample");
        await t.StartAsync();
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);

        DisasmArchitecture arch = Assert.Single(DisasmArchitectures.Plugins, a => a.Id == DisasmArchitectures.PluginPrefix + "test.sample.disasm");
        Assert.Equal("sample-arch", arch.Family);
        Assert.Equal("Sample Plugin", arch.PluginName);
        Assert.True(arch.IsOther);
        Assert.Contains(DisasmArchitectures.All, a => a.Id == arch.Id);

        using IInstructionDecoder decoder = Disassembler.Create(arch, DisasmFormat.Default);
        IReadOnlyList<DisasmInstruction> list = Disassembler.Sweep(decoder, [0xAB, 0xCD], 0x100, DisasmFormat.Default);
        Assert.Equal(["db 0xAB", "db 0xCD"], list.Select(i => i.Text));
        Assert.Equal(0x101UL, list[1].Address);
    }
}
