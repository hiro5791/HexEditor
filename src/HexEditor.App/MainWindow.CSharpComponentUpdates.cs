using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting.CSharp;

namespace HexEditor.App;

/// <summary>
/// アプリの更新に合わせた C# スクリプト コンポーネントの更新 (10 の PKG-17 の仕様 7): 追加済みのコンポーネントが古い版だけなら、更新後の
/// 初回起動時に新しい版をダウンロードする (<c>update.checkAutomatically</c> が true でダウンロードが許されているとき。false なら次に
/// C# スクリプトを使うときに追加を案内する)。古い版のフォルダは新しい版を入れた後に消す (<see cref="CSharpComponentManager.InstallFromZipAsync"/>)。
/// </summary>
public sealed partial class MainWindow
{
    private static bool s_componentUpdateStarted;

    /// <summary>コンポーネントの更新の状態 (テスト用の状態の表示): 実行中か、最後の失敗の理由。</summary>
    private static bool s_componentUpdating;
    private static string? s_componentUpdateError;

    /// <summary>起動時の通知の後に呼ぶ。プロセスで 1 回だけ。</summary>
    private void UpdateCSharpComponentAfterAppUpdate()
    {
        if (s_componentUpdateStarted)
        {
            return;
        }

        s_componentUpdateStarted = true;

        // コンポーネントを一度も追加していなければ何もしない (起動時にスクリプトの仕組みを作らない)。
        if (!Directory.Exists(Path.Combine(Hosting.Program.Environment.Locations.Components, "csharp")))
        {
            return;
        }

        CSharpComponentManager components;
        try
        {
            components = ScriptingService.Components;
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warning($"C# component: not checked at start-up ({ex.Message})");
            return;
        }

        if (!components.NeedsUpdate || !AppUpdates.Preferences.CheckAutomatically || !components.DownloadsAllowed())
        {
            return;
        }

        AppLog.Info($"C# component: updating from {string.Join(", ", components.InstalledVersions)} to {components.Manifest?.Version}");
        _ = InstallComponentInBackgroundAsync(components);
    }

    /// <summary>バックグラウンドで入れる (処理センターに進捗を出す。失敗は InfoBar)。</summary>
    private async Task InstallComponentInBackgroundAsync(CSharpComponentManager components)
    {
        s_componentUpdating = true;
        s_componentUpdateError = null;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("CSharpComponent_Operation"), Core.Operations.OperationKind.ReadOnly, null, 1000, async op =>
            {
                var progress = new Progress<double>(f => op.Report((long)(Math.Clamp(f, 0, 1) * 1000)));
                await components.InstallAsync(progress, op.CancellationToken);
            });
            AppLog.Info("C# component: updated");
        }
        catch (CSharpComponentException ex)
        {
            s_componentUpdateError = ex.Message;
            ShowNotice(Loc.Format(ex.ManualInstructions ? "CSharpComponent_FailedManual" : "CSharpComponent_Failed", ex.Message),
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            s_componentUpdateError = "cancelled";
        }
        finally
        {
            s_componentUpdating = false;
        }
    }

#if HEX_TEST_HOOKS
    /// <summary>
    /// テスト用の命令 <c>csharpComponent</c>: コンポーネントの状態 (置いてある版・フォルダ・実行中・最後の失敗)。<c>action</c> が <c>install</c> なら、
    /// 追加の確認ダイアログの「ダウンロード」と同じ処理をバックグラウンドで始める (配布のテスト TC-PKG-17-05)。
    /// </summary>
    private System.Text.Json.Nodes.JsonObject TestCSharpComponent(System.Text.Json.Nodes.JsonObject request)
    {
        CSharpComponentManager components = ScriptingService.Components;
        if (request["action"]?.GetValue<string>() == "install" && !s_componentUpdating)
        {
            _ = InstallComponentInBackgroundAsync(components);
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["folder"] = components.Folder,
            ["version"] = components.Manifest?.Version,
            ["package"] = components.PackageName(Hosting.Program.Environment.AppVersion, Hosting.Program.Environment.ProcessArchitecture),
            ["installed"] = components.IsInstalled,
            ["needsUpdate"] = components.NeedsUpdate,
            ["versions"] = new System.Text.Json.Nodes.JsonArray([.. components.InstalledVersions.Select(v => (System.Text.Json.Nodes.JsonNode?)v)]),
            ["busy"] = s_componentUpdating,
            ["error"] = s_componentUpdateError,
        };
    }
#endif
}
