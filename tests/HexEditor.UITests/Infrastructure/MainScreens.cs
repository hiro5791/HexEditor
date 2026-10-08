using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// UI-47 の仕様 1 の主要な画面を順に表示する: スタートページ、各メニューを開いた状態、ファイルを開いた状態、コマンドパレット、
/// 検索バー (オプションを開いた状態)、移動バー、データインスペクタ、設定画面の各カテゴリ、サイズを指定して新規作成のダイアログ、
/// バージョン情報、閉じるときの確認ダイアログ、処理センター。
/// <para>
/// メニューは UI オートメーションで開く。メニューを開くとフォーカスが動くことがあるため、CI (GITHUB_ACTIONS) か
/// HEXEDITOR_SCREENSHOT_MENUS=1 のときだけ開き、それ以外は「失敗」(理由付き) として記録する (作業中の PC で前面を奪わない)。
/// </para>
/// </summary>
public static class MainScreens
{
    /// <summary>メニューバーのメニューの数 (MainWindow.xaml の MenuBarItem。テスト用のメニューは除く)。</summary>
    public const int MenuCount = 8;

    /// <summary>設定画面のカテゴリ (Core の SettingCategories.All と同じ順)。</summary>
    public static readonly string[] SettingsCategories =
        ["general", "appearance", "view", "editing", "search", "files", "keyboard", "language", "accessibility", "automation", "update", "privacy", "explorer", "advanced"];

    /// <summary>フェーズ 0 からある画面 (疑似翻訳の切れの検査 TC-UI-46-02 が調べる画面)。</summary>
    public static readonly string[] Names = ["start", "document", "findBar", "goToBar", "newSizeDialog", "aboutDialog", "closeDialog"];

    /// <summary>スクリーンショット (UI-47) で撮るすべての画面。</summary>
    public static readonly string[] AllNames =
    [
        "start", .. Enumerable.Range(1, MenuCount).Select(i => $"menu{i}"), "document", "palette", "findBar", "goToBar", "inspector",
        .. SettingsCategories.Select(c => "settings-" + c), "newSizeDialog", "aboutDialog", "closeDialog", "processingCenter",
    ];

    /// <summary>メニューを開いてよいか (CI か、明示したとき)。</summary>
    public static bool MenusAllowed =>
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" || Environment.GetEnvironmentVariable("HEXEDITOR_SCREENSHOT_MENUS") == "1";

    /// <summary>
    /// フェーズ 0 からある画面 (<see cref="Names"/>) を 1 つずつ表示し、表示したら画面の名前を返す (呼び出し側はそのときに撮影・検査する)。
    /// <paramref name="width"/> と <paramref name="height"/> は表示倍率 100% 換算のウィンドウの大きさ。
    /// </summary>
    public static async IAsyncEnumerable<string> ShowAsync(AppSession app, UiTestContext ctx, int width, int height)
    {
        await foreach ((string name, string? error) in ShowAllAsync(app, ctx, width, height, Names, requireSize: false))
        {
            if (error is not null)
            {
                throw new InvalidOperationException($"{name}: {error}");
            }

            yield return name;
        }
    }

    /// <summary>
    /// <paramref name="names"/> の画面 (既定は <see cref="AllNames"/>) を 1 つずつ表示し、表示したら (名前, null) を、表示できなかったら
    /// (名前, 理由) を返す。表示できなかった画面があっても次の画面に進む (UI-47 の「エラー」)。
    /// <paramref name="zoom"/> は画面全体のズーム (表示倍率 200% の再現に使う。1 は 100%)。<paramref name="requireSize"/> なら、ウィンドウを
    /// その大きさにできない (画面より大きい) ときにすべての画面を「失敗」にする。
    /// </summary>
    public static async IAsyncEnumerable<(string Name, string? Error)> ShowAllAsync(AppSession app, UiTestContext ctx, int width, int height,
        IReadOnlyList<string>? names = null, double zoom = 1, bool requireSize = true)
    {
        names ??= AllNames;
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        int wantWidth = (int)Math.Round(width * scale * zoom);
        int wantHeight = (int)Math.Round(height * scale * zoom);
        string size = (await app.SendAsync("resizePhysical", new JsonObject { ["width"] = wantWidth, ["height"] = wantHeight }))["size"]!.GetValue<string>();
        await SettleAsync(app);
        if (requireSize && size != $"{wantWidth}x{wantHeight}")
        {
            // 画面より大きいウィンドウにはできない (ランナーの画面の大きさ)。その大きさのすべての画面を「失敗」にする。
            foreach (string name in names)
            {
                yield return (name, $"the window could not be resized to {wantWidth}x{wantHeight} (got {size})");
            }

            yield break;
        }

        bool documentOpen = false;
        foreach (string name in names)
        {
            string? error = null;
            try
            {
                if (name != "start" && !name.StartsWith("menu", StringComparison.Ordinal) && !documentOpen)
                {
                    await OpenDocumentAsync(app, ctx);
                    documentOpen = true;
                }

                error = await ShowOneAsync(app, name);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or ArgumentException or COMException)
            {
                error = ex.Message;
            }

            if (error is null)
            {
                await SettleAsync(app);
            }

            yield return (name, error);
            try
            {
                await CloseOneAsync(app, name);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or ArgumentException or COMException)
            {
                // 閉じられなかった: 次の画面の表示で失敗すれば、その画面が「失敗」になる。
            }
        }
    }

