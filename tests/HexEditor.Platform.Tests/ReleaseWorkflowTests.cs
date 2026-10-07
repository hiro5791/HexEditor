using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// CI とリリースのワークフロー (PKG-23〜PKG-26)。テストケースはテスト用のリポジトリで PR やタグを実際に作って確かめるもので、
/// 次の 2 段で自動化する。
/// <list type="number">
/// <item>ワークフローの定義の検査 (常に実行): .github/workflows の ci.yml・release.yml が、各テストケースの期待結果を満たす作りになっているか。</item>
/// <item>テスト用のリポジトリの結果の確認 (<see cref="GitHubFactAttribute"/>): 環境変数 HEXEDITOR_GH_TEST_REPO (owner/name) と gh CLI の認証が
/// あるときだけ、テストケースの手順で作った PR・リリース・Issue を GitHub の API で読んで確かめる (読むだけで、PR やタグは作らない)。</item>
/// </list>
/// </summary>
public sealed partial class ReleaseWorkflowTests
{
    private static string Ci => File.ReadAllText(RepoFile(".github/workflows/ci.yml"));

    private static string Release => File.ReadAllText(RepoFile(".github/workflows/release.yml"));

    /// <summary>ワークフローの jobs の 1 つ (2 文字の字下げの「名前:」から次のジョブまで)。</summary>
    private static string Job(string workflow, string name)
    {
        Match m = Regex.Match(workflow, $@"(?ms)^  {Regex.Escape(name)}:\r?\n(.*?)(?=^  [a-z0-9-]+:\r?$|\z)");
        Assert.True(m.Success, $"ジョブ {name} がありません。");
        return m.Groups[1].Value;
    }

    [GeneratedRegex(@"(?m)^  ([a-z0-9-]+):\r?$")]
    private static partial Regex JobName();

    // ---- PKG-23: プルリクエストのジョブ ----

    [Fact]
    [Trait(TC, "TC-PKG-23-01")]
    public void PullRequestsRunTheSixJobs()
    {
        string ci = Ci;
        Assert.Matches(@"(?m)^\s+pull_request:", ci);
        string[] jobs = [.. JobName().Matches(ci[ci.IndexOf("\njobs:", StringComparison.Ordinal)..]).Select(m => m.Groups[1].Value)];
        foreach (string name in new[] { "build", "test-x64", "test-arm64", "i18n", "lint", "size" })
        {
            Assert.Contains(name, jobs);

            // PR で走る (毎晩だけのジョブではない)。
            Assert.DoesNotMatch(@"(?m)^    if: github\.event_name == 'schedule'", Job(ci, name));
        }

        // 20 分以内に終わるよう、PR で走るジョブには時間の上限を付ける。
        foreach (string name in jobs)
        {
            Assert.Matches(@"(?m)^    timeout-minutes: \d+", Job(ci, name));
        }
    }

