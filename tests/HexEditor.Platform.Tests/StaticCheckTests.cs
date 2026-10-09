using System.Text.RegularExpressions;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>ソースコードと配布物の文書の静的な検査。</summary>
public sealed partial class StaticCheckTests
{
    /// <summary>配布形態を判定してよいファイル (IAppEnvironment の実装と判定、Velopack の初期化。PKG-01 の仕様 3)。</summary>
    private static readonly string[] AllowedFiles =
    [
        "src/HexEditor.Platform/AppEnvironment.cs",
        "src/HexEditor.App/Hosting/VelopackBootstrap.cs",

        // MSIX 版だけに入る右クリックメニューの DLL (UI-55)。配布形態の判定ではなく、パッケージのリソースを読むためにパッケージ名を使う。
        "src/HexEditor.ShellExtension/PackageCommandHost.cs",
    ];

    [GeneratedRegex(@"#if\s+.*HEX_DISTRO_|GetCurrentPackageFullName|Package\.Current|portable\.marker")]
    private static partial Regex DistributionCheck();

    /// <summary>ネットワークに接続してよいファイル: 通信の入口と、通信の入口で許可を確かめる Velopack のダウンロード (UI-57、UI-58)。</summary>
    private static readonly string[] NetworkFiles =
    [
        "src/HexEditor.Platform/Network/NetworkClient.cs",
        "src/HexEditor.App/Hosting/VelopackBootstrap.cs",
    ];

    [GeneratedRegex(@"\bnew\s+HttpClient\b|\bHttpClient\s*\(|\b(TcpClient|UdpClient|TcpListener|WebRequest|HttpWebRequest|WebClient|ClientWebSocket)\b|\bnew\s+Socket\s*\(|\bDns\.")]
    private static partial Regex NetworkUse();

    [Fact]
    [Trait(TC, "TC-UI-57-03")]
    [Trait(TC, "TC-UI-58-04")]
    public void Only_the_network_client_connects_to_the_network()
    {
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(RepoFile("src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || NetworkFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (!line.StartsWith("//", StringComparison.Ordinal) && NetworkUse().IsMatch(line))
                {
                    found.Add($"{relative}:{i + 1}: {line}");
                }
            }
        }

        Assert.True(found.Count == 0, "network access outside NetworkClient:\n" + string.Join("\n", found));

        // Velopack のダウンロードも、要求の前に通信の入口で許可を確かめる。
        string velopack = File.ReadAllText(RepoFile("src/HexEditor.App/Hosting/VelopackBootstrap.cs"));
        Assert.Contains("network.EnsureAllowed(NetworkFeature.Updates)", velopack, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-PKG-01-03")]
    public void OnlyTheEnvironmentDetectsTheDistribution()
    {
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(RepoFile("src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || AllowedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*'))
                {
                    continue;
                }

                if (DistributionCheck().IsMatch(line))
                {
                    found.Add($"{relative}({i + 1}): {line}");
                }
            }
        }

        Assert.True(found.Count == 0, "配布形態を判定するコードが IAppEnvironment の外にあります:\n" + string.Join("\n", found));
    }

    /// <summary>SmartScreen の案内の 4 つの内容 (PKG-25 の仕様 3)。言語ごとに、どれかの言い方があればよい。</summary>
    private static readonly (string Topic, string[] AnyOf)[] SmartScreenTopics =
    [
        ("SmartScreen の警告と理由", ["SmartScreen"]),
        ("続ける手順 (詳細情報 → 実行)", ["More info", "詳細情報"]),
        ("SHA256SUMS.txt での確認", ["SHA256SUMS.txt"]),
        ("Microsoft Store 版の案内と管理者権限", ["Microsoft Store"]),
    ];

    public static TheoryData<string> SmartScreenDocuments() =>
    [
        "README.md",
        "build/portable/README.txt",
        "build/release/release-notes-template.md",
    ];

    [Theory]
    [Trait(TC, "TC-PKG-25-02")]
    [MemberData(nameof(SmartScreenDocuments))]
    public void DocumentsExplainSmartScreen(string document)
    {
        string text = File.ReadAllText(RepoFile(document));
        foreach ((string topic, string[] anyOf) in SmartScreenTopics)
        {
            Assert.True(anyOf.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{document} に「{topic}」の説明がありません。");
        }

        // Store 版で管理者権限が必要な機能を使うには、アプリ全体を管理者として実行する (PKG-04)。
        Assert.True(text.Contains("Run as administrator", StringComparison.OrdinalIgnoreCase) || text.Contains("管理者として実行"),
            $"{document} に Store 版の管理者権限の扱いがありません。");
    }

    [Fact]
    public void PortableReadmeExplainsCommandLineAndRemoval()
    {
        string text = File.ReadAllText(RepoFile("build/portable/README.txt"));
        Assert.Contains("hexed --unregister", text);

        // フェーズ 1 から HexEditor.exe も --unregister を受け付ける (hexed.exe はフェーズ 3。08 の AUTO-36 の 9)。
        Assert.Contains("HexEditor.exe\" --unregister", text);
        Assert.Contains("hexed.exe", text);
        Assert.Contains("PATH", text);
        Assert.Contains("DataDirectory=", text);
    }

    [Fact]
    public void ManifestListsAll23LanguagesAndUsesResources()
    {
        string manifest = File.ReadAllText(RepoFile("src/HexEditor.App/Package.appxmanifest"));
        string[] languages =
        [
            "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
            "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
        ];
        Assert.All(languages, l => Assert.Contains($"<Resource Language=\"{l}\" />", manifest));
        Assert.DoesNotContain("x-generate", manifest);
        Assert.Contains("ms-resource:AppDisplayName", manifest);
        Assert.Contains("ms-resource:AppDescription", manifest);
        Assert.Contains("Name=\"HexEditor\"", manifest);
        Assert.DoesNotContain("Windows.Universal", manifest);
        Assert.DoesNotContain("allowElevation", manifest);
        foreach (string lang in new[] { "en", "ja" })
        {
            string resw = File.ReadAllText(RepoFile($"src/HexEditor.App/Strings/{lang}/Resources.resw"));
            Assert.Contains("name=\"AppDisplayName\"", resw);
            Assert.Contains("name=\"AppDescription\"", resw);
        }
    }

    [Fact]
    public void PowerShellScriptsAreAscii()
    {
        foreach (string file in Directory.EnumerateFiles(RepoFile("build"), "*.ps1", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert.True(bytes.All(b => b < 0x80), $"{file} に ASCII 以外の文字があります (Windows PowerShell 5.1 で壊れるため)。");
        }
    }
}