    private static async Task OpenDocumentAsync(AppSession app, UiTestContext ctx)
    {
        // セッションごとに複製を開く。前のセッションのアプリが同じファイルを編集中 (書き込み禁止のハンドル。ENG-15) だと、他のアプリが
        // 書き込み中として読み取り専用で開く (ENG-14 の仕様 1) ため、最後の画面の編集ができない。
        string folder = Path.Combine(ctx.Root, "screens-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "TD-SEQ-1M.bin");
        File.Copy(ctx.TestData("TD-SEQ-1M"), file);
        await app.OpenAsync(file);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        await app.SelectAsync(0x10, 0x10);
    }

    /// <summary>画面を表示する。表示できない理由があれば返す。</summary>
    private static async Task<string?> ShowOneAsync(AppSession app, string name)
    {
        switch (name)
        {
            case "start":
            case "document":
                return null;

            case var _ when name.StartsWith("menu", StringComparison.Ordinal):
            {
                if (!MenusAllowed)
                {
                    return "menus are opened only in CI (GITHUB_ACTIONS) or with HEXEDITOR_SCREENSHOT_MENUS=1, because opening a menu can take the focus";
                }

                int index = int.Parse(name["menu".Length..], System.Globalization.CultureInfo.InvariantCulture) - 1;
                List<AutomationElement> menus = TopMenus(app);
                if (index >= menus.Count)
                {
                    return $"there are only {menus.Count} menus";
                }

                menus[index].Patterns.ExpandCollapse.Pattern.Expand();
                await app.IdleAsync();
                return null;
            }

            case "palette":
                await app.SendAsync("palette", new JsonObject { ["text"] = ">" });
                return null;

            case "findBar":
                await app.KeyAsync("F", ctrl: true);
                await app.IdleAsync();
                await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
                (await app.WaitForAsync("Find_Options")).Patterns.Toggle.Pattern.Toggle();
                await app.UiaSetValueAsync("Find_Query", "Hello");
                return null;

            case "goToBar":
                await app.KeyAsync("G", ctrl: true);
                await app.IdleAsync();
                await app.UiaSetValueAsync("GoTo_Input", "0x1F00");
                return null;

            case "inspector":
                await app.SendAsync("panelShow", new JsonObject { ["id"] = "inspector" });
                return null;

            case var _ when name.StartsWith("settings-", StringComparison.Ordinal):
                await app.SendAsync("settingsPage", new JsonObject { ["category"] = name["settings-".Length..] });
                return null;

            case "newSizeDialog":
                await app.CommandAsync("Command_NewWithSize");
                await app.WaitForAsync("NewSize_Size");
                return null;

            case "aboutDialog":
                await app.CommandAsync("Command_About");
                await app.WaitForAsync("About_Version");
                return null;

            case "closeDialog":
                // 変更のある文書でウィンドウを閉じようとしたときの確認 (UI-13)。
                await app.TypeAsync("41");
                await app.CommandAsync("Command_Exit");
                await app.WaitForAsync("CloseDialog");
                return null;

            case "processingCenter":
            {
                // 遅いデータソースの検索を実行中にし、ステータスバーの進捗表示から処理センターを開く (UI-37)。
                await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-16M", ["length"] = 16 * 1024 * 1024, ["content"] = "zero", ["delayMs"] = 2000 });
                await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the slow tab");
                await app.KeyAsync("F", ctrl: true);
                await app.IdleAsync();
                await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 0 });
                await app.UiaSetValueAsync("Find_Query", "DE AD BE EF 01");
                await app.UiaInvokeAsync("Find_Next");
                await app.WaitUntilAsync(async () => (await app.StateAsync())["statusOperationsVisible"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the progress");
                await app.UiaInvokeAsync("Status_Operations");
                await app.WaitForAsync("Operations_Cancel");
                return null;
            }

            default:
                return "unknown screen";
        }
    }

    /// <summary>画面を閉じて、次の画面の前の状態に戻す。</summary>
    private static async Task CloseOneAsync(AppSession app, string name)
    {
        switch (name)
        {
            case var _ when name.StartsWith("menu", StringComparison.Ordinal):
                if (MenusAllowed)
                {
                    foreach (AutomationElement menu in TopMenus(app))
                    {
                        if (menu.Patterns.ExpandCollapse.PatternOrDefault is { } pattern && pattern.ExpandCollapseState.ValueOrDefault != FlaUI.Core.Definitions.ExpandCollapseState.Collapsed)
                        {
                            pattern.Collapse();
                        }
                    }

                    await app.IdleAsync();
                }

                break;
            case "palette":
                await app.SendAsync("paletteClose");
                break;
            case "findBar":
                await app.UiaInvokeAsync("Find_Close");
                break;
            case "goToBar":
                await app.KeyAsync("Home", ctrl: true);
                break;
            case "inspector":
                await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.inspector" });
                break;
            case "settings-advanced":
                // 最後のカテゴリの後で設定画面のタブを閉じる (Ctrl+W は選択中の文書を閉じるので使わない)。
                await app.SendAsync("closeToolPage", new JsonObject { ["id"] = "settings" });
                break;
            case "newSizeDialog":
                await CloseDialogAsync(app, "NewSizeDialog", "Common_Cancel");
                break;
            case "aboutDialog":
                await CloseDialogAsync(app, "AboutDialog", "Common_Close");
                break;
            case "closeDialog":
                await CloseDialogAsync(app, "CloseDialog", "Common_Cancel");
                break;
            case "processingCenter":
                (await app.WaitForAsync("Operations_Cancel")).Patterns.Invoke.Pattern.Invoke();
                break;
        }

        await app.IdleAsync();
    }

    private static List<AutomationElement> TopMenus(AppSession app) =>
        [.. (app.Window.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu")) ?? throw new InvalidOperationException("No menu bar.")).FindAllChildren()
            .Where(m => AppSession.NameOf(m) != "Test")];

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
