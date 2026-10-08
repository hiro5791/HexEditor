using System.Diagnostics;
using System.Text;
using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// 配布先への提出の準備 (10 の PKG-04 Microsoft Store、PKG-27 winget) とリリースチャネルの配布 (PKG-21 の仕様 3)。
/// 実際の提出は行わない (マニフェスト・スクリプト・ワークフローの定義を確かめる)。winget・Store への提出のテストケースは手動。
/// </summary>
public sealed class PublishingTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static (int ExitCode, string Output) RunScript(string script, params string[] args)
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
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '" + RepoFile(script) + "' " + string.Join(' ', args.Select(a => a.StartsWith('-') ? a : "'" + a.Replace("'", "''") + "'")) + "; exit $LASTEXITCODE" })
        {
            start.ArgumentList.Add(a);
        }

        start.Environment.Remove("PSModulePath");
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), $"{script} が終わりません。");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private string Sums(string version)
    {
        string path = _temp.Sub("SHA256SUMS.txt");
        File.WriteAllText(path, $"{new string('a', 64)}  HexEditor-{version}-x64-Setup.exe\n{new string('b', 64)}  HexEditor-{version}-arm64-Setup.exe\n{new string('c', 64)}  HexEditor-{version}-x64-portable.zip\n");
        return path;
    }

    // ---- PKG-27 winget ----

    [Fact]
    public void Winget_manifests_register_the_user_scope_installer_for_x64_and_arm64()
    {
        string output = _temp.Sub("out");
        (int exit, string log) = RunScript("build/winget-manifests.ps1", "-Version", "v1.2.0", "-Sha256Sums", Sums("1.2.0"), "-OutDir", output, "-ReleaseDate", "2026-10-08");
        Assert.True(exit == 0, log);
        Assert.Equal(["Hiroyura.HexEditor.installer.yaml", "Hiroyura.HexEditor.locale.en-US.yaml", "Hiroyura.HexEditor.locale.ja-JP.yaml", "Hiroyura.HexEditor.yaml"],
            Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        string installer = File.ReadAllText(Path.Combine(output, "Hiroyura.HexEditor.installer.yaml"));
        Assert.Contains("PackageIdentifier: Hiroyura.HexEditor", installer);
        Assert.Contains("PackageVersion: 1.2.0", installer);
        Assert.Contains("InstallerType: exe", installer);
        Assert.Contains("Scope: user", installer);
        Assert.Contains("Silent: --silent", installer);
        Assert.Contains("InstallerUrl: https://github.com/hiro5791/HexEditor/releases/download/v1.2.0/HexEditor-1.2.0-x64-Setup.exe", installer);
        Assert.Contains("InstallerSha256: " + new string('A', 64), installer);
        Assert.Contains("Architecture: arm64", installer);
        Assert.Contains("InstallerSha256: " + new string('B', 64), installer);
        Assert.DoesNotContain("{{", installer);
        Assert.DoesNotContain("PKG-27", installer);

        string en = File.ReadAllText(Path.Combine(output, "Hiroyura.HexEditor.locale.en-US.yaml"));
        Assert.Contains("Publisher: Hiroyura", en);
        Assert.Contains("ShortDescription: A hex editor", en);
        string ja = File.ReadAllText(Path.Combine(output, "Hiroyura.HexEditor.locale.ja-JP.yaml"), Encoding.UTF8);
        Assert.Contains("ShortDescription: どんな大きさの", ja);
    }

    [Fact]
    public void Winget_gets_no_preview_versions()
    {
        (int exit, string log) = RunScript("build/winget-manifests.ps1", "-Version", "1.3.0-preview.1", "-Sha256Sums", Sums("1.3.0-preview.1"), "-OutDir", _temp.Sub("out"));
        Assert.NotEqual(0, exit);
        Assert.Contains("stable versions only", log);
        (int missing, _) = RunScript("build/winget-manifests.ps1", "-Version", "1.4.0", "-Sha256Sums", Sums("1.3.0"), "-OutDir", _temp.Sub("out2"));
        Assert.NotEqual(0, missing);
    }

    [Fact]
    public void Winget_workflow_submits_stable_releases_and_opens_an_issue_on_failure()
    {
        string workflow = File.ReadAllText(RepoFile(".github/workflows/winget.yml"));
        Assert.Contains("types: [published]", workflow);
        Assert.Contains("github.event.release.prerelease == false", workflow);
        Assert.Contains("wingetcreate.exe update Hiroyura.HexEditor", workflow);
        Assert.Contains("--submit --token $env:WINGET_TOKEN", workflow);
        Assert.Contains("WINGET_TOKEN: ${{ secrets.WINGET_TOKEN }}", workflow);
        Assert.Contains("build/winget-manifests.ps1", workflow);
        Assert.Contains("if: failure()", workflow);
        Assert.Contains("gh issue create", workflow);

        // リリースの公開は取り消さない: release.yml とは別のワークフローで、公開の後に動く。
        Assert.DoesNotContain("wingetcreate", File.ReadAllText(RepoFile(".github/workflows/release.yml")));
    }

    // ---- PKG-21 リリースチャネル (配布) ----

    [Fact]
    public void Stable_releases_are_published_to_both_velopack_channels()
    {
        string publish = File.ReadAllText(RepoFile("build/publish.ps1"));
        Assert.Contains("$channel = \"win-$Arch-\" + $(if ($isStable) { 'stable' } else { 'preview' })", publish);
        Assert.Contains("'--channel', $channel,", publish);

        string release = File.ReadAllText(RepoFile(".github/workflows/release.yml"));
        Assert.Contains("$channel = \"win-$arch-\" + $(if ($pre) { 'preview' } else { 'stable' })", release);
        Assert.Contains("releases.win-$arch-preview.json", release);
        Assert.Contains("gh release upload '${{ github.ref_name }}' \"$feedDir/releases.win-$arch-preview.json\"", release);
        Assert.DoesNotContain("--channel \"win-${{ matrix.arch }}\"", release);
    }

    // ---- PKG-04 Microsoft Store ----

    [Fact]
    public void Store_listing_exists_in_english_and_japanese_and_explains_administrator_rights()
    {
        foreach ((string file, string admin) in new[] { ("build/store/listing/en-US.md", "Run as administrator"), ("build/store/listing/ja-JP.md", "管理者として実行") })
        {
            string text = File.ReadAllText(RepoFile(file));
            Assert.Contains(admin, text);
            Assert.Contains("HexEditor", text);
        }

        // Store に提出するパッケージはワークフローの成果物 (store-submission) にだけ置き、Releases には載せない。
        string release = File.ReadAllText(RepoFile(".github/workflows/release.yml"));
        Assert.Contains("name: store-submission", release);
        Assert.Contains("TC-PKG-04-01", File.ReadAllText(RepoFile("build/tests/Test-Artifacts.ps1")));
    }
}
