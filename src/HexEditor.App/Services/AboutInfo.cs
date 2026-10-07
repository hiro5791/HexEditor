using System.Runtime.InteropServices;
using HexEditor.App.Hosting;

namespace HexEditor.App.Services;

/// <summary>
/// バージョン情報の内容 (UI-40 の仕様 2) と「問題を報告」の URL (仕様 5)。開いたファイルの名前・パス・内容は入れない。
/// </summary>
public static class AboutInfo
{
    public const string RepositoryUrl = "https://github.com/hiro5791/HexEditor";

    /// <summary>ドキュメントのサイト (URL は未決定。決まるまでリポジトリの README)。</summary>
    public const string DocumentationUrl = RepositoryUrl + "#readme";

    public static IReadOnlyList<(string Label, string Value)> Items(IAppEnvironment env) =>
    [
        ("Version", env.InformationalVersion),
        ("Channel", env.Channel.ToString()),
        ("Distribution", env.Distribution.ToString()),
        ("Architecture", env.ProcessArchitecture.ToString()),
        (".NET", RuntimeInformation.FrameworkDescription),
        ("Windows App SDK", WindowsAppSdkVersion()),
        ("OS", $"{RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})"),
        ("Administrator", env.IsElevated ? "Yes" : "No"),
    ];

    /// <summary>「情報をコピー」でクリップボードに入れるテキスト (仕様 3)。データフォルダの場所は含めない (利用者名を含むため)。</summary>
    public static string Text(IAppEnvironment env) =>
        "HexEditor" + Environment.NewLine + string.Join(Environment.NewLine, Items(env).Select(i => $"{i.Label}: {i.Value}"));

    /// <summary>GitHub の Issue 作成ページ。本文に環境の情報を入れる。<paramref name="note"/> は追加の案内。</summary>
    public static Uri IssueUrl(IAppEnvironment env, string? note = null)
    {
        string body = (note is null ? string.Empty : note + "\n\n")
            + "## What happened\n\n\n## Steps to reproduce\n\n1. \n\n## Environment\n\n```\n" + Text(env) + "\n```\n";
        return new Uri($"{RepositoryUrl}/issues/new?body={Uri.EscapeDataString(body)}");
    }

    private static string WindowsAppSdkVersion()
    {
        try
        {
            return Microsoft.Windows.ApplicationModel.WindowsAppRuntime.RuntimeInfo.AsString;
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
