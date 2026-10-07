using System.Text;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>アプリを起動して確かめる結合テスト (ENG-17 の一時ファイル、UI-57 のログ、PKG-11 の単一インスタンス)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class IntegrationTests
{
    /// <summary>復旧用データを定期的には書かない (テストが明示的に書く)。</summary>
    private static readonly JsonObject NoTimer = new() { ["recoveryIntervalSeconds"] = 3600 };

    /// <summary>確認のダイアログのボタンを名前で探して押す (UI オートメーションの Invoke。マウスは使わない)。</summary>
    private static async Task PressDialogButtonAsync(AppSession app, string name)
    {
        AutomationElement? button = null;
        await app.WaitUntilAsync(() => Task.FromResult((button = app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => AppSession.NameOf(b) == name)) is not null), TimeSpan.FromSeconds(15), $"the '{name}' button");
        button!.Patterns.Invoke.Pattern.Invoke();
    }

    [Theory]
    [Trait(UiTest.TC, "TC-ENG-17-03")]
    [InlineData("dontSave")]
    [InlineData("save")]
    [InlineData("exit")]
    public Task No_temporary_files_remain_after_closing(string how) => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-SEQ-1M のコピーを開く。設定「追加データをメモリに置く上限」(16 MiB) は設定画面が未実装のため既定 (256 MiB) のまま。
        // 追加バッファの一時ファイル (add.bin) は復旧用データの書き出しでも作られる (ENG-27 の仕様 2)。
        string path = ctx.CopyTestData("TD-SEQ-1M", $"close-{how}.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = NoTimer });

        // 手順 1: 32 MiB のデータを挿入貼り付けし、復旧用データを書く (70 秒待つ代わりに、保存間隔の処理をすぐ行う)。
        JsonObject inserted = await app.SendAsync("insertBytes", new JsonObject
        {
            ["offset"] = 0, ["length"] = 32 * TestDataCatalog.MiB, ["seed"] = 1703,
        });
        string folder = Path.Combine(app.RecoveryFolder, inserted["documentId"]!.GetValue<string>());
        await app.SendAsync("writeRecovery");

        // 手順 2: add.bin と復旧用データがある。
        Assert.True(File.Exists(Path.Combine(folder, "add.bin")), $"add.bin がありません: {folder}");
        Assert.True(File.Exists(Path.Combine(folder, "state.json")), $"state.json がありません: {folder}");
        Assert.True(new FileInfo(Path.Combine(folder, "add.bin")).Length >= 32 * TestDataCatalog.MiB);

        // 手順 3・5: Ctrl+W で「保存しない」/「保存」、またはファイル > 終了 で「保存しない」。
        switch (how)
        {
            case "dontSave":
                await app.KeyAsync("W", ctrl: true);
                await PressDialogButtonAsync(app, "Don't save");
                break;
            case "save":
                await app.KeyAsync("W", ctrl: true);
                await PressDialogButtonAsync(app, "Save");
                break;
            default:
                await app.CommandAsync("Command_Exit");
                await PressDialogButtonAsync(app, "Don't save");

                // 終了は通常 0.5 秒以内だが、UI オートメーションのクライアント (このテスト) が要素を参照していると、まれに数十秒かかる
                // (CrashAndRecoveryTests と同じく長めに待つ)。
                await app.WaitForExitAsync(TimeSpan.FromSeconds(90));
                break;
        }

        if (how != "exit")
        {
            await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 0, TimeSpan.FromSeconds(30), "the tab to close");
        }

        // 手順 4: そのドキュメントの recovery/<ドキュメント ID>/ が残っていない。
        await app.WaitUntilAsync(() => Task.FromResult(!Directory.Exists(folder)), TimeSpan.FromSeconds(10), $"{folder} to be removed");
        if (how == "save")
        {
            Assert.Equal(TestDataCatalog.MiB + 32 * TestDataCatalog.MiB, new FileInfo(path).Length);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-57-02")]
    public Task Logs_do_not_contain_file_content() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: 既定の設定 (log.level = info)。TD-UI-SECRET を secret-content.bin の名前で置く。
        string path = ctx.CopyTestData("TD-UI-SECRET", "secret-content.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        // 手順 1: オフセット 0 を選択し、検索・保存・閉じる・終了を行う。コピーはシステムのクリップボードを書き換えるため行わない
        // (ログに書くのはアプリの処理で、コピーの処理はクリップボードの内容をログに書かない)。
        await app.SelectAsync(0, TestDataCatalog.SecretMarker.Length);
        Assert.Equal("menu:Command_Find", (await app.KeyAsync("F", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = "ASCII" });
        await app.UiaSetValueAsync("Find_Query", "SECRET-7F3A");
        await app.UiaInvokeAsync("Find_Next");
        await app.IdleAsync();
        await app.UiaInvokeAsync("Find_Close");

        // 保存: オフセット 0 に同じ値 (48) を上書きして Ctrl+S (その場保存)。
        await app.GoToAsync(0);
        await app.TypeAsync("48");
        await app.KeyAsync("S", ctrl: true);
        await app.IdleAsync();
        Assert.False((await app.DocumentAsync())["modified"]!.GetValue<bool>());
        await app.KeyAsync("W", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 0, TimeSpan.FromSeconds(10), "the tab to close");
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(90));

        // 手順 2・3: logs\ 以下のすべてのファイルに、目印の文字列 (ASCII、UTF-16 LE、Hex の空白区切りと区切りなし) とファイル名がない。
        string logs = Path.Combine(app.Profile, "logs");
        string[] files = Directory.Exists(logs) ? Directory.GetFiles(logs, "*", SearchOption.AllDirectories) : [];
        Assert.NotEmpty(files);
        string marker = TestDataCatalog.SecretMarker;
        string hex = Convert.ToHexString(Encoding.ASCII.GetBytes(marker));
        string spaced = string.Join(" ", Encoding.ASCII.GetBytes(marker).Select(b => b.ToString("X2")));
        byte[][] needles =
        [
            Encoding.ASCII.GetBytes(marker),
            Encoding.Unicode.GetBytes(marker),
            Encoding.ASCII.GetBytes(hex),
            Encoding.ASCII.GetBytes(hex.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(spaced),
            Encoding.ASCII.GetBytes(spaced.ToLowerInvariant()),
            Encoding.UTF8.GetBytes("secret-content.bin"),
            Encoding.Unicode.GetBytes("secret-content.bin"),
        ];
        foreach (string file in files)
        {
            byte[] content = File.ReadAllBytes(file);
            foreach (byte[] needle in needles)
            {
                Assert.True(content.AsSpan().IndexOf(needle) < 0, $"{file} に {Encoding.UTF8.GetString(needle)} が含まれています。");
            }
        }
    });

    [Fact(Skip = "管理者権限のプロセスを UAC の確認なしで起動できない (作業中の PC では昇格しない)。CI の管理者のランナーで、制限付きトークンの一般の" +
        "プロセスと並べて行う。インスタンスのキーが権限で分かれることは HexEditor.Platform.Tests の EnvironmentTests で確かめている")]
    [Trait(UiTest.TC, "TC-PKG-11-03")]
    public Task Normal_and_elevated_instances_run_side_by_side() => Task.CompletedTask;
}
