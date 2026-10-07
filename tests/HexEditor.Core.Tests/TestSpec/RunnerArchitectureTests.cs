using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>
/// PKG-15 の受け入れ基準 2: ARM64 の CI ランナー (ci.yml の test-arm64) で単体テストが ARM64 のプロセスとして動く。
/// CI は期待するアーキテクチャを環境変数 HEXEDITOR_EXPECTED_ARCH で渡す (test-arm64 は Arm64、test-x64 は X64)。
/// 渡されていない (開発者の PC) ときは、動いているアーキテクチャを出力するだけ。
/// </summary>
public sealed class RunnerArchitectureTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("TC", "TC-PKG-15-03")]
    public void TestProcessRunsOnTheExpectedArchitecture()
    {
        Architecture process = RuntimeInformation.ProcessArchitecture;
        output.WriteLine($"テストのプロセス: {process}、OS: {RuntimeInformation.OSArchitecture}");
        if (Environment.GetEnvironmentVariable("HEXEDITOR_EXPECTED_ARCH") is { Length: > 0 } expected)
        {
            Assert.Equal(expected, process.ToString(), ignoreCase: true);
        }
    }
}
