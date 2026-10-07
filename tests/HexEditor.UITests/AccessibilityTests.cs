using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>キーボードのフォーカスの枠 (UI-52 の仕様 6)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class AccessibilityTests
{
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [Trait(UiTest.TC, "TC-UI-52-04")]
    public Task Tab_focus_shows_a_focus_rectangle(string theme) => UiTestContext.RunAsync(async ctx =>
    {
        // ハイコントラスト「夜空」の回は Windows のコントラストテーマを切り替える必要があるため行わない (作業中の PC の設定を
        // 変えない)。Tab はシステムのキーボードを使わず、Tab と同じフォーカスの移動 (FocusManager.TryMoveFocus、キーボードでの
        // 移動として扱う) をテスト用の命令で行う。
        string profile = ctx.NewProfile();
        await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), $"{{\"$schemaVersion\": 1, \"ui.theme\": \"{theme}\"}}");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        Assert.Equal(theme == "light" ? "Light" : "Dark", (await app.StateAsync())["actualTheme"]!.GetValue<string>());

        var missing = new List<string>();
        var checkedElements = new HashSet<string>();
        foreach (string screen in new[] { "document", "findBar", "goToBar" })
        {
            if (screen == "findBar")
            {
                await OperationsTests.OpenFindAsync(app, "41 42");
            }
            else if (screen == "goToBar")
            {
                await app.UiaInvokeAsync("Find_Close");
                await app.KeyAsync("G", ctrl: true);
                await app.IdleAsync();
            }

            await app.FocusAsync("editor", keyboard: false);
            for (int step = 0; step < 30; step++)
            {
                JsonObject focus = await app.SendAsync("focus", new JsonObject { ["target"] = "next" });
                await app.IdleAsync();
                await Task.Delay(150);
                string name = $"{focus["focused"]}@{focus["where"]}";
                if (focus["left"] is null || name.StartsWith("HexView", StringComparison.Ordinal) || !checkedElements.Add(screen + "|" + name))
                {
                    continue;
                }

                WindowImage focused = app.Screenshot();
                await app.FocusAsync("editor", keyboard: false);
                await Task.Delay(150);
                WindowImage unfocused = app.Screenshot();
                if (!FrameDiffers(app, focused, unfocused, focus))
                {
                    missing.Add($"{screen}: {name}");
                }

                // フォーカスを戻して次へ進む。
                string target = focus["focused"]!.GetValue<string>().Split(':') is [_, { Length: > 0 } id] ? id : "editor";
                await app.FocusAsync(target);
            }
        }

        Assert.True(checkedElements.Count >= 5, $"only {checkedElements.Count} elements received focus");
        Assert.True(missing.Count == 0, $"{theme}: no focus rectangle on:\n" + string.Join("\n", missing));
    });

    [Fact(Skip = "ハイコントラスト「夜空」の回は Windows のコントラストテーマを有効にする必要があり、作業中の PC の設定を変えるため自動では実行しない (CI の専用の環境が必要)")]
    [Trait(UiTest.TC, "TC-UI-52-04")]
    public Task Tab_focus_shows_a_focus_rectangle_in_high_contrast() => Task.CompletedTask;

    /// <summary>要素の外周 (外側 4 px と内側 2 px) に、フォーカスの有無で画素の違いがあるか。</summary>
    private static bool FrameDiffers(AppSession app, WindowImage a, WindowImage b, JsonObject bounds)
    {
        double scale = 1;
        try
        {
            scale = app.StateAsync().GetAwaiter().GetResult()["scale"]!.GetValue<double>();
        }
        catch (InvalidOperationException)
        {
        }

        NativeMethods.GetWindowRect(app.Hwnd, out NativeMethods.Rect window);
        WindowHitTest.ClientOrigin(app.Hwnd, out int originX, out int originY);
        int dx = originX - window.Left;
        int dy = originY - window.Top;
        int left = dx + (int)Math.Floor(bounds["left"]!.GetValue<double>() * scale);
        int top = dy + (int)Math.Floor(bounds["top"]!.GetValue<double>() * scale);
        int right = dx + (int)Math.Ceiling(bounds["right"]!.GetValue<double>() * scale);
        int bottom = dy + (int)Math.Ceiling(bounds["bottom"]!.GetValue<double>() * scale);
        int differing = 0;
        for (int y = Math.Max(0, top - 4); y < Math.Min(a.Height, bottom + 4); y++)
        {
            for (int x = Math.Max(0, left - 4); x < Math.Min(a.Width, right + 4); x++)
            {
                bool frame = x < left + 2 || x >= right - 2 || y < top + 2 || y >= bottom - 2;
                if (frame && a.Pixel(x, y) != b.Pixel(x, y))
                {
                    differing++;
                }
            }
        }

        return differing >= 8;
    }
}
