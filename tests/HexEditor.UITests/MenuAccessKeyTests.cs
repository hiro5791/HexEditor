using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// メニューのアクセスキーの重複 (09 の UI-03 の仕様 5、UI-42 の仕様 6)。XAML に書いたメニューは Core の ResourceTests で静的に調べる。
/// ここでは言語ごとにアプリを起動し、コードで作る項目 (ズーム・表示の設定・文字コードなど) を含む実行中のメニューを調べる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class MenuAccessKeyTests
{
    public static TheoryData<string> Languages =>
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
    ];

    [Theory]
    [MemberData(nameof(Languages))]
    [Trait(UiTest.TC, "TC-UI-03-04")]
    public Task Access_keys_are_unique_within_each_running_menu(string language) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = language, Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonArray items = (await app.SendAsync("menuTexts"))["items"]!.AsArray();

        // 1〜2. メニューバー (トップの項目) と各メニュー・サブメニューの中で、アクセスキー (大文字・小文字を区別しない) が重ならない。
        var duplicates = items.Select(i => i!.AsObject())
            .Where(i => i["accessKey"]?.GetValue<string>() is { Length: > 0 })
            .GroupBy(i => i["menu"]?.GetValue<bool>() == true ? "(menu bar)" : i["parent"]!.GetValue<string>())
            .SelectMany(menu => menu.GroupBy(i => i["accessKey"]!.GetValue<string>().ToUpperInvariant())
                .Where(g => g.Count() > 1)
                .Select(g => $"{menu.Key}: '{g.Key}' is used by {string.Join(", ", g.Select(i => i["text"]!.GetValue<string>()))}"))
            .ToList();
        Assert.True(duplicates.Count == 0, $"{language}: duplicate access keys:\n" + string.Join("\n", duplicates));
    });
}
