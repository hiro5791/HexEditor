using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>文字列の単数形と複数形 (UI-42 の仕様 4)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PluralTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-UI-42-03")]
    public Task Match_count_uses_singular_and_plural() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-MATCH-12: 12 バイト。`41 42` が 1 か所、`41` が 2 か所。
        byte[] data = [0x41, 0x42, 0x00, 0x00, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00];
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-UI-MATCH-12.bin", data)] });

        // 1. Hex の `41 42` をすべて検索: 単数形。
        await SearchResultsTests.OpenFindAsync(app, 0, "41 42");
        await SearchResultsTests.FindAllAsync(app);
        var one = await SearchResultsTests.WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "one result");
        Assert.Equal("Hex: 41 42 — 1 match (completed)", one["summary"]!.GetValue<string>());

        // 2. `41`: 複数形。
        await app.UiaSetValueAsync("Find_Query", "41");
        await SearchResultsTests.FindAllAsync(app);
        var two = await SearchResultsTests.WaitForResultsAsync(app, r => r["count"]!.GetValue<long>() == 2 && !r["running"]!.GetValue<bool>(), "two results");
        Assert.Equal("Hex: 41 — 2 matches (completed)", two["summary"]!.GetValue<string>());
    });
}
