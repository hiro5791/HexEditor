using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 画像表示 (VIEW-36)。設定は設定の欄で選んだのと同じ処理 (<c>visualizerSet</c>)、画素のクリックはクリックと同じ処理 (<c>visualizerClick</c>) で行う
/// (マウスは使わない)。画像は、パネルが描いた画素 (拡大前) を読んで比べる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class VisualizerTests
{
    private static async Task<AppSession> StartAsync(UiTestContext ctx)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-VIEW-BMP24")] });
        await app.CommandAsync("Command_ToggleVisualizer");
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await StateAsync(app))["active"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the image view panel");
        return app;
    }

    private static Task<JsonObject> StateAsync(AppSession app, JsonObject? args = null) => app.SendAsync("visualizer", args);

    /// <summary>TC-VIEW-36-01 の手順 1〜2: 0x36 に移動し、BGR888・幅 64・下から上・1 倍にする。<paramref name="fixedStart"/> なら開始位置を「固定」の 0x36。</summary>
    private static async Task SetUpBitmapAsync(AppSession app, bool fixedStart)
    {
        await ViewOps.GoToAsync(app, "0x36");
        Assert.Equal(0x36, await ViewOps.CursorAsync(app));
        var args = new JsonObject { ["format"] = "Bgr888", ["width"] = 64, ["bottomUp"] = true, ["zoom"] = 1 };
        if (fixedStart)
        {
            args["start"] = "fixed";
            args["fixed"] = "0x36";
        }

        JsonObject state = await app.SendAsync("visualizerSet", args);
        Assert.Equal(0x36, state["layout"]!["start"]!.GetValue<long>());
        Assert.Equal(192, state["layout"]!["stride"]!.GetValue<long>());
        Assert.Equal(32, state["layout"]!["rows"]!.GetValue<int>());
    }

    private static async Task<uint[]> PixelsAsync(AppSession app)
    {
        JsonArray pixels = (await StateAsync(app, new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 64, ["height"] = 32 }))["pixels"]!.AsArray();
        return [.. pixels.Select(p => Convert.ToUInt32(p!.GetValue<string>(), 16))];
    }

    private static uint Expected(int x, int y)
    {
        (byte r, byte g, byte b) = TestDataCatalog.Bmp24Pixel(x, y);
        return 0xFF000000 | (uint)(r << 16) | (uint)(g << 8) | b;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-36-01")]
    public Task Shows_the_pixels_of_a_24_bit_bmp() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await SetUpBitmapAsync(app, fixedStart: false);

        // 3・4: 左上から 64×32 画素が BMP の画素と一致する。
        uint[] pixels = await PixelsAsync(app);
        Assert.Equal(64 * 32, pixels.Length);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                Assert.True(Expected(x, y) == pixels[y * 64 + x], $"pixel ({x}, {y}): {pixels[y * 64 + x]:X8}, expected {Expected(x, y):X8}");
            }
        }

        // カーソルの位置の画素の枠と、マウスを合わせた画素の値 (仕様 5・6)。
        Assert.True((await StateAsync(app))["frames"]!.GetValue<int>() >= 1);
        JsonObject hover = await app.SendAsync("visualizerHover", new JsonObject { ["x"] = 1, ["y"] = 0 });
        Assert.Equal("0x1779 RGB(4, 0, 251)", hover["hover"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-36-02")]
    public Task Clicking_a_pixel_moves_the_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await SetUpBitmapAsync(app, fixedStart: true);

        // 1・2: 左から 10、上から 5 の画素 → 0x36 + (31 − 5) × 192 + 10 × 3 = 0x13D4。
        Assert.True((await app.SendAsync("visualizerClick", new JsonObject { ["x"] = 10, ["y"] = 5 }))["clicked"]!.GetValue<bool>());
        await app.IdleAsync();
        Assert.Equal(0x13D4, await ViewOps.CursorAsync(app));

        // 3: Alt+← で 0x36 に戻る (クリックはジャンプ履歴に記録する)。
        await app.FocusAsync("editor");
        await app.KeyAsync("Left", alt: true);
        await app.IdleAsync();
        Assert.Equal(0x36, await ViewOps.CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-36-04")]
    public Task Saves_the_shown_image_as_png() => UiTestContext.RunAsync(async ctx =>
    {
        string output = Path.Combine(ctx.Root, "out.png");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-VIEW-BMP24")], Hooks = new JsonObject { ["savePicker"] = output } });
        await app.CommandAsync("Command_ToggleVisualizer");
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await StateAsync(app))["active"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the image view panel");
        await SetUpBitmapAsync(app, fixedStart: false);
        uint[] shown = await PixelsAsync(app);

        // 1: パネルのメニュー「画像として保存…」→ 保存ダイアログで out.png。
        (await app.WaitForAsync("Visualizer_Menu")).Patterns.ExpandCollapse.Pattern.Expand();
        await app.UiaInvokeAsync("Visualizer_SaveImage");
        await app.WaitForNotificationAsync(m => m.Contains("out.png", StringComparison.Ordinal), "the saved notice");

        // 2: 正しい PNG として読め、すべての画素が表示と一致する。
        uint[] saved = DecodePng(File.ReadAllBytes(output), out int width, out int height);
        Assert.Equal((64, 32), (width, height));
        Assert.Equal(shown, saved);
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-36-03")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Frame_time_on_a_100_GB_file() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        await app.CommandAsync("Command_ToggleVisualizer");
        await app.IdleAsync();
        await app.SendAsync("visualizerSet", new JsonObject { ["format"] = "Gray8", ["width"] = 1024, ["zoom"] = 1 });
        await ViewOps.GoToAsync(app, "0x1000000000");

        // 1〜3: 開始位置はカーソルに追従。PageDown を繰り返し、描き直しごとにパネルの描画に掛かった時間をアプリの中で測る。
        var times = new List<double>();
        for (int i = 0; i < 300; i++)
        {
            await app.KeyAsync("PageDown");
            await app.IdleAsync();
            times.Add((await StateAsync(app))["renderMs"]!.GetValue<double>());
        }

        times.Sort();
        double p99 = times[(int)(times.Count * 0.99)];
        TimeBudget.Limit(p99 <= 16.7, $"image view frame time p99 {p99:F1} ms");
    });

    /// <summary>PNG (8 bit RGBA、フィルタなし) を読む (このアプリの書き出しの形式)。</summary>
    private static uint[] DecodePng(byte[] png, out int width, out int height)
    {
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        int pos = 8;
        width = height = 0;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos + 8));
                height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos + 12));
                Assert.Equal(8, png[pos + 16]);
                Assert.Equal(6, png[pos + 17]);
            }
            else if (type == "IDAT")
            {
                idat.Write(png, pos + 8, length);
            }

            pos += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        uint[] pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int line = y * (1 + width * 4);
            Assert.Equal(0, bytes[line]);
            for (int x = 0; x < width; x++)
            {
                int o = line + 1 + x * 4;
                pixels[y * width + x] = (uint)(bytes[o + 3] << 24 | bytes[o] << 16 | bytes[o + 1] << 8 | bytes[o + 2]);
            }
        }

        return pixels;
    }
}
