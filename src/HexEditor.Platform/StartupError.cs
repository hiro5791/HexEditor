using System.Runtime.InteropServices;
using System.Text;

namespace HexEditor.Platform;

/// <summary>
/// 起動処理の 1〜5 (PKG-11) で起きた例外の記録と表示 (PKG-11 の「エラー」)。
/// データフォルダ (判定できなければ %TEMP%\HexEditor) の <c>logs\startup-error.log</c> に書き、Win32 の MessageBox で知らせる。
/// XAML もリソースの読み込みも使えない段階なので、文言は呼び出し側が渡す (取れなければ英語の既定)。
/// </summary>
public static class StartupError
{
    public const string FileName = "startup-error.log";

    /// <summary>ログを書き、書いた場所を返す (書けなければ null)。ファイルのパスは書かない (UI-57)。</summary>
    public static string? WriteLog(Exception exception, string step, string? logsFolder, string tempPath)
    {
        string text = new StringBuilder()
            .AppendLine($"{DateTimeOffset.Now:O} Startup failed at step: {step}")
            .AppendLine($"OS: {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}")
            .AppendLine(Redaction.RedactPaths(exception.ToString()))
            .AppendLine()
            .ToString();
        foreach (string? folder in new[] { logsFolder, Path.Combine(tempPath, "HexEditor", "logs") })
        {
            if (folder is null)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, FileName);
                File.AppendAllText(path, text, Encoding.UTF8);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }
        }

        return null;
    }

    /// <summary>「起動できませんでした」と理由・ログの場所を表示する。</summary>
    public static void Show(string title, string message)
    {
        try
        {
            _ = MessageBoxW(0, message, title, MbOk | MbIconError | MbSetForeground);
        }
        catch (Exception)
        {
            // 表示できなくても終了する。
        }
    }

    private const uint MbOk = 0x0;
    private const uint MbIconError = 0x10;
    private const uint MbSetForeground = 0x10000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}

/// <summary>ログからファイルのパスを除く (UI-57 の仕様 3: パスは log.level = debug のときだけ書く)。</summary>
public static partial class Redaction
{
    public static string RedactPaths(string text) => PathPattern().Replace(text, "<path>");

    /// <summary>ドライブ文字または UNC で始まるパス。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\s""'<>|*?\r\n]*")]
    private static partial System.Text.RegularExpressions.Regex PathPattern();
}

/// <summary>このプロセスの AppUserModelID (PKG-12 の仕様 4)。</summary>
public static class ProcessIdentity
{
    /// <summary>非パッケージ版で、最初のウィンドウを作る前に呼ぶ。失敗しても起動は続ける。</summary>
    public static bool TrySetAppUserModelId(string appUserModelId)
    {
        try
        {
            return SetCurrentProcessExplicitAppUserModelID(appUserModelId) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// このプロセスに明示的に設定した AppUserModelID (<c>GetCurrentProcessExplicitAppUserModelID</c>)。設定していなければ null。
    /// 配布のテスト (TC-PKG-12-03) で、スタートメニューのショートカットの値と比べるために使う。
    /// </summary>
    public static string? TryGetAppUserModelId()
    {
        try
        {
            if (GetCurrentProcessExplicitAppUserModelID(out nint value) < 0 || value == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(value);
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);
}
