using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.Core.Plugins;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Plugins;

/// <summary>
/// プラグイン SDK のパッケージと dotnet new のテンプレート (08 の AUTO-29、AUTO-33)。テンプレートはこのリポジトリの
/// src/HexEditor.Sdk.Templates をパックしたもので、インストールは一時的なフォルダ (dotnet new の custom hive) にだけ行う
/// (利用者の dotnet new の設定には入れない)。パッケージはローカルのフォルダのフィードだけから取り、ダウンロードしない
/// (nuget.config で他のフィードを外し、テストのひな形の依存はこの PC のパッケージのキャッシュを読み取り専用で使う)。
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class TemplatePackageTests
{
    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "HexEditor.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("The repository root is not found.");
    }

    private static string TemplateFolder => Path.Combine(RepositoryRoot(), "src", "HexEditor.Sdk.Templates", "templates", "hexeditor-plugin");

    [Fact]
    [Trait("TC", "TC-AUTO-33-01")]
    public void The_template_launch_profile_starts_HexEditor_with_the_build_output_as_a_development_folder()
    {
        // 手順 1: 起動設定 (launchSettings.json) の起動するプログラムと引数。デバッガの下での起動とブレークポイント (手順 2〜4) は、
        // ICorDebug を使うテスト用のデバッガがないため自動化しない (用意するにはパッケージのダウンロードが要る)。
        JsonObject profile = JsonNode.Parse(File.ReadAllText(Path.Combine(TemplateFolder, "HexEditorPlugin1", "Properties", "launchSettings.json")))!
            ["profiles"]!.AsObject().Single().Value!.AsObject();
        Assert.Equal("Executable", profile["commandName"]!.GetValue<string>());
        Assert.Equal("$(HexEditorExe)", profile["executablePath"]!.GetValue<string>());
        string args = profile["commandLineArgs"]!.GetValue<string>();
        Assert.Contains("--plugin-dev \"$(TargetDir)\"", args, StringComparison.Ordinal);
        string csproj = File.ReadAllText(Path.Combine(TemplateFolder, "HexEditorPlugin1", "HexEditorPlugin1.csproj"));
        Assert.Contains(@"\HexEditor\current\HexEditor.exe</HexEditorExe>", csproj, StringComparison.Ordinal);

        // ひな形のマニフェストは、この SDK と同じバージョンを宣言し、参照する SDK のパッケージも同じバージョン。
        PluginManifest manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(TemplateFolder, "HexEditorPlugin1", "plugin.json")));
        Assert.Equal(PluginHostOptions.CurrentSdkVersion, manifest.Sdk);
        Assert.Contains($"Include=\"HexEditor.Sdk\" Version=\"{PluginHostOptions.CurrentSdkVersion.Major}.{PluginHostOptions.CurrentSdkVersion.Minor}.0\"", csproj, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) Run(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        // 利用者の設定を読み書きしない: テレメトリーと初回の案内を止める。
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet " + string.Join(' ', args));
        }

        return (process.ExitCode, output.Result + error.Result);
    }

    private static void Ok(string workingDirectory, params string[] args)
    {
        (int code, string output) = Run(workingDirectory, args);
        Assert.True(code == 0, $"dotnet {string.Join(' ', args)} failed ({code}):\n{output}");
    }

    [Fact]
    [Trait("TC", "TC-AUTO-29-01")]
    [Trait("Category", "Nightly")]
    public async Task The_dotnet_new_template_builds_tests_and_loads_in_the_app()
    {
        string root = Path.Combine(Path.GetTempPath(), "HexEditorTests", "template-" + Guid.NewGuid().ToString("N")[..8]);
        string feed = Path.Combine(root, "feed");
        string hive = Path.Combine(root, "hive");
        string work = Path.Combine(root, "work");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(work);
        string repo = RepositoryRoot();

        // SDK・テスト用の部品・テンプレートをローカルのフィードにパックする。
        foreach (string project in new[] { "HexEditor.Sdk", "HexEditor.Sdk.Testing", "HexEditor.Sdk.Templates" })
        {
            Ok(repo, "pack", Path.Combine(repo, "src", project, project + ".csproj"), "-c", "Release", "-o", feed, "-p:HexTestHooks=false");
        }

        // 1. dotnet new install (一時的なフォルダの hive に)。2. dotnet new hexeditor-plugin -n TplTest。
        string templatePackage = Directory.GetFiles(feed, "HexEditor.Sdk.Templates.*.nupkg").Single();
        Ok(work, "new", "install", templatePackage, "--debug:custom-hive", hive);
        Ok(work, "new", "hexeditor-plugin", "-n", "TplTest", "-o", Path.Combine(work, "TplTest"), "--debug:custom-hive", hive);
        string solution = Path.Combine(work, "TplTest");
        File.WriteAllText(Path.Combine(solution, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{Path.Combine(root, "packages")}" />
              </config>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
              </packageSources>
              <fallbackPackageFolders>
                <clear />
                <add key="cache" value="{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")}" />
              </fallbackPackageFolders>
            </configuration>
            """);

        // 3. 警告をエラーとして扱う設定 (ひな形の csproj) のまま、ビルドとテストが通る。
        Ok(solution, "build", "-c", "Release");
        Ok(solution, "test", "-c", "Release", "--no-build");

        // 4. ビルドの出力を .hexplugin にまとめ、プラグインマネージャから追加して再起動する (信頼の確認で「有効にする」)。
        string output = Path.Combine(solution, "TplTest", "bin", "Release", "net10.0");
        Assert.False(File.Exists(Path.Combine(output, "HexEditor.Sdk.dll")), "the SDK must not be copied to the plugin output");
        string package = TestPlugins.Package(output, Path.Combine(root, "TplTest.hexplugin"));
        using var t = new PluginTestHost();
        PluginManager m = await t.StartAsync();
        PluginInstallResult installed = await m.InstallAsync(package);
        Assert.True(installed.Installed, installed.Message);
        await t.StartAsync();

        // 5. 状態が「有効」、ひな形が登録するコマンドが一覧に出る。
        PluginRecord p = t.Manager!.Find("tpltest")!;
        Assert.Equal(PluginState.Enabled, p.State);
        RegisteredExtension command = Assert.Single(p.Extensions);
        Assert.Equal("tpltest.hello", command.Id);
        Assert.Equal(PluginExtensionKinds.Command, command.Kind);
        Assert.True(t.Execute("tpltest.hello"));
        Assert.Contains("Hello from TplTest (no bytes)", t.OutputOf("TplTest"));
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時フォルダなので残っても構わない。
        }
    }
}
