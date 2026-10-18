using System.Diagnostics;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 自己適用型パッチ (EDIT-44) を作り、適用プログラムを UI オートメーションで操作して当てる (TC-EDIT-44-01・TC-EDIT-44-02)。
/// 適用プログラムは NativeAOT の発行が要るため (C++ のビルドツール)、CI のジョブだけが HEXEDITOR_PATCH_STUB_DIR に発行したもの
/// (PatchApplier\&lt;arch&gt;\HexPatchApply.stub) を置いて動かす。適用プログラムはメッセージボックスとファイルのダイアログを前面に出すため、
/// 開発者の PC では動かさない (GitHub Actions のときだけ)。作った実行ファイルはテストの一時フォルダの中でだけ動かす。
/// </summary>
public sealed class SelfPatchFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_PATCH_STUB_DIR";

    public SelfPatchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            Skip = "適用プログラムは前面にダイアログを出すため、CI (GitHub Actions) だけで動かす。";
        }
        else if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } dir || !Directory.Exists(dir))
        {
            Skip = $"{Variable} がない (適用プログラムを発行する CI のジョブだけで動く)。";
        }
    }
}

[Trait(UiTest.Category, UiTest.UI)]
public sealed class SelfPatchTests
{
    /// <summary>手順 1: TD-EDIT-PATCH-SRC の 3 か所を書き換え、既定のファイル名 target.bin の自己適用型パッチ patch.exe を作る。作成時の表示を返す。</summary>
    private static async Task<(string Exe, string SmartScreen)> CreateAsync(UiTestContext ctx)
    {
        byte[] mod3 = File.ReadAllBytes(ctx.TestData("TD-EDIT-PATCH-MOD3"));
        string exe = Path.Combine(ctx.Root, "patch.exe");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-EDIT-PATCH-SRC", "source.bin")] });
        foreach (long at in HexEditor.TestData.TestDataCatalog.PatchMod3Offsets)
        {
            await app.GoToAsync(at);
            await app.TypeAsync(mod3[at].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            await app.IdleAsync();
        }

        await app.CommandAsync("Command_PatchCreate");
        await app.WaitForAsync("PatchCreateDialog");
        await app.WaitUntilAsync(async () => !(await app.SendAsync("patchDialog"))["computing"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(60)), "the estimates");
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "PatchCreate_Format_SelfApplying", ["value"] = true });
        await app.UiaSetValueAsync("PatchCreate_DefaultFileName", "target.bin");
        await app.UiaSetValueAsync("PatchCreate_Path", exe);
        await app.IdleAsync();
        string smartScreen = (await app.SendAsync("patchDialog"))["smartScreen"]!.GetValue<string>();
        await EditCommandTests.PressAsync(app);
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(exe)), UiTest.Scaled(TimeSpan.FromSeconds(60)), "patch.exe");
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));
        return (exe, smartScreen);
    }

    /// <summary>適用プログラムのウィンドウ (メッセージボックス・ファイルのダイアログ) を待つ。</summary>
    private static async Task<Window> WaitWindowAsync(Process process, Func<Window, bool> match, string what)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < UiTest.Scaled(TimeSpan.FromSeconds(30)))
        {
            var app = FlaUI.Core.Application.Attach(process);
            foreach (Window w in app.GetAllTopLevelWindows(AppSession.Automation))
            {
                if (match(w))
                {
                    return w;
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(what);
    }

    private static string AllText(Window w) =>
        string.Join("\n", w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name));

    private static void Press(Window w, string automationId) => w.FindFirstDescendant(cf => cf.ByAutomationId(automationId))!.AsButton().Invoke();

    /// <summary>手順 3: patch.exe を起動し、対象ファイルに target.bin を選ぶ。</summary>
    private static async Task<(Process Process, Window Next)> StartAndSelectAsync(string exe, string target)
    {
        Process process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })!;
        Window intro = await WaitWindowAsync(process, w => w.FindFirstDescendant(cf => cf.ByAutomationId("1148")) is null, "the intro message");
        Press(intro, "1");
        Window open = await WaitWindowAsync(process, w => w.FindFirstDescendant(cf => cf.ByAutomationId("1148")) is not null, "the file dialog");
        open.FindFirstDescendant(cf => cf.ByAutomationId("1148"))!.AsComboBox().EditableText = target;
        Press(open, "1");
        Window next = await WaitWindowAsync(process, w => w.FindFirstDescendant(cf => cf.ByAutomationId("1148")) is null && !ReferenceEquals(w, open), "the next message");
        return (process, next);
    }

    [SelfPatchFact]
    [Trait(UiTest.TC, "TC-EDIT-44-01")]
    public Task Self_applying_patch_applies_to_the_target_with_a_backup() => UiTestContext.RunAsync(async ctx =>
    {
        (string built, string smartScreen) = await CreateAsync(ctx);
        Assert.Contains("SmartScreen", smartScreen, StringComparison.Ordinal);

        // 2. patch.exe と target.bin (TD-EDIT-PATCH-SRC) を同じフォルダに置く (テストの一時フォルダの中)。
        string folder = Path.Combine(ctx.Root, "apply");
        Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "patch.exe");
        File.Copy(built, exe);
        string target = Path.Combine(folder, "target.bin");
        File.Copy(ctx.TestData("TD-EDIT-PATCH-SRC"), target);

        // 3. 起動して target.bin を選び、バックアップを作る (既定) のまま適用する。
        (Process process, Window backup) = await StartAndSelectAsync(exe, target);
        Assert.Contains("backup", AllText(backup), StringComparison.OrdinalIgnoreCase);
        Press(backup, "6");

        // 4. 適用に成功した旨。
        Window done = await WaitWindowAsync(process, w => AllText(w).Contains("applied", StringComparison.Ordinal), "the result");
        done.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button))!.AsButton().Invoke();
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);

        // 5. target.bin は TD-EDIT-PATCH-MOD3、バックアップは TD-EDIT-PATCH-SRC と一致する。
        Assert.Equal(File.ReadAllBytes(ctx.TestData("TD-EDIT-PATCH-MOD3")), File.ReadAllBytes(target));
        Assert.Equal(File.ReadAllBytes(ctx.TestData("TD-EDIT-PATCH-SRC")), File.ReadAllBytes(target + ".bak"));
    }, TimeSpan.FromMinutes(4));

    [SelfPatchFact]
    [Trait(UiTest.TC, "TC-EDIT-44-02")]
    public Task Self_applying_patch_warns_for_a_different_file() => UiTestContext.RunAsync(async ctx =>
    {
        (string built, _) = await CreateAsync(ctx);
        string folder = Path.Combine(ctx.Root, "apply");
        Directory.CreateDirectory(folder);
        string exe = Path.Combine(folder, "patch.exe");
        File.Copy(built, exe);
        string target = Path.Combine(folder, "target.bin");
        File.Copy(ctx.TestData("TD-EDIT-PATCH-OTHER"), target);

        // 1〜3. target.bin を選ぶと、別のファイル用の可能性がある旨の警告と、適用するかの選択。キャンセル (いいえ) を選ぶ。
        (Process process, Window warning) = await StartAndSelectAsync(exe, target);
        Assert.Contains("different file", AllText(warning), StringComparison.Ordinal);
        Press(warning, "7");
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(1, process.ExitCode);

        // 4. target.bin は変わらない。
        Assert.Equal(File.ReadAllBytes(ctx.TestData("TD-EDIT-PATCH-OTHER")), File.ReadAllBytes(target));
    }, TimeSpan.FromMinutes(4));
}
