using HexEditor.Core.View;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.View;

/// <summary>VIEW-42 の仕様 6・7: プリセットの作成・自動適用の条件・エクスポートとインポート (Core の部分)。</summary>
public sealed class ViewPresetTests
{
    [Fact]
    public void Export_and_import_round_trip_and_unknown_fields_are_ignored()
    {
        ViewPreset nes = ViewPresets.FromView("NES", ".nes; gb", ViewSettings.Default with { BytesPerRow = 8, LowercaseHex = true, BaseAddress = 0x8000 });
        Assert.Equal([".nes", ".gb"], nes.Extensions);
        Assert.False(nes.View.ContainsKey("baseAddress"));
        Assert.True(nes.Matches(@"C:\x\GAME.NES"));
        Assert.False(nes.Matches(@"C:\x\game.bin"));

        IReadOnlyList<ViewPreset> back = ViewPresets.Import(ViewPresets.Export([nes]));
        ViewSettings applied = Assert.Single(back).ApplyTo(ViewSettings.Default);
        Assert.Equal((8, true), (applied.BytesPerRow, applied.LowercaseHex));

        ViewPreset future = Assert.Single(ViewPresets.Import(TestDataCatalog.ViewPresetFutureJson));
        Assert.Equal("Future", future.Name);
        Assert.Equal(16, future.ApplyTo(ViewSettings.Default with { BytesPerRow = 32 }).BytesPerRow);
    }

    [Fact]
    public void Invalid_json_reports_the_line()
    {
        var ex = Assert.Throws<ViewPresetFormatException>(() => ViewPresets.Import("{\n  \"presets\": [\n    { \"name\": \"Bad\" "));
        Assert.NotNull(ex.Line);
    }

    [Fact]
    public void Same_names_are_replaced_and_the_count_is_limited()
    {
        ViewPreset a1 = ViewPresets.FromView("A", string.Empty, ViewSettings.Default with { BytesPerRow = 8 });
        ViewPreset a2 = ViewPresets.FromView("a", string.Empty, ViewSettings.Default with { BytesPerRow = 32 });
        IReadOnlyList<ViewPreset> list = ViewPresets.Normalize([a1, a2]);
        Assert.Equal(32, Assert.Single(list).ApplyTo(ViewSettings.Default).BytesPerRow);
        Assert.Equal(ViewPresets.MaxPresets, ViewPresets.Normalize(Enumerable.Range(0, 150).Select(i => ViewPresets.FromView("P" + i, string.Empty, ViewSettings.Default))).Count);
    }
}
