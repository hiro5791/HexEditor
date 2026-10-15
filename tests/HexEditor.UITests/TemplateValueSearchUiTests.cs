using System.Buffers.Binary;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.TemplateUiTests;

namespace HexEditor.UITests;

/// <summary>
/// テンプレートの値の検索 (FIND-29): テンプレート結果パネルの検索欄の条件で検索し、共通の結果一覧に出し、結果を選ぶとツリーのノードと
/// エディタの範囲を選ぶ。条件の解釈と検索そのものは Core のテスト (TemplateValueSearchTests)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TemplateValueSearchUiTests
{
    /// <summary>TD-PE-X64 の <c>Characteristics</c> を名前に含むフィールドの (オフセット, 長さ) (テストのコードが PE を読んで求める)。</summary>
    private static HashSet<(long, long)> ExpectedCharacteristics(byte[] pe)
    {
        long lfanew = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(0x3C));
        long fileHeader = lfanew + 4;
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)fileHeader + 2));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)fileHeader + 16));
        long optional = fileHeader + 20;
        long table = optional + optionalSize;
        var expected = new HashSet<(long, long)> { (fileHeader + 18, 2), (optional + 70, 2) };
        for (int i = 0; i < sections; i++)
        {
            expected.Add((table + i * 40 + 36, 4));
        }

        return expected;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-29-01")]
    public Task Pe_characteristics_fields_are_listed_and_a_result_selects_the_node_and_the_range() => UiTestContext.RunAsync(async ctx =>
    {
        string pe = ctx.TestData("TD-PE-X64");
        byte[] bytes = File.ReadAllBytes(pe);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [pe] });
        await TemplateAsync(app, "removeAll");
        await TemplateAsync(app, "apply", new JsonObject { ["template"] = "pe" });
        await WaitTemplatesAsync(app);
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "templates" });

        // 1: 検索欄に name:*Characteristics* を入力して検索する (すべて解析してから検索)。
        JsonObject search = await TemplateAsync(app, "filter", new JsonObject { ["text"] = "name:*Characteristics*", ["all"] = true });
        Assert.True(search["valueSearch"]!.GetValue<bool>());
        Assert.Equal(string.Empty, search["message"]!.GetValue<string>());

        // 2: 共通の結果一覧に、パス・オフセット・長さ・値付きで出る。
        HashSet<(long, long)> expected = ExpectedCharacteristics(bytes);
        JsonObject results = null!;
        await app.WaitUntilAsync(async () =>
        {
            results = await app.SendAsync("searchResults", new JsonObject { ["from"] = 0, ["count"] = 100 });
            return results["visible"]!.GetValue<bool>() && !results["running"]!.GetValue<bool>() && results["count"]!.GetValue<long>() == expected.Count;
        }, Wait, "the template value results");
        List<JsonObject> rows = [.. results["rows"]!.AsArray().Select(r => r!.AsObject())];
        Assert.Equal(expected, rows.Select(r => (r["offset"]!.GetValue<long>(), r["length"]!.GetValue<long>())).ToHashSet());
        Assert.All(rows, r =>
        {
            string field = r["endian"]!.GetValue<string>();
            Assert.Matches(@"Characteristics = \S", field);
        });

        // 3: 結果一覧の 1 行目を選ぶ。
        JsonObject first = rows[0];
        string firstPath = first["endian"]!.GetValue<string>().Split(" = ")[0];
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 0 });

        // 4: ツリーでそのノードが選ばれ、エディタでそのフィールドの範囲が選ばれている。
        await app.WaitUntilAsync(async () => (await TemplateAsync(app, "state"))["selectedPath"]?.GetValue<string>() == firstPath, Wait, "the tree node of the first result");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(first["offset"]!.GetValue<long>(), doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(first["length"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>());

        // 5: 構文エラーは位置を示す。
        JsonObject bad = await TemplateAsync(app, "filter", new JsonObject { ["text"] = "name:*Characteristics* value>" });
        Assert.Contains("29", bad["message"]!.GetValue<string>(), StringComparison.Ordinal);
    });
}
