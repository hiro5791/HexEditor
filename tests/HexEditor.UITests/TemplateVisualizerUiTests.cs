using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.TemplateUiTests;

namespace HexEditor.UITests;

/// <summary>
/// テンプレートのビジュアライザ (07 の TPL-16)。ノードは結果ツリーで選んだのと同じ処理 (<c>tplvis show</c>) で表示する。音声は
/// テスト用の仮想の出力 (サンプル列を記録する。再生位置はテスト用の命令で進める) で確かめる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TemplateVisualizerUiTests
{
    private static Task<JsonObject> VisAsync(AppSession app, string action, JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("tplvis", request, TimeSpan.FromSeconds(120));
    }

    /// <summary>ノードを表示し、読み込みが終わるまで待つ。</summary>
    private static async Task<JsonObject> ShowAsync(AppSession app, string path)
    {
        JsonObject state = await VisAsync(app, "show", new JsonObject { ["path"] = path });
        Assert.True(state["selected"]!.GetValue<bool>(), "node " + path);
        try
        {
            await app.WaitUntilAsync(async () => !(state = await VisAsync(app, "state"))["loading"]!.GetValue<bool>() && state["kind"] is not null,
                Wait, "the visualizer content");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + " " + state.ToJsonString(), ex);
        }
        Assert.True(state["visible"]!.GetValue<bool>());
        return state;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-16-02")]
    public Task Audio_of_a_wav_data_chunk_plays_the_same_samples() => UiTestContext.RunAsync(async ctx =>
    {
        string wav = ctx.TestData("TD-TPL-WAV");
        string template = ctx.TestData("TD-TPL-WAV-HXT");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [wav], ExtraArgs = ["--template", template] });
        await WaitTemplatesAsync(app);
        await VisAsync(app, "audio", new JsonObject { ["op"] = "fake" });
        JsonObject state = await ShowAsync(app, "chunks[1].data");
        Assert.Equal("Audio", state["kind"]!.GetValue<string>());

        // 波形と代替テキスト (長さ 1 秒、44,100 Hz)。
        Assert.True(state["waveform"]!.GetValue<int>() > 0, "waveform");
        string alt = state["contentName"]!.GetValue<string>();
        Assert.Contains("1 ", alt);
        Assert.Contains("44,100", alt);

        // 再生: 仮想の出力が受け取ったサンプル列の 1 秒分が data チャンクと一致する。
        await app.UiaInvokeAsync("TplVis_Play");
        await app.WaitUntilAsync(async () => (await VisAsync(app, "state"))["playing"]!.GetValue<bool>(), Wait, "playing");
        JsonObject received = await VisAsync(app, "audio", new JsonObject { ["op"] = "received", ["count"] = 44_100 });
        Assert.Equal(44_100, received["sampleRate"]!.GetValue<int>());
        byte[] bytes = Convert.FromBase64String(received["samples"]!.GetValue<string>());
        Assert.Equal(44_100 * 2, bytes.Length);
        for (int n = 0; n < 44_100; n++)
        {
            Assert.Equal(TestDataCatalog.TplWavSample(n), BitConverter.ToInt16(bytes, n * 2));
        }

        // 0.5 秒進めて停止すると、位置はそこで止まる。
        await VisAsync(app, "audio", new JsonObject { ["op"] = "advance", ["ms"] = 500 });
        await app.UiaInvokeAsync("TplVis_Stop");
        await app.WaitUntilAsync(async () => !(await VisAsync(app, "state"))["playing"]!.GetValue<bool>(), Wait, "stopped");
        double at = (await VisAsync(app, "state"))["positionMs"]!.GetValue<double>();
        Assert.Equal(500, at, 1);
        await VisAsync(app, "audio", new JsonObject { ["op"] = "advance", ["ms"] = 300 });
        Assert.Equal(at, (await VisAsync(app, "state"))["positionMs"]!.GetValue<double>(), 1);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-16-04")]
    public Task Broken_png_shows_an_error_without_crashing() => UiTestContext.RunAsync(async ctx =>
    {
        string broken = ctx.TestData("TD-TPL-PNG-BROKEN");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [broken], ExtraArgs = ["--template", Fixture("visualize-image.hxt")] });
        await WaitTemplatesAsync(app);
        JsonObject state = await ShowAsync(app, "img");
        Assert.Equal("Image", state["kind"]!.GetValue<string>());
        if (state["width"] is null)
        {
            Assert.StartsWith("Cannot show this image: ", state["message"]!.GetValue<string>());
        }

        // 正しい PNG は画像になる (同じアプリで続けて開く)。アプリは落ちていない。
        string png = ctx.TestData("TD-PNG");
        await app.OpenAsync(png);
        await TemplateAsync(app, "applyText", new JsonObject { ["file"] = Fixture("visualize-image.hxt") });
        await WaitTemplatesAsync(app);
        state = await ShowAsync(app, "img");
        Assert.Equal(16, state["width"]!.GetValue<int>());
        Assert.Contains("16", state["contentName"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-16-05")]
    public Task Struct_array_is_a_table_and_saves_as_csv() => UiTestContext.RunAsync(async ctx =>
    {
        string rec = ctx.TestData("TD-ANA-REC64");
        string csv = Path.Combine(ctx.Root, "recs.csv");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [rec],
            ExtraArgs = ["--template", Fixture("visualize-table.hxt")],
            Hooks = new JsonObject { ["savePicker"] = csv },
        });
        await WaitTemplatesAsync(app);
        JsonObject state = await ShowAsync(app, "recs");
        Assert.Equal("Table", state["kind"]!.GetValue<string>());
        Assert.Equal(["tag", "no", "rest"], state["columns"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(16, state["rows"]!.GetValue<long>());
        Assert.Equal(16, state["listRows"]!.GetValue<int>());
        var table = new List<string[]>();
        for (int i = 0; i < 16; i++)
        {
            JsonObject row = await VisAsync(app, "row", new JsonObject { ["index"] = i });
            string[] values = [.. row["values"]!.AsArray().Select(v => v!.GetValue<string>())];
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), values[1]);
            Assert.Contains("no: " + i, row["name"]!.GetValue<string>());
            table.Add(values);
        }

        // 保存ボタン → CSV (見出しの行と 16 行。値は表と同じ)。
        await app.UiaInvokeAsync("TplVis_Save");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(csv) && new FileInfo(csv).Length > 0), Wait, "the CSV file");
        string[] lines = (await File.ReadAllTextAsync(csv)).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(17, lines.Length);
        Assert.Equal("tag,no,rest", lines[0]);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(string.Join(",", table[i].Select(Csv)), lines[i + 1]);
        }
    });

    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-16-01")]
    public Task Bitmap_of_a_top_down_bmp_has_alt_text() => UiTestContext.RunAsync(async ctx =>
    {
        string bmp = ctx.TestData("TD-TPL-BMP");
        string template = Path.Combine(AppLocator.RepositoryRoot, "tests", "fixtures", "templates", "TC-TPL-16-01", "template.hxt");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [bmp], ExtraArgs = ["--template", template] });
        await WaitTemplatesAsync(app);
        JsonObject state = await ShowAsync(app, "pixels");
        Assert.Equal(4, state["width"]!.GetValue<int>());
        Assert.Equal(4, state["height"]!.GetValue<int>());
        Assert.Equal(TestDataCatalog.TplBmpExpected().Select(p => p.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
            state["pixels"]!.AsArray().Select(p => p!.GetValue<string>()));
        string alt = state["contentName"]!.GetValue<string>();
        Assert.Contains("width 4", alt);
        Assert.Contains("height 4", alt);
        Assert.Contains("bgra8", alt);
    });
}
