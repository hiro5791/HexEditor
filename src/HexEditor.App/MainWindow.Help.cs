using System.Net;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.Core.Help;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 利用者向けマニュアルを開く (09 の UI-40 の仕様 1、UI-59)。ヘルプ > ドキュメントと、機能の中のヘルプへのリンク
/// (テンプレートのエラーのコード、C# スクリプトのデバッグなど) はすべて <see cref="OpenManualAsync"/> を使う。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>アプリに同梱したマニュアル (HexEditor.App.csproj の HexBuildManual が ManualBuilder で作る)。</summary>
    internal static string BundledManualRoot => Path.Combine(AppContext.BaseDirectory, ManualLinks.BundledFolder);

    private static readonly SemaphoreSlim ManualCopyLock = new(1, 1);

    /// <summary>
    /// マニュアルのページを既定のブラウザで開く。オンライン (オフラインモードでなく、ネットワークにつながっている) なら表示言語の
    /// オンラインのページ、そうでなければ同梱の HTML。表示言語のマニュアルがなければ英語 (UI-59 の仕様 4)。
    /// </summary>
    internal async Task OpenManualAsync(ManualTopic topic)
    {
        bool online = !AppUpdates.Policy.Offline && IsNetworkAvailable();
        ManualLink link = ManualLinks.Choose(topic, s_language, online, BundledManualRoot);
        AppLog.Info($"Manual: {(link.IsLocal ? "bundled" : "online")} {link.Language}/{topic.Page}");
        if (!link.IsLocal)
        {
            await OpenUriAsync(link.Uri);
            return;
        }

        await OpenLocalManualPageAsync(topic, link.Language);
    }

    /// <summary>OS がネットワークにつながっていると知らせているか (通信はしない)。テストでは networkAvailable で差し替える。</summary>
    private static bool IsNetworkAvailable()
    {
        if (TestHooks.NetworkAvailableOverride is bool available)
        {
            return available;
        }

        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 同梱のページを開く。アプリのフォルダはブラウザから読めない場合があるため (MSIX のインストール先など)、データの一時フォルダに
    /// 写したものを開く。見出しへのリンク (#) は、file: の URL を直接開くと落ちるため、転送するだけの HTML を経由する。
    /// </summary>
    private async Task OpenLocalManualPageAsync(ManualTopic topic, string language)
    {
        string root = await LocalManualCopyAsync();
        Uri page = ManualLinks.Local(root, topic, language);
        if (TestHooks.InterceptLaunch(page))
        {
            return;
        }

        Uri target = page;
        if (!string.IsNullOrEmpty(topic.Anchor))
        {
            try
            {
                string redirect = Path.Combine(root, "open.html");
                string url = WebUtility.HtmlEncode(page.AbsoluteUri);
                await File.WriteAllTextAsync(redirect,
                    $"<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={url}\"><title>HexEditor</title></head>"
                    + $"<body><a href=\"{url}\">{url}</a></body></html>\n");
                target = new Uri(redirect);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning($"Manual: the redirect page could not be written ({ex.GetType().Name})");
            }
        }

        if (!await Windows.System.Launcher.LaunchUriAsync(target))
        {
            var package = new DataPackage();
            package.SetText(page.AbsoluteUri);
            SystemClipboard.SetContent(package);
            ShowNotice(Loc.Get("Notice_UrlCopied"), InfoBarSeverity.Informational);
        }
    }

    /// <summary>
    /// 同梱のマニュアルを、データの一時フォルダの <c>manual\&lt;版&gt;\</c> に写す (版ごとに 1 回。古い版のものは消す)。写せなければ
    /// アプリのフォルダのものを使う。
    /// </summary>
    private static async Task<string> LocalManualCopyAsync()
    {
        string bundled = BundledManualRoot;
        string index = Path.Combine(bundled, ManualLinks.IndexFile);
        if (!File.Exists(index))
        {
            return bundled;
        }

        string parent = Path.Combine(Program.Environment.Locations.Temp, "manual");
        string stamp = File.GetLastWriteTimeUtc(index).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string target = Path.Combine(parent, stamp);
        await ManualCopyLock.WaitAsync();
        try
        {
            if (File.Exists(Path.Combine(target, ManualLinks.IndexFile)))
            {
                return target;
            }

            return await Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(parent);
                    foreach (string old in Directory.EnumerateDirectories(parent))
                    {
                        TryDeleteDirectory(old);
                    }

                    string temp = target + ".tmp";
                    CopyDirectory(bundled, temp);
                    Directory.Move(temp, target);
                    return target;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warning($"Manual: the bundled manual could not be copied ({ex.GetType().Name}); opening it in the app folder");
                    return bundled;
                }
            });
        }
        finally
        {
            ManualCopyLock.Release();
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
