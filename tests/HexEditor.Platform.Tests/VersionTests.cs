using System.Diagnostics;
using System.Text.Json;
using HexEditor.Platform.Tests.Support;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>版の付け方 (PKG-28)。</summary>
public sealed class VersionTests
{
    [Theory]
    [Trait(TC, "TC-PKG-28-01")]
    [InlineData("1.3.0-preview.2", "1.3.2.0", ReleaseChannel.Preview)]
    [InlineData("1.3.0", "1.3.999.0", ReleaseChannel.Stable)]
    [InlineData("1.3.1", "1.3.1999.0", ReleaseChannel.Stable)]
    [InlineData("1.3.0-preview.998", "1.3.998.0", ReleaseChannel.Preview)]
    [InlineData("0.0.0-local", "0.0.0.0", ReleaseChannel.Preview)]
    public void MapsSemVerToMsixAndAssemblyVersions(string text, string msix, ReleaseChannel channel)
    {
        SemanticVersion v = SemanticVersion.Parse(text);
        Assert.Equal(msix, v.MsixVersion.ToString());
        Assert.Equal($"{v.Major}.{v.Minor}.0.0", v.AssemblyVersion.ToString());
        Assert.Equal(text, v.SemVer);
        Assert.Equal(channel, v.Channel);
    }

    [Theory]
    [Trait(TC, "TC-PKG-28-01")]
    [InlineData("1.3.65")]
    [InlineData("1.3.0-preview.999")]
    [InlineData("1.3.0-preview.0")]
    [InlineData("1.3")]
    [InlineData("v1.3.0")]
    [InlineData("1.3.0-beta.1")]
    [InlineData("01.3.0")]
    public void RejectsOutOfRangeOrMalformedVersions(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    [Trait(TC, "TC-PKG-28-01")]
    public void VersionOrderIsPreviewThenStableThenNextPatch()
    {
        Version preview = SemanticVersion.Parse("1.3.0-preview.2").MsixVersion;
        Version lastPreview = SemanticVersion.Parse("1.3.0-preview.998").MsixVersion;
        Version stable = SemanticVersion.Parse("1.3.0").MsixVersion;
        Version next = SemanticVersion.Parse("1.3.1-preview.1").MsixVersion;
        Assert.True(preview < lastPreview && lastPreview < stable && stable < next);
    }

    [Fact]
    public void InformationalVersionKeepsCommitHash()
    {
        SemanticVersion v = SemanticVersion.FromInformationalVersion("1.3.0-preview.2+abc1234");
        Assert.Equal("1.3.0-preview.2", v.SemVer);
        Assert.Equal("1.3.0-preview.2+abc1234", v.Informational);
        Assert.Equal("abc1234", v.Commit);
    }

    /// <summary>Directory.Build.props を HexVersion を変えて評価する (テストケースの手順 1・2)。</summary>
    [Theory]
    [Trait(TC, "TC-PKG-28-01")]
    [InlineData("1.3.0-preview.2", "1.3.2.0")]
    [InlineData("1.3.0", "1.3.999.0")]
    [InlineData("1.3.1", "1.3.1999.0")]
    [InlineData("1.3.0-preview.998", "1.3.998.0")]
    public void DirectoryBuildPropsComputesVersions(string hexVersion, string msix)
    {
        (int exit, string output) = EvaluateProps(hexVersion);
        Assert.True(exit == 0, output);
        JsonElement props = JsonDocument.Parse(output).RootElement.GetProperty("Properties");
        Assert.Equal(msix, props.GetProperty("HexMsixVersion").GetString());
        Assert.Equal(msix, props.GetProperty("FileVersion").GetString());
        Assert.Equal("1.3.0.0", props.GetProperty("AssemblyVersion").GetString());
        // Velopack の版 (publish.ps1 が HexVersion をそのまま渡す) と NuGet の Version。
        Assert.Equal(hexVersion, props.GetProperty("Version").GetString());
        Assert.Equal(hexVersion, props.GetProperty("InformationalVersion").GetString());
    }

    [Theory]
    [Trait(TC, "TC-PKG-28-01")]
    [InlineData("1.3.65", "64")]
    [InlineData("1.3.0-preview.999", "998")]
    [InlineData("1.3", "MAJOR.MINOR.PATCH")]
    public void DirectoryBuildPropsFailsTheBuildForInvalidVersions(string hexVersion, string message)
    {
        (int exit, string output) = EvaluateProps(hexVersion);
        Assert.NotEqual(0, exit);
        Assert.True(output.Contains(message, StringComparison.Ordinal), output);
    }

    private static (int ExitCode, string Output) EvaluateProps(string hexVersion)
    {
        using var temp = new TempFolder();
        string probe = temp.Sub("probe.proj");
        File.WriteAllText(probe, $"""
            <Project>
              <Import Project="{RepoFile("Directory.Build.props")}" />
              <Import Project="{RepoFile("Directory.Build.targets")}" />
            </Project>
            """);
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                "msbuild", probe, "-nologo", $"-p:HexVersion={hexVersion}", "-t:HexValidateVersion",
                "-getProperty:HexMsixVersion", "-getProperty:FileVersion", "-getProperty:AssemblyVersion",
                "-getProperty:Version", "-getProperty:InformationalVersion",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "dotnet msbuild が終わりません。");
        string output = stdout.Result + stderr.Result;
        if (process.ExitCode == 0)
        {
            // 成功時の標準出力は JSON だけ。
            output = output[output.IndexOf('{')..];
        }

        return (process.ExitCode, output);
    }
}
