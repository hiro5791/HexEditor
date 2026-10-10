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

    [Fact]
    public void Editing_changes_the_name_and_extensions_but_keeps_the_view()
    {
        ViewPreset a = ViewPresets.FromView("A", ".a", ViewSettings.Default with { BytesPerRow = 8 });
        ViewPreset b = ViewPresets.FromView("B", string.Empty, ViewSettings.Default);
        IReadOnlyList<ViewPreset> edited = ViewPresets.Edit([a, b], "a", " NES ", ".nes; gb")!;
        Assert.Equal(["NES", "B"], edited.Select(p => p.Name));
        Assert.Equal([".nes", ".gb"], edited[0].Extensions);
        Assert.Equal(8, edited[0].ApplyTo(ViewSettings.Default).BytesPerRow);

        // 名前を変えずに条件だけ変える (大文字・小文字の違いは同じ名前)。
        Assert.Equal("a", ViewPresets.Edit([a, b], "A", "a", string.Empty)![0].Name);

        // ほかのプリセットと同じ名前・空の名前・ない名前は変えない。
        Assert.Null(ViewPresets.Edit([a, b], "A", "b", string.Empty));
        Assert.Null(ViewPresets.Edit([a, b], "A", "  ", string.Empty));
        Assert.Null(ViewPresets.Edit([a, b], "C", "D", string.Empty));
    }

    [Fact]
    public void Import_over_the_limit_is_refused_instead_of_truncated()
    {
        ViewPreset[] existing = [.. Enumerable.Range(0, 98).Select(i => ViewPresets.FromView("P" + i, string.Empty, ViewSettings.Default))];

        // 同じ名前の置き換えは数に入れない: 98 + 新しい 2 = 100 は読み込める。
        ViewPreset[] fits = [ViewPresets.FromView("p0", string.Empty, ViewSettings.Default), .. new[] { "X", "Y" }.Select(n => ViewPresets.FromView(n, string.Empty, ViewSettings.Default))];
        IReadOnlyList<ViewPreset>? merged = ViewPresets.Merge(existing, fits, out int total);
        Assert.Equal(100, total);
        Assert.Equal(100, merged!.Count);

        // 101 個になる場合は null (一覧を変えない)。
        ViewPreset[] over = [.. new[] { "X", "Y", "Z" }.Select(n => ViewPresets.FromView(n, string.Empty, ViewSettings.Default))];
        Assert.Null(ViewPresets.Merge(existing, over, out total));
        Assert.Equal(101, total);
    }
}
