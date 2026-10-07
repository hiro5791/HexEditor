using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// 配布のテストのスクリプト (build/tests/*.ps1。テスト方針 6.2) のテスト。スクリプトそのものは CI のランナーで配布物を
/// 入れて動かすので、ここでは、壊れていないこと (構文、ASCII、CI から呼ばれていること) と、インストールせずに
/// 確かめられる部分 (リリースの確認、Process Monitor の記録の読み取り) をテスト用のデータで確かめる。
/// </summary>
public sealed partial class DistributionScriptTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static IEnumerable<string> Scripts() =>
        Directory.EnumerateFiles(RepoFile("build/tests"), "*.ps1").Order(StringComparer.Ordinal);

    /// <summary>Windows PowerShell でコマンドを実行する (CI の shell: powershell と同じ)。</summary>
    private static (int ExitCode, string Output) RunPowerShell(string command, IDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + command + "; exit $LASTEXITCODE" })
        {
            start.ArgumentList.Add(a);
        }

        // CI の上で動かしても、インストールする部分が動かないようにする。
        // PowerShell 7 (CI の既定のシェル) から起動されると、その PSModulePath を引き継いで Windows PowerShell の標準のモジュール
        // (Get-FileHash など) が読み込めなくなる。既定の場所を使わせる。
        start.Environment.Remove("PSModulePath");
        start.Environment.Remove("GITHUB_ACTIONS");
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment.Remove("HEX_SIGNING_ENABLED");
        start.Environment.Remove("HEX_TEST_CASES");
        foreach ((string key, string? value) in environment ?? new Dictionary<string, string?>())
        {
            start.Environment[key] = value;
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), $"{command} が終わりません。");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    [Fact]
    public void ScriptsParseAndAreAscii()
    {
        // 構文の確認は Windows PowerShell の構文解析器で行う (CI の shell: powershell と同じ)。
        string list = string.Join(", ", Scripts().Select(Quote));
        (int exit, string output) = RunPowerShell(
            $"$bad = 0; foreach ($f in @({list})) {{ $e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$e); " +
            "foreach ($x in $e) { $bad++; 'PARSE ' + (Split-Path -Leaf $f) + ':' + $x.Extent.StartLineNumber + ' ' + $x.Message } }; $global:LASTEXITCODE = $bad");
        Assert.True(exit == 0, output);

        foreach (string script in Scripts())
        {
            byte[] bytes = File.ReadAllBytes(script);
            int index = Array.FindIndex(bytes, b => b > 0x7F);
            Assert.True(index < 0, $"{Path.GetFileName(script)} は ASCII 以外の文字を含みます (位置 {index})。Windows PowerShell は BOM のない UTF-8 を読み違えます。");
        }
    }

    [Fact]
    public void EveryTestScriptRunsInAWorkflow()
    {
        string workflows = File.ReadAllText(RepoFile(".github/workflows/ci.yml")) + File.ReadAllText(RepoFile(".github/workflows/release.yml"));
        foreach (string script in Scripts().Where(s => Path.GetFileName(s).StartsWith("Test-", StringComparison.Ordinal)))
        {
            Assert.Contains("build/tests/" + Path.GetFileName(script), workflows);
        }
    }

    /// <summary>スクリプトの Invoke-TestCase の ID は、テストケースの文書にある「自動」のテストケース。</summary>
    [Fact]
    public void EveryInvokedTestCaseIsAnAutomatedCase()
    {
        string cases = string.Join('\n', Directory.EnumerateFiles(RepoFile("docs/test/cases"), "*.md").Select(File.ReadAllText));
        foreach (string script in Scripts())
        {
            foreach (Match m in InvokeTestCase().Matches(File.ReadAllText(script)))
            {
                string id = m.Groups[1].Value;
                Match heading = Regex.Match(cases, $@"### {Regex.Escape(id)} [^\n]*\n(?:[^\n]*\n){{1,6}}?\| 種別 \| ([^|]+) \|");
                Assert.True(heading.Success, $"{Path.GetFileName(script)}: {id} がテストケースの文書にありません。");
                Assert.StartsWith("自動", heading.Groups[1].Value.Trim());
            }
        }
    }

    [GeneratedRegex(@"Invoke-TestCase '(TC-[A-Z]+-\d{2}-\d{2})'")]
    private static partial Regex InvokeTestCase();

    // ---- Test-Release.ps1 (release.yml の publish のジョブ) ----

    private const string Version = "1.2.0";

    /// <summary>署名のない PE ファイル (このテストのアセンブリ。Setup.exe・HexEditor.exe の代わり)。</summary>
    private static string UnsignedExe() => typeof(DistributionScriptTests).Assembly.Location;

    /// <summary>リリースのファイル・SHA256SUMS.txt・Store 提出用の成果物と、gh release view の JSON を作る。</summary>
    private (string ReleaseJson, string Assets, string Store) FakeRelease(bool draft, bool prerelease, IEnumerable<string>? extra = null)
    {
        string assets = _temp.Sub("assets");
        string store = _temp.Sub("store");
        Directory.CreateDirectory(assets);
        foreach (string arch in new[] { "x64", "arm64" })
        {
            File.Copy(UnsignedExe(), Path.Combine(assets, $"HexEditor-{Version}-{arch}-Setup.exe"), overwrite: true);
            string zip = Path.Combine(assets, $"HexEditor-{Version}-{arch}-portable.zip");
            using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(UnsignedExe(), "HexEditor/HexEditor.exe");
            }

            File.WriteAllText(Path.Combine(assets, $"HexEditor-{Version}-win-{arch}-full.nupkg"), "full");
            File.WriteAllText(Path.Combine(assets, $"releases.win-{arch}.json"), "{}");
            Directory.CreateDirectory(Path.Combine(store, arch));
            File.WriteAllText(Path.Combine(store, arch, $"HexEditor-{Version}-{arch}.msix"), "msix");
        }

        foreach (string name in extra ?? [])
        {
            File.WriteAllText(Path.Combine(assets, name), name);
        }

        using (ZipArchive bundle = ZipFile.Open(Path.Combine(store, $"HexEditor-{Version}.msixbundle"), ZipArchiveMode.Create))
        {
            bundle.CreateEntry($"HexEditor-{Version}-x64.msix");
            bundle.CreateEntry($"HexEditor-{Version}-arm64.msix");
        }

        string[] files = [.. Directory.EnumerateFiles(assets).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        File.WriteAllText(Path.Combine(assets, "SHA256SUMS.txt"), string.Concat(files.Select(f =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(assets, f)))).ToLowerInvariant() + "  " + f + "\n")));
        string json = _temp.Sub("release.json");
        string assetList = string.Join(",", files.Append("SHA256SUMS.txt").Select(f => $$"""{ "name": "{{f}}" }"""));
        string body = "## Which file should I download?\\n\\n| If you want to... | Download |\\n| --- | --- |\\n";
        File.WriteAllText(json, $$"""{ "isDraft": {{(draft ? "true" : "false")}}, "isPrerelease": {{(prerelease ? "true" : "false")}}, "body": "{{body}}", "assets": [{{assetList}}] }""");
        return (json, assets, store);
    }

    private (int ExitCode, string Output) RunReleaseTests(string tag, (string ReleaseJson, string Assets, string Store) release) =>
        RunPowerShell($"& {Quote(RepoFile("build/tests/Test-Release.ps1"))} -Tag {Quote(tag)} -StoreDir {Quote(release.Store)} " +
            $"-ReleaseJson {Quote(release.ReleaseJson)} -AssetsDir {Quote(release.Assets)} -PreviousStable $false -WorkDir {Quote(_temp.Sub("work"))}");

    [Fact]
    [Trait(TC, "TC-PKG-01-01")]
    [Trait(TC, "TC-PKG-26-01")]
    public void ReleaseTestsPassForACompleteStableRelease()
    {
        (int exit, string output) = RunReleaseTests("v" + Version, FakeRelease(draft: true, prerelease: false));
        Assert.True(exit == 0, output);
        foreach (string id in new[] { "TC-PKG-01-01", "TC-PKG-24-01", "TC-PKG-25-01", "TC-PKG-25-04", "TC-PKG-26-01", "TC-PKG-26-02" })
        {
            Assert.Contains("PASS " + id, output);
        }

        // 前回の安定版がなければ差分パッケージの確認は飛ばす (失敗にしない)。
        Assert.Contains("SKIP TC-PKG-24-04", output);
    }

    [Fact]
    [Trait(TC, "TC-PKG-25-04")]
    public void ReleaseTestsFailWhenTheReleaseHasAnMsix()
    {
        (int exit, string output) = RunReleaseTests("v" + Version, FakeRelease(draft: true, prerelease: false, extra: [$"HexEditor-{Version}-x64.msix"]));
        Assert.NotEqual(0, exit);
        Assert.Contains("FAIL TC-PKG-25-04", output);
    }

    [Fact]
    [Trait(TC, "TC-PKG-24-01")]
    public void ReleaseTestsFailWhenAStableReleaseIsNotADraft()
    {
        (int exit, string output) = RunReleaseTests("v" + Version, FakeRelease(draft: false, prerelease: false));
        Assert.NotEqual(0, exit);
        Assert.Contains("FAIL TC-PKG-24-01", output);
    }

    [Fact]
    [Trait(TC, "TC-PKG-26-02")]
    public void ReleaseTestsFailWhenAFileIsNotInTheChecksums()
    {
        var release = FakeRelease(draft: true, prerelease: false);
        File.AppendAllText(Path.Combine(release.Assets, $"HexEditor-{Version}-x64-portable.zip"), "changed");
        (int exit, string output) = RunReleaseTests("v" + Version, release);
        Assert.NotEqual(0, exit);
        Assert.Contains("FAIL TC-PKG-26-02", output);
    }

    // ---- ProcessMonitor.ps1 (TC-PKG-06-01、TC-PKG-08-04 の記録の読み取り) ----

    [Fact]
    [Trait(TC, "TC-PKG-08-04")]
    public void ProcessMonitorExportIsReducedToRegistryWritesOfTheGivenProcesses()
    {
        string csv = _temp.Sub("events.csv");
        File.WriteAllText(csv, string.Join("\r\n",
            "\"Time of Day\",\"Process Name\",\"PID\",\"Operation\",\"Path\",\"Result\",\"Detail\"",
            "\"1:00\",\"Update.exe\",\"10\",\"RegSetValue\",\"HKLM\\Software\\X\\Y\",\"ACCESS DENIED\",\"Type: REG_SZ\"",
            "\"1:00\",\"Update.exe\",\"10\",\"RegCreateKey\",\"HKCU\\Software\\A\",\"SUCCESS\",\"Desired Access: Read, Disposition: REG_OPENED_EXISTING_KEY\"",
            "\"1:00\",\"Update.exe\",\"10\",\"RegCreateKey\",\"HKCU\\Software\\B\",\"SUCCESS\",\"Desired Access: All Access, Disposition: REG_CREATED_NEW_KEY\"",
            "\"1:00\",\"Update.exe\",\"10\",\"RegQueryValue\",\"HKLM\\Software\\Z\",\"SUCCESS\",\"Length: 4\"",
            "\"1:00\",\"explorer.exe\",\"11\",\"RegSetValue\",\"HKLM\\Software\\E\",\"SUCCESS\",\"Type: REG_DWORD\"",
            "\"1:00\",\"HexEditor-1.0.0-x64-Setup.exe\",\"12\",\"RegDeleteValue\",\"HKCU\\Software\\C\",\"SUCCESS\",\"\"",
            "\"1:00\",\"HexEditor.exe\",\"13\",\"CreateFile\",\"C:\\x\",\"SUCCESS\",\"\""));
        (int exit, string output) = RunPowerShell(
            $". {Quote(RepoFile("build/tests/TestCase.ps1"))}; . {Quote(RepoFile("build/tests/ProcessMonitor.ps1"))}; " +
            $"$r = Read-RegistryWrites {Quote(csv)} '(?i)^(HexEditor.*Setup\\.exe|Update\\.exe|HexEditor\\.exe)$'; " +
            "'PROCESSES=' + (($r.Processes | Sort-Object) -join ','); $r.Writes | ForEach-Object { 'WRITE=' + $_.Process + ' ' + $_.Operation + ' ' + $_.Path }");
        Assert.True(exit == 0, output);
        string[] writes = [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("WRITE=", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        Assert.Equal(
            [
                @"WRITE=HexEditor-1.0.0-x64-Setup.exe RegDeleteValue HKCU\Software\C",
                @"WRITE=Update.exe RegCreateKey HKCU\Software\B",
                @"WRITE=Update.exe RegSetValue HKLM\Software\X\Y",
            ],
            writes);
        Assert.Contains("PROCESSES=HexEditor-1.0.0-x64-Setup.exe,Update.exe", output);
    }

    // ---- アプリ側の補助 ----

    /// <summary>アプリがログに書く AppUserModelID (TC-PKG-12-03 が Test-Distributions.ps1 で比べる値) の取得。</summary>
    [Fact]
    public void ExplicitAppUserModelIdCanBeReadBack()
    {
        Assert.True(ProcessIdentity.TrySetAppUserModelId("HexEditor.Platform.Tests"));
        Assert.Equal("HexEditor.Platform.Tests", ProcessIdentity.TryGetAppUserModelId());
    }
}
