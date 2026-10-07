using System.Text.Json.Nodes;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// Hex ビューの操作の補助 (表示・移動・選択・入力のテスト)。ポインタ・ホイール・スクロールバーの操作は、テスト用の命令の通り道で
/// アプリの中の同じ処理に渡す (実際のマウスは使わない)。座標は Hex ビューの描画面の座標 (epx)。
/// </summary>
public static class ViewOps
{
    /// <summary>描画モデル (行・セル・カーソル・セル幅・行の高さなど)。</summary>
    public static Task<JsonObject> RenderAsync(AppSession app) => app.RenderAsync();

    /// <summary>描画モデルの、先頭オフセットが <paramref name="rowStart"/> の行。なければ null。</summary>
    public static JsonObject? Row(JsonObject render, long rowStart)
    {
        int digits = render["offsetDigits"]!.GetValue<int>();
        string text = rowStart.ToString("X" + digits);
        return render["rows"]!.AsArray().FirstOrDefault(r => r!["offsetText"]!.GetValue<string>() == text)?.AsObject();
    }

    /// <summary>行の Hex 列の文字列 (セルを空白で区切る)。</summary>
    public static string RowHex(JsonObject row) =>
        string.Join(' ', row["cells"]!.AsArray().Select(c => c!["hex"]!.GetValue<string>()));

    /// <summary>1 行の中の Hex 列の c 番目のバイトの文字位置 (中央に 1 文字分の区切り。HexView.RowColumns と同じ)。</summary>
    public static int HexIndex(int bytesPerRow, int c) => c * 3 + (c >= bytesPerRow / 2 && bytesPerRow > 1 ? 1 : 0);

    /// <summary>1 行の中のテキスト列の c 番目の文字位置。</summary>
    public static int TextIndex(int bytesPerRow, int c) => bytesPerRow * 3 + (bytesPerRow > 1 ? 1 : 0) + 1 + c;

    /// <summary>
    /// オフセットのセルの文字の中央の座標。<paramref name="text"/> が false なら Hex 列の <paramref name="charIndex"/> 文字目
    /// (0: 上位ニブル、1: 下位ニブル)、true ならテキスト列。
    /// </summary>
    public static (double X, double Y) CellPoint(JsonObject render, long offset, bool text = false, int charIndex = 0)
    {
        int b = render["bytesPerRow"]!.GetValue<int>();
        long topRow = render["topRow"]!.GetValue<long>();
        double cell = render["cellWidth"]!.GetValue<double>();
        double height = render["rowHeight"]!.GetValue<double>();
        int c = (int)(offset % b);
        int index = text ? TextIndex(b, c) : HexIndex(b, c) + charIndex;
        double x = render["contentLeft"]!.GetValue<double>() + (index + 0.5) * cell - render["horizontalOffset"]!.GetValue<double>();
        double y = ((offset / b) - topRow + 0.5) * height - render["subRowOffset"]!.GetValue<double>();
        return (x, y);
    }

    /// <summary>オフセット列の、表示中の <paramref name="rowIndex"/> 番目の行の中央の座標。</summary>
    public static (double X, double Y) OffsetColumnPoint(JsonObject render, int rowIndex) =>
        (render["offsetLeft"]!.GetValue<double>() + render["cellWidth"]!.GetValue<double>() * 2,
            (rowIndex + 0.5) * render["rowHeight"]!.GetValue<double>() - render["subRowOffset"]!.GetValue<double>());

    /// <summary>クリック (左ボタンを押して離す。<paramref name="clicks"/> が 2 ならダブルクリック)。</summary>
    public static Task ClickAsync(AppSession app, (double X, double Y) point, bool shift = false, int clicks = 1) =>
        app.SendAsync("pointer", new JsonObject
        {
            ["action"] = "click", ["x"] = point.X, ["y"] = point.Y, ["shift"] = shift, ["clicks"] = clicks,
        });

    /// <summary>右クリック (右クリックメニューも開く)。</summary>
    public static Task RightClickAsync(AppSession app, (double X, double Y) point) =>
        app.SendAsync("pointer", new JsonObject { ["action"] = "click", ["x"] = point.X, ["y"] = point.Y, ["button"] = "right" });

    /// <summary>ポインタの操作 1 つ (down / move / up)。</summary>
    public static Task PointerAsync(AppSession app, string action, (double X, double Y) point, string device = "mouse") =>
        app.SendAsync("pointer", new JsonObject { ["action"] = action, ["x"] = point.X, ["y"] = point.Y, ["device"] = device });

    /// <summary>ホイール (<paramref name="delta"/> は 1 ノッチ 120。下へ回すと負)。</summary>
    public static Task WheelAsync(AppSession app, int delta, int count = 1, bool shift = false, bool horizontal = false) =>
        app.SendAsync("wheel", new JsonObject { ["delta"] = delta, ["count"] = count, ["shift"] = shift, ["horizontal"] = horizontal });

    /// <summary>Ctrl+G で移動バーを開く。</summary>
    public static async Task OpenGoToAsync(AppSession app)
    {
        Assert.Equal("menu:Command_GoTo", (await app.KeyAsync("G", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
    }

    /// <summary>Ctrl+G で移動バーを開き、<paramref name="input"/> を入力して Enter を押す。</summary>
    public static async Task GoToAsync(AppSession app, string input)
    {
        await OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", input);
        await app.SendAsync("goToKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();
    }

    public static Task<JsonObject> ElementAsync(AppSession app, string id) => app.SendAsync("element", new JsonObject { ["id"] = id });

    public static async Task<long> CursorAsync(AppSession app) => (await app.DocumentAsync())["cursor"]!.GetValue<long>();

    public static async Task<long> TopRowAsync(AppSession app) => (await app.DocumentAsync())["topRow"]!.GetValue<long>();

    public static async Task AssertSelectionAsync(AppSession app, long start, long length)
    {
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(length, doc["selectionLength"]!.GetValue<long>());
        if (length > 0)
        {
            Assert.Equal(start, doc["selectionStart"]!.GetValue<long>());
        }
    }

    /// <summary>
    /// 設定ファイル (UI-23 の settings.json) を設定フォルダに書く。起動前に書けば起動時に読み、起動中に書けば外部の編集として反映される。
    /// </summary>
    public static void WriteSettings(string profile, JsonObject settings)
    {
        var root = new JsonObject { ["$schemaVersion"] = 1 };
        foreach ((string key, JsonNode? value) in settings)
        {
            root[key] = value?.DeepClone();
        }

        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), root.ToJsonString());
    }

    /// <summary>表示中の通知 (InfoBar) の文。</summary>
    public static async Task<IReadOnlyList<string>> NoticesAsync(AppSession app) =>
        (await app.StateAsync())["notifications"]!.AsArray().Select(n => n!["message"]?.GetValue<string>() ?? string.Empty).ToList();

    /// <summary>描画モデルの中身が読み込まれる (仮表示でなくなる) まで待つ。</summary>
    public static async Task<JsonObject> WaitForContentAsync(AppSession app, long rowStart)
    {
        JsonObject render = null!;
        await app.WaitUntilAsync(async () =>
        {
            render = await app.RenderAsync();
            return Row(render, rowStart) is { } row && row["cells"]!.AsArray().All(c => c!["state"]?.GetValue<string>() is null or "Valid");
        }, TimeSpan.FromSeconds(15), $"row {rowStart:X} to load");
        return render;
    }
}
