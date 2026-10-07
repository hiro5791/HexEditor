using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// リリースのワークフローが使うスクリプト (build/*.ps1) のテスト。ワークフローの中と同じスクリプトを、テスト用の成果物で動かす。
/// </summary>
public sealed class BuildScriptTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static (int ExitCode, string Output) RunScript(string script, IDictionary<string, string?>? environment, params string[] args)
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
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '" + RepoFile(script) + "' " + string.Join(' ', args.Select(Quote)) + "; exit $LASTEXITCODE" })
        {
            start.ArgumentList.Add(a);
        }

        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment.Remove("HEX_SIGNING_ENABLED");
        foreach ((string key, string? value) in environment ?? new Dictionary<string, string?>())
        {
            start.Environment[key] = value;
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), $"{script} が終わりません。");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string Quote(string arg) => arg.StartsWith('-') ? arg : "'" + arg.Replace("'", "''") + "'";

    private string Artifact(string relative, long length)
    {
        string path = _temp.Sub("artifacts", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create);
        file.SetLength(length);
        return path;
    }

    private void SmallArtifacts()
    {
        foreach (string arch in new[] { "x64", "arm64" })
        {
            Artifact($"Portable/{arch}/HexEditor-1.0.0-{arch}-portable.zip", 60L << 20);
            Artifact($"Msix/{arch}/HexEditor-1.0.0-{arch}.msix", 60L << 20);
            Artifact($"Installer/{arch}/HexEditor-1.0.0-{arch}-Setup.exe", 70L << 20);
        }
    }

    [Fact]
    [Trait(TC, "TC-PKG-16-01")]
    public void SizeSummaryHasTheTableWithPreviousReleaseAndTargets()
    {
        SmallArtifacts();
        string previous = _temp.Sub("previous.json");
        File.WriteAllText(previous, """{ "x64": { "portable zip": 52428800 }, "arm64": {} }""");
        string summary = _temp.Sub("summary.md");
        (int exit, string output) = RunScript("build/measure-size.ps1", new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = summary },
            "-ArtifactsDir", _temp.Sub("artifacts"), "-PreviousJson", previous);
        Assert.True(exit == 0, output);
        string table = File.ReadAllText(summary);
        foreach (string item in new[] { "portable zip", "msix", "Setup.exe", "installed size", "delta package" })
        {
            Assert.Contains($"| {item} | x64 |", table);
            Assert.Contains($"| {item} | arm64 |", table);
        }

        Assert.Contains("| portable zip | x64 | 60.0 MB | 50.0 MB | 100 MB | ok |", table);
        Assert.Contains("Previous release", table);
    }

    [Fact]
    [Trait(TC, "TC-PKG-16-02")]
    public void SizeTenPercentOverTheTargetFailsUnlessApproved()
    {
        SmallArtifacts();
        // TD-PKG-PAD-50M を同梱したのと同じく、ポータブル版の zip が 50 MB 増えた (110 MB 以上は目標 100 MB の 10% 超え)。
        Artifact("Portable/x64/HexEditor-1.0.0-x64-portable.zip", 110L << 20);
        (int exit, string output) = RunScript("build/measure-size.ps1", null, "-ArtifactsDir", _temp.Sub("artifacts"));
        Assert.NotEqual(0, exit);
        Assert.Contains("portable zip (x64)", output);

        (int approved, string warning) = RunScript("build/measure-size.ps1", null, "-ArtifactsDir", _temp.Sub("artifacts"), "-WarnOnly");
        Assert.True(approved == 0, warning);
        Assert.Contains("::warning::", warning);
    }

    [Fact]
    [Trait(TC, "TC-PKG-26-02")]
    public void ChecksumsMatchGetFileHashInSha256sumFormat()
    {
        string dir = _temp.Sub("release");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "HexEditor-1.0.0-x64-Setup.exe"), "setup");
        File.WriteAllBytes(Path.Combine(dir, "HexEditor-1.0.0-x64-portable.zip"), [1, 2, 3, 0xFF]);
        File.WriteAllText(Path.Combine(dir, "releases.win-x64.json"), "{}");
        (int exit, string output) = RunScript("build/checksums.ps1", null, "-Dir", dir);
        Assert.True(exit == 0, output);

        string[] lines = File.ReadAllText(Path.Combine(dir, "SHA256SUMS.txt")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        foreach (string line in lines)
        {
            Assert.Matches("^[0-9a-f]{64}  [^ ].*$", line);
            string name = line[66..];
            string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, name)))).ToLowerInvariant();
            Assert.Equal(expected, line[..64]);
        }

        Assert.DoesNotContain(lines, l => l.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TC, "TC-PKG-25-02")]
    public void GeneratedReleaseNotesStartWithTheFileTableAndExplainSmartScreen()
    {
        string changelog = _temp.Sub("CHANGELOG.md");
        File.WriteAllText(changelog, """
            # Changelog

            ## [Unreleased]

            ## [1.2.0] - 2026-10-07

            ### Added

            - Something new.

            ### Known Issues

            - Something known.

            ## [1.1.0] - 2026-09-01

            ### Fixed

            - Old fix.
            """);
        string notes = _temp.Sub("notes.md");
        (int exit, string output) = RunScript("build/release-notes.ps1", null, "-Version", "v1.2.0", "-Changelog", changelog, "-OutFile", notes);
        Assert.True(exit == 0, output);
        string text = File.ReadAllText(notes);
        Assert.StartsWith("## Which file should I download?", text);
        Assert.Contains("HexEditor-1.2.0-x64-Setup.exe", text);
        Assert.Contains("HexEditor-1.2.0-x64-portable.zip", text);
        Assert.Contains("- Something new.", text);
        Assert.Contains("## Known issues", text);
        Assert.DoesNotContain("Old fix", text);
        Assert.DoesNotContain("{{", text);
        foreach (string topic in new[] { "SmartScreen", "More info", "SHA256SUMS.txt", "Microsoft Store", "Run as administrator", "Get-FileHash" })
        {
            Assert.Contains(topic, text);
        }
    }

    [Fact]
    public void ReleaseNotesFailWithoutAChangelogSection()
    {
        string changelog = _temp.Sub("CHANGELOG.md");
        File.WriteAllText(changelog, "# Changelog\n\n## [1.1.0]\n\n- x\n");
        (int exit, string output) = RunScript("build/release-notes.ps1", null, "-Version", "1.2.0", "-Changelog", changelog);
        Assert.NotEqual(0, exit);
        Assert.Contains("1.2.0", output);
    }

    [Fact]
    [Trait(TC, "TC-PKG-25-01")]
    public void SigningIsDisabledByDefaultAndNeedsNoSecrets()
    {
        string dir = _temp.Sub("publish");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "HexEditor.exe"), "x");
        (int exit, string output) = RunScript("build/sign.ps1", null, "-Path", dir);
        Assert.True(exit == 0, output);
        Assert.Contains("コード署名: 無効", output);

        (int enabled, _) = RunScript("build/sign.ps1", new Dictionary<string, string?> { ["HEX_SIGNING_ENABLED"] = "true" }, "-Path", dir);
        Assert.NotEqual(0, enabled);
    }
}
