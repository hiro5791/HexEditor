using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// UI-47 の仕様 1 の主要な画面のうち、今の版にあるものを順に表示する: スタートページ、ファイルを開いた状態、検索バー
/// (オプションを開いた状態)、移動バー、サイズを指定して新規作成のダイアログ、バージョン情報、閉じるときの確認ダイアログ。
/// コマンドパレット・設定画面・データインスペクタはフェーズ 1。メニューを開いた状態は、メニューを開くとフォーカスが動くため
/// 撮らない (メニューの文字列は TC-UI-46-01 で調べる)。
/// </summary>
public static class MainScreens
{
    public static readonly string[] Names = ["start", "document", "findBar", "goToBar", "newSizeDialog", "aboutDialog", "closeDialog"];

    /// <summary>
    /// 画面を 1 つずつ表示し、表示したら画面の名前を返す (呼び出し側はそのときに撮影・検査する)。<paramref name="width"/> と
    /// <paramref name="height"/> は表示倍率 100% 換算のウィンドウの大きさ。
    /// </summary>
    public static async IAsyncEnumerable<string> ShowAsync(AppSession app, UiTestContext ctx, int width, int height)
    {
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        await app.ResizeAsync((int)Math.Round(width * scale), (int)Math.Round(height * scale));
        await SettleAsync(app);
        yield return "start";

        await app.OpenAsync(ctx.TestData("TD-SEQ-1M"));
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        await app.SelectAsync(0x10, 0x10);
        await SettleAsync(app);
        yield return "document";

        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        (await app.WaitForAsync("Find_Options")).Patterns.Toggle.Pattern.Toggle();
        await app.UiaSetValueAsync("Find_Query", "Hello");
        await SettleAsync(app);
        yield return "findBar";
        await app.UiaInvokeAsync("Find_Close");

        await app.KeyAsync("G", ctrl: true);
        await app.IdleAsync();
        await app.UiaSetValueAsync("GoTo_Input", "0x1F00");
        await SettleAsync(app);
        yield return "goToBar";
        await app.KeyAsync("Home", ctrl: true);

        await app.CommandAsync("Command_NewWithSize");
        await app.WaitForAsync("NewSize_Size");
        await SettleAsync(app);
        yield return "newSizeDialog";
        await CloseDialogAsync(app, "NewSizeDialog", "Common_Cancel");

        await app.CommandAsync("Command_About");
        await app.WaitForAsync("About_Version");
        await SettleAsync(app);
        yield return "aboutDialog";
        await CloseDialogAsync(app, "AboutDialog", "Common_Close");

        // 変更のある文書でウィンドウを閉じようとしたときの確認 (UI-13)。
        await app.TypeAsync("41");
        await app.CommandAsync("Command_Exit");
        await app.WaitForAsync("CloseDialog");
        await SettleAsync(app);
        yield return "closeDialog";
        await CloseDialogAsync(app, "CloseDialog", "Common_Cancel");
    }

    /// <summary>レイアウトと描画が落ち着くのを待つ。</summary>
    private static async Task SettleAsync(AppSession app)
    {
        await Task.Delay(400);
        await app.IdleAsync();
    }

    /// <summary>ダイアログ (AutomationId) のボタンを、表示言語のリソースの文字列で探して押す。</summary>
    private static async Task CloseDialogAsync(AppSession app, string dialogId, string key)
    {
        string uiCulture = (await app.StateAsync())["uiCulture"]!.GetValue<string>();
        IReadOnlyList<Regex> names = TrimReport.ValuePatterns(key, uiCulture);
        var dialog = await app.WaitForAsync(dialogId);
        var button = dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .FirstOrDefault(b => names.Any(n => n.IsMatch(AppSession.NameOf(b))));
        if (button is null)
        {
            throw new InvalidOperationException($"No '{key}' button in the dialog.");
        }

        button.Patterns.Invoke.Pattern.Invoke();
        await app.IdleAsync();
        await Task.Delay(300);
    }
}
