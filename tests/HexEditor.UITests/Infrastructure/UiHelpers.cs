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

    /// <summary>
    /// 画面に収まる大きさ (物理ピクセル)。ウィンドウを作り直したとき (セッションの復元など)、Windows は画面より大きいウィンドウを
    /// 画面の大きさ (と枠) に縮める。CI のランナーの画面は小さい (1024 px 幅) ので、復元を確かめるテストはこの大きさを使う。
    /// </summary>
    public static async Task<(int Width, int Height)> FitToScreenAsync(this AppSession app, int width, int height)
    {
        JsonObject shell = await app.SendAsync("shellState");
        return (Math.Min(width, shell["monitorWidth"]!.GetValue<int>() - 40), Math.Min(height, shell["monitorHeight"]!.GetValue<int>() - 40));
    }

    /// <summary>テスト対象のアプリの配布形態 (Development、Portable、Installer、Msix)。</summary>
    public static async Task<string> DistributionAsync(this AppSession app) =>
        (await app.StateAsync())["distribution"]!.GetValue<string>();

    /// <summary>ウィンドウの大きさを物理ピクセルで変える。</summary>
    public static async Task ResizeAsync(this AppSession app, int width, int height)
    {
        await app.SendAsync("resizePhysical", new JsonObject { ["width"] = width, ["height"] = height });
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
        IReadOnlyList<JsonObject> shown = [];
        try
        {
            await app.WaitUntilAsync(async () =>
            {
                found = (shown = await app.NotificationsAsync()).FirstOrDefault(n => match(n["message"]!.GetValue<string>()));
                return found is not null;
            }, TimeSpan.FromSeconds(20), what);
        }
        catch (TimeoutException ex)
        {
            // 出ている通知とアプリのログの末尾を失敗の文に入れる (CI のログだけで原因を調べるため)。
            throw new TimeoutException($"{ex.Message} Shown: [{string.Join(" | ", shown.Select(n => n["message"]?.GetValue<string>()))}]. Log:\n"
                + string.Join("\n", (await app.LogAsync()).TakeLast(15)), ex);
        }

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

    /// <summary>
    /// ダイアログ (ContentDialog) のボタンを、表示名で押す (UI オートメーションの Invoke)。<paramref name="idle"/> が false なら
    /// 押した後の処理の完了を待たない (アプリが終わるボタンや、続く処理の途中の状態を確かめるとき)。
    /// </summary>
    public static async Task InvokeDialogButtonAsync(this AppSession app, string name, bool idle = true)
    {
        FlaUI.Core.AutomationElements.AutomationElement? button = null;
        await app.WaitUntilAsync(() => Task.FromResult((button = app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .FirstOrDefault(b => AppSession.NameOf(b) == name)) is not null), TimeSpan.FromSeconds(15), $"the button '{name}'");
        button!.Patterns.Invoke.Pattern.Invoke();
        if (idle)
        {
            await app.IdleAsync();
        }
    }

    /// <summary>
    /// ダイアログの文字が <paramref name="expected"/> をすべて含むまで待ち、文字を返す。ダイアログの中身 (変更量など) は表示の後に
    /// UI オートメーションに現れることがある (遅い CI のランナー・リリースのビルド)。
    /// </summary>
    public static async Task<string> WaitForDialogTextAsync(this AppSession app, FlaUI.Core.AutomationElements.AutomationElement dialog, params string[] expected)
    {
        string text = string.Empty;
        try
        {
            await app.WaitUntilAsync(() => Task.FromResult(expected.All((text = AppSession.AllText(dialog)).Contains)), TimeSpan.FromSeconds(5), "the dialog text");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The dialog text has no '{string.Join("', '", expected.Where(e => !text.Contains(e, StringComparison.Ordinal)))}': {text}");
        }

        return text;
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
