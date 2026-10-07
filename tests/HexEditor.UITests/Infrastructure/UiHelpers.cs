using System.Text.Json.Nodes;
using HexEditor.TestData;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// 複数のテストで使う操作 (命令の通り道の追加の命令: 要素の状態・ドロップ・ウィンドウの大きさ・フォーカス・文字列の検査など)。
/// 命令の一覧はアプリの MainWindow.TestCommands.cs。
/// </summary>
public static class UiHelpers
{
    /// <summary>AutomationId の要素の状態 (アプリの中で探す。ポップアップの中も探す)。</summary>
    public static Task<JsonObject> ElementAsync(this AppSession app, string id) =>
        app.SendAsync("element", new JsonObject { ["id"] = id });

    /// <summary>要素が表示されているか (祖先を含めて)。</summary>
    public static async Task<bool> IsShownAsync(this AppSession app, string id)
    {
        JsonObject e = await app.ElementAsync(id);
        return e["found"]!.GetValue<bool>() && e["effectivelyVisible"]!.GetValue<bool>() && e["width"]!.GetValue<double>() > 0;
    }

    /// <summary>ファイル > 開く と同じ処理で開く (開けないときは通知が出る)。</summary>
    public static Task<JsonObject> UiOpenAsync(this AppSession app, string path) =>
        app.SendAsync("uiOpen", new JsonObject { ["path"] = path });

    /// <summary>ファイル・フォルダのドロップ (Explorer からのドロップと同じ処理)。</summary>
    public static Task<JsonObject> DropAsync(this AppSession app, IEnumerable<string> paths, int? insertAt = null, JsonArray? virtualItems = null)
    {
        var request = new JsonObject { ["paths"] = new JsonArray([.. paths.Select(p => (JsonNode?)p)]) };
        if (insertAt is int at)
        {
            request["insertAt"] = at;
        }

        if (virtualItems is not null)
        {
            request["virtual"] = virtualItems;
        }

        return app.SendAsync("drop", request);
    }

    /// <summary>ウィンドウの大きさを物理ピクセルで変える。</summary>
    public static async Task ResizeAsync(this AppSession app, int width, int height)
    {
        await app.SendAsync("resize", new JsonObject { ["width"] = width, ["height"] = height });
        await Task.Delay(300);
        await app.IdleAsync();
    }

    /// <summary>フォーカスを動かす ("editor"、"next"、"previous"、AutomationId)。フォーカスのある要素 ("型:AutomationId") を返す。</summary>
    public static async Task<string?> FocusAsync(this AppSession app, string target, bool keyboard = true)
    {
        JsonObject r = await app.SendAsync("focus", new JsonObject { ["target"] = target, ["keyboard"] = keyboard });
        await app.IdleAsync();
        return r["focused"]?.GetValue<string>();
    }

    /// <summary>フォーカスのある要素 ("型:AutomationId")。</summary>
    public static async Task<string?> FocusedAsync(this AppSession app) => (await app.StateAsync())["focused"]?.GetValue<string>();

    /// <summary>文字列の切れと要素の重なり (アプリの textCheck)。</summary>
    public static Task<JsonObject> TextCheckAsync(this AppSession app) => app.SendAsync("textCheck");

    /// <summary>表示中の通知 (メッセージ・重要度・件数)。</summary>
    public static async Task<IReadOnlyList<JsonObject>> NotificationsAsync(this AppSession app) =>
        (await app.StateAsync())["notifications"]!.AsArray().Select(n => n!.AsObject()).ToList();

    /// <summary>通知が出るまで待つ。</summary>
    public static async Task<JsonObject> WaitForNotificationAsync(this AppSession app, Func<string, bool> match, string what)
    {
        JsonObject? found = null;
        await app.WaitUntilAsync(async () =>
        {
            found = (await app.NotificationsAsync()).FirstOrDefault(n => match(n["message"]!.GetValue<string>()));
            return found is not null;
        }, TimeSpan.FromSeconds(20), what);
        return found!;
    }

    /// <summary>ログに行が現れるまで待つ。</summary>
    public static async Task<string> WaitForLogAsync(this AppSession app, Func<string, bool> match, string what)
    {
        string? line = null;
        await app.WaitUntilAsync(async () => (line = (await app.LogAsync()).LastOrDefault(match)) is not null, TimeSpan.FromSeconds(20), what);
        return line!;
    }

    /// <summary>タブの数が <paramref name="count"/> になるまで待つ。</summary>
    public static Task WaitForTabsAsync(this AppSession app, int count) =>
        app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == count, TimeSpan.FromSeconds(30), $"{count} tabs");

    /// <summary>ダイアログ (ContentDialog) のボタンを、表示名で押す (UI オートメーションの Invoke)。</summary>
    public static async Task InvokeDialogButtonAsync(this AppSession app, string name)
    {
        FlaUI.Core.AutomationElements.AutomationElement? button = null;
        await app.WaitUntilAsync(() => Task.FromResult((button = app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .FirstOrDefault(b => AppSession.NameOf(b) == name)) is not null), TimeSpan.FromSeconds(15), $"the button '{name}'");
        button!.Patterns.Invoke.Pattern.Invoke();
        await app.IdleAsync();
    }

    /// <summary>ダイアログ (AutomationId で指定) が出るまで待ち、その要素を返す。</summary>
    public static Task<FlaUI.Core.AutomationElements.AutomationElement> WaitForDialogAsync(this AppSession app, string automationId) =>
        app.WaitForAsync(automationId);

    /// <summary>ウィンドウ (ダイアログを含む) の中の、表示名が <paramref name="name"/> のボタン。なければ null。</summary>
    public static FlaUI.Core.AutomationElements.AutomationElement? Button(this AppSession app, string name) =>
        app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)).FirstOrDefault(b => AppSession.NameOf(b) == name);

    /// <summary>ダイアログのボタンの表示名の一覧。</summary>
    public static IReadOnlyList<string> DialogButtons(FlaUI.Core.AutomationElements.AutomationElement dialog) =>
        [.. dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)).Select(AppSession.NameOf).Where(n => n.Length > 0)];

    /// <summary>
    /// 目印のファイル (TD-MARKERS-1G と同じ規則: 先頭・<paramref name="step"/> ごと・末尾に `@` + 16 桁の Hex) を作る。
    /// それ以外は 00。小さいテストデータとして使う (スパースにはしない)。
    /// </summary>
    public static string WriteMarkerFile(UiTestContext ctx, string name, long length, long step)
    {
        string path = Path.Combine(ctx.Root, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(length);
        var positions = new List<long>();
        for (long p = 0; p < length; p += step)
        {
            positions.Add(p);
        }

        positions.Add(length - TestDataCatalog.MarkerLength);
        foreach (long p in positions)
        {
            stream.Position = p;
            stream.Write(TestDataCatalog.Marker(p));
        }

        return path;
    }

    /// <summary>リソース (.resw) の値の一覧 (キー → 値)。</summary>
    public static Dictionary<string, string> LoadResw(string language)
    {
        string path = Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.App", "Strings", language, "Resources.resw");
        return System.Xml.Linq.XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
    }
}