    [Fact]
    [Trait(TC, "TC-PKG-23-02")]
    public void FailingUnitTestsFailTheTestJobs()
    {
        string ci = Ci;
        foreach (string name in new[] { "test-x64", "test-arm64" })
        {
            string job = Job(ci, name);
            Assert.Contains("dotnet test tests/HexEditor.Core.Tests", job, StringComparison.Ordinal);

            // テストの失敗でジョブが失敗する (失敗を無視しない)。ログに失敗したテスト名が出るよう、コンソールに詳しく出す。
            Assert.DoesNotContain("continue-on-error", job, StringComparison.Ordinal);
            Assert.DoesNotContain("|| true", job, StringComparison.Ordinal);
            Assert.Contains("console;verbosity=normal", job, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TC, "TC-PKG-23-03")]
    public void PullRequestsFromForksGetNoSecrets()
    {
        string ci = Ci;

        // pull_request_target は使わず (フォークのコードに Secrets を渡さない)、ci.yml は Secrets を参照しない。
        Assert.DoesNotContain("pull_request_target", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", ci, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^permissions:\r?\n  contents: read", ci);
        foreach (string name in new[] { "build", "test-x64", "test-arm64" })
        {
            Job(ci, name);
        }
    }

    // ---- PKG-24〜PKG-26: リリース ----

    [Fact]
    [Trait(TC, "TC-PKG-24-01")]
    public void StableTagsMakeADraftRelease()
    {
        string release = Release;
        Assert.Matches(@"(?ms)^on:\r?\n  push:\r?\n    tags: \[""v\*""\]", release);
        string publish = Job(release, "publish");
        Assert.Contains("--draft=true", publish, StringComparison.Ordinal);

        // 60 分以内に終わるよう、ジョブには時間の上限を付ける。
        foreach (string name in new[] { "prepare", "build", "bundle", "verify", "size", "publish" })
        {
            Assert.Matches(@"(?m)^    timeout-minutes: \d+", Job(release, name));
        }
    }

    [Fact]
    [Trait(TC, "TC-PKG-24-02")]
    public void PreviewTagsArePublishedAsPrereleases()
    {
        string release = Release;
        Assert.Contains("$pre = if ($version -match '-') { 'true' } else { 'false' }", Job(release, "prepare"), StringComparison.Ordinal);
        Assert.Contains("--prerelease --draft=false", Job(release, "publish"), StringComparison.Ordinal);

        // winget の提出 (publish.yml) は、プレビュー版では動かない (公開されたリリースのうち Pre-release でないもの)。
        string publishYml = RepoFile(".github/workflows/publish.yml");
        if (File.Exists(publishYml))
        {
            string text = File.ReadAllText(publishYml);
            Assert.Contains("prerelease", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TC, "TC-PKG-24-03")]
    public void FailedVerificationCreatesNoReleaseAndOpensAnIssue()
    {
        string release = Release;

        // 公開は検証の後で、検証が失敗したら走らない。途中の下書きは消し、Issue を作る。
        Assert.Matches(@"(?m)^    needs: \[[^\]]*\bverify\b[^\]]*\]", Job(release, "publish"));
        string verify = Job(release, "verify");
        Assert.Contains("Test-Installer.ps1", verify, StringComparison.Ordinal);
        Assert.Contains("Test-Portable.ps1", verify, StringComparison.Ordinal);
        Assert.Contains("Remove the partial draft", Job(release, "publish"), StringComparison.Ordinal);
        string report = Job(release, "report-failure");
        Assert.Contains("if: failure()", report, StringComparison.Ordinal);
        Assert.Contains("gh issue create", report, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  issues: write", release);
    }

    [Fact]
    [Trait(TC, "TC-PKG-24-04")]
    public void LaterReleasesBuildDeltaPackagesFromThePreviousRelease()
    {
        string build = Job(Release, "build");
        Assert.Contains("vpk download github", build, StringComparison.Ordinal);
        Assert.Contains("-PreviousReleaseDir previous", build, StringComparison.Ordinal);
        Assert.Contains("matrix.distro == 'Installer'", build, StringComparison.Ordinal);
        Assert.Contains("arch: [x64, arm64]", build, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-PKG-25-04")]
    public void GitHubReleasesHaveNoMsix()
    {
        string publish = Job(Release, "publish");
        Assert.Contains("$_.Extension -notin '.msix', '.msixbundle', '.appx', '.appxbundle'", publish, StringComparison.Ordinal);

        // GitHub Releases に上げるのは Setup.exe・portable.zip と、Velopack の更新用のファイルと、それらの SHA256SUMS.txt だけ。
        Assert.Contains("$_.Name -like '*-Setup.exe' -or $_.Name -like '*-portable.zip'", publish, StringComparison.Ordinal);
        Assert.Contains("gh release upload $tag sums/SHA256SUMS.txt", publish, StringComparison.Ordinal);
        // Store 提出用の成果物は、リリースの確認 (Test-Release.ps1) のためにダウンロードするだけで、アップロードはしない。
        foreach (string line in publish.Split('\n').Where(l => l.Contains("release upload", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("store-submission", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TC, "TC-PKG-26-01")]
    public void ReleaseFilesAreTheDocumentedSet()
    {
        string publish = Job(Release, "publish");
        // SHA256SUMS.txt はアップロードの後に、リリースに実際に載ったファイルから作る (PKG-26 の仕様 2)。
        Assert.Contains("gh release view $tag --json assets", publish, StringComparison.Ordinal);
        Assert.Contains("./build/checksums.ps1 -Dir sums", publish, StringComparison.Ordinal);
        Assert.Contains("vpk', 'upload', 'github'", publish, StringComparison.Ordinal);
        Assert.Contains("--notes-file release-notes.md", publish, StringComparison.Ordinal);
        Assert.Contains("foreach ($arch in 'x64', 'arm64')", publish, StringComparison.Ordinal);

        // C# スクリプト コンポーネントの zip (*-csharp.zip) は AUTO-03 (フェーズ 3) の実装時に加える (release.yml の冒頭の「まだないもの」)。
        Assert.Contains("C# スクリプト コンポーネントの zip (AUTO-03)", Release, StringComparison.Ordinal);
    }

    // ---- テスト用のリポジトリの結果 (読むだけ) ----

    /// <summary>
    /// TC-PKG-24-01・TC-PKG-25-04・TC-PKG-26-01: タグ v0.1.0 のリリースが下書きで、PKG-26 の表のファイルがあり、MSIX がない。
    /// </summary>
    [GitHubFact]
    [Trait(TC, "TC-PKG-24-01")]
    [Trait(TC, "TC-PKG-25-04")]
    [Trait(TC, "TC-PKG-26-01")]
    public void TestRepositoryStableReleaseIsADraftWithTheDocumentedFiles()
    {
        JsonElement release = GitHubFactAttribute.Api("releases").EnumerateArray().Single(r => r.GetProperty("tag_name").GetString() == "v0.1.0");
        Assert.True(release.GetProperty("draft").GetBoolean());
        string[] assets = [.. release.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString()!)];
        foreach (string name in new[]
        {
            "HexEditor-0.1.0-x64-Setup.exe", "HexEditor-0.1.0-arm64-Setup.exe",
            "HexEditor-0.1.0-x64-portable.zip", "HexEditor-0.1.0-arm64-portable.zip", "SHA256SUMS.txt",
        })
        {
            Assert.Contains(name, assets);
        }

        Assert.Contains(assets, a => a.EndsWith("-full.nupkg", StringComparison.Ordinal));
        Assert.Contains(assets, a => Regex.IsMatch(a, @"^releases\..+\.json$"));
        Assert.DoesNotContain(assets, a => Regex.IsMatch(a, @"\.(msix|msixbundle|appx|appxbundle)$", RegexOptions.IgnoreCase));
        Assert.StartsWith("|", release.GetProperty("body").GetString()!.TrimStart(), StringComparison.Ordinal);
    }

    [GitHubFact]
    [Trait(TC, "TC-PKG-24-02")]
    public void TestRepositoryPreviewIsAPublishedPrerelease()
    {
        JsonElement release = GitHubFactAttribute.Api("releases").EnumerateArray().Single(r => r.GetProperty("tag_name").GetString() == "v0.2.0-preview.1");
        Assert.False(release.GetProperty("draft").GetBoolean());
        Assert.True(release.GetProperty("prerelease").GetBoolean());
    }

    [GitHubFact]
    [Trait(TC, "TC-PKG-24-03")]
    public void TestRepositoryFailedReleaseHasNoReleaseAndAnIssue()
    {
        Assert.DoesNotContain(GitHubFactAttribute.Api("releases").EnumerateArray(), r => r.GetProperty("tag_name").GetString() == "v0.1.1");
        Assert.Contains(GitHubFactAttribute.Api("issues?state=all&per_page=100").EnumerateArray(),
            i => i.GetProperty("title").GetString() == "Release v0.1.1 failed");
    }

    [GitHubFact]
    [Trait(TC, "TC-PKG-24-04")]
    public void TestRepositorySecondReleaseHasDeltaPackages()
    {
        JsonElement release = GitHubFactAttribute.Api("releases").EnumerateArray().Single(r => r.GetProperty("tag_name").GetString() == "v0.1.2");
        string[] assets = [.. release.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString()!)];
        foreach (string arch in new[] { "x64", "arm64" })
        {
            Assert.Contains(assets, a => a.Contains(arch, StringComparison.Ordinal) && a.EndsWith("-delta.nupkg", StringComparison.Ordinal));
        }
    }

    /// <summary>TC-PKG-23-01〜03: 環境変数で渡した PR の番号のチェックの結果 (HEXEDITOR_GH_PR_OK・_BROKEN・_FORK)。</summary>
    [GitHubFact]
    [Trait(TC, "TC-PKG-23-01")]
    [Trait(TC, "TC-PKG-23-02")]
    [Trait(TC, "TC-PKG-23-03")]
    public void TestRepositoryPullRequestChecks()
    {
        static Dictionary<string, JsonElement> Checks(string variable)
        {
            string pr = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"{variable} がありません。");
            string sha = GitHubFactAttribute.Api($"pulls/{pr}").GetProperty("head").GetProperty("sha").GetString()!;
            return GitHubFactAttribute.Api($"commits/{sha}/check-runs?per_page=100").GetProperty("check_runs").EnumerateArray()
                .GroupBy(c => c.GetProperty("name").GetString()!).ToDictionary(g => g.Key, g => g.Last());
        }

        // TC-PKG-23-01: 6 つのジョブがすべて成功し、最初の開始から最後の終了まで 20 分以内。
        Dictionary<string, JsonElement> ok = Checks("HEXEDITOR_GH_PR_OK");
        foreach (string name in new[] { "build", "test-x64", "test-arm64", "i18n", "lint", "size" })
        {
            Assert.Contains(ok.Keys, k => k == name || k.StartsWith(name + " (", StringComparison.Ordinal));
        }

        Assert.All(ok.Values, c => Assert.Equal("success", c.GetProperty("conclusion").GetString()));
        DateTime first = ok.Values.Min(c => c.GetProperty("started_at").GetDateTime());
        DateTime last = ok.Values.Max(c => c.GetProperty("completed_at").GetDateTime());
        Assert.True(last - first <= TimeSpan.FromMinutes(20), $"{(last - first).TotalMinutes:F1} 分");

        // TC-PKG-23-02: 単体テストを壊した PR は test-x64・test-arm64 が失敗し、マージできない。
        Dictionary<string, JsonElement> broken = Checks("HEXEDITOR_GH_PR_BROKEN");
        Assert.Equal("failure", broken["test-x64"].GetProperty("conclusion").GetString());
        Assert.Equal("failure", broken["test-arm64"].GetProperty("conclusion").GetString());
        string brokenPr = Environment.GetEnvironmentVariable("HEXEDITOR_GH_PR_BROKEN")!;
        Assert.NotEqual("clean", GitHubFactAttribute.Api($"pulls/{brokenPr}").GetProperty("mergeable_state").GetString());

        // TC-PKG-23-03: フォークからの PR でも build と test が成功する。
        Dictionary<string, JsonElement> fork = Checks("HEXEDITOR_GH_PR_FORK");
        foreach (string name in new[] { "test-x64", "test-arm64" })
        {
            Assert.Equal("success", fork[name].GetProperty("conclusion").GetString());
        }
    }
}

/// <summary>
/// テスト用のリポジトリ (環境変数 HEXEDITOR_GH_TEST_REPO = owner/name) の結果を読むテスト。変数がない・gh CLI がないときはスキップする。
/// </summary>
public sealed class GitHubFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_GH_TEST_REPO";

    public GitHubFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"テスト用のリポジトリ (PKG-23・PKG-24 のテストケースの環境) の指定がない ({Variable}=owner/name と gh CLI の認証で実行する)。";
        }
    }

    /// <summary>gh api repos/{owner}/{name}/{path} の結果。</summary>
    public static JsonElement Api(string path)
    {
        string repo = Environment.GetEnvironmentVariable(Variable)!;
        var info = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("api");
        info.ArgumentList.Add($"repos/{repo}/{path}");
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("gh を起動できません。");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"gh api {path}: {error}");
        return JsonDocument.Parse(output).RootElement.Clone();
    }
}
