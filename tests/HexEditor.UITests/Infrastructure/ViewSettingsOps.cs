using System.Text.Json.Nodes;

namespace HexEditor.UITests.Infrastructure;

/// <summary>表示設定のテストの補助 (表示メニュー、入力欄のフライアウト、メニューの項目の状態)。</summary>
public static class ViewSettingsOps
{
    /// <summary>表示メニューなどの項目を押し、描画を済ませる。</summary>
    public static async Task MenuAsync(AppSession app, string id)
    {
        await app.CommandAsync(id);
        await app.IdleAsync();
    }

    /// <summary>メニューの項目の状態 ({found, enabled, checked, text, items})。</summary>
    public static Task<JsonObject> MenuItemAsync(AppSession app, string id) => app.SendAsync("menuItem", new JsonObject { ["id"] = id });

    /// <summary>入力欄のフライアウトに入力する。<paramref name="commit"/> は Enter。状態 ({open, error, okEnabled, invalid}) を返す。</summary>
    public static async Task<JsonObject> InputAsync(AppSession app, string? text, bool commit = false)
    {
        var args = new JsonObject { ["commit"] = commit };
        if (text is not null)
        {
            args["text"] = text;
        }

        JsonObject result = await app.SendAsync("viewInput", args);
        await app.IdleAsync();
        return result;
    }

    /// <summary>「1 行のバイト数 > 指定…」で値を入力して確定する。</summary>
    public static async Task SetBytesPerRowAsync(AppSession app, int value)
    {
        await MenuAsync(app, "Command_ViewBytesPerRowCustom");
        JsonObject state = await InputAsync(app, value.ToString(), commit: true);
        Assert.False(state["open"]!.GetValue<bool>(), $"bytes per row {value}: {state}");
    }

    /// <summary>「ベースアドレスを設定…」で値を入力して確定する。</summary>
    public static async Task SetBaseAddressAsync(AppSession app, string value)
    {
        await MenuAsync(app, "Command_ViewBaseAddress");
        JsonObject state = await InputAsync(app, value, commit: true);
        Assert.False(state["open"]!.GetValue<bool>(), $"base address {value}: {state}");
    }

    /// <summary>アプリを終了する (設定と付随データの書き出しを待つ)。同じ設定フォルダで起動し直すときに使う。</summary>
    public static async Task ExitAsync(AppSession app)
    {
        await Task.Delay(1000);
        try
        {
            await app.SendAsync("exit");
        }
        catch (IOException)
        {
        }

        await app.WaitForExitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>表示設定 (ViewSettings の JSON) とビューの状態。</summary>
    public static Task<JsonObject> ViewSettingsAsync(AppSession app) => app.SendAsync("viewSettings");

    /// <summary>ハイコントラストの描き方にする (OS の設定は変えない模擬)。</summary>
    public static async Task ForceHighContrastAsync(AppSession app)
    {
        await app.SendAsync("forceHighContrast", new JsonObject { ["on"] = true });
        await app.IdleAsync();
    }

    /// <summary>ステータスバーのカーソル位置の文字列。</summary>
    public static Task<string> StatusOffsetAsync(AppSession app) => app.UiaNameAsync("Status_Offset");

    /// <summary>Windows のシステム色のリソースの値 (#AARRGGBB)。ハイコントラストの模擬で使う色と同じ。</summary>
    public static async Task<string> SystemColorAsync(AppSession app, string key) =>
        (await app.SendAsync("systemColor", new JsonObject { ["key"] = key }))["color"]!.GetValue<string>();
}
