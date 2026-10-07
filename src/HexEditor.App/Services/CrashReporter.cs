using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.App.Hosting;

namespace HexEditor.App.Services;

/// <summary>
/// 異常終了時のデータ (PKG-30)。未処理の例外を捕まえたら、復旧用データを書き出し (2 秒で打ち切る)、クラッシュ情報を
/// <c>crash\&lt;日時&gt;.txt</c> に書いてから終了する。次の起動で、未確認のクラッシュ情報を知らせる。
/// </summary>
public static partial class CrashReporter
{
    public const int KeepCount = 10;

    private const string SeenFileName = "seen.txt";

    private static IAppEnvironment? _environment;
    private static string _folder = Path.Combine(Path.GetTempPath(), "HexEditor", "crash");
    private static int _handling;

    public const string MiniDumpKey = "diagnostics.writeMiniDump";

    /// <summary>
    /// クラッシュ情報と一緒にミニダンプを書くか (設定 diagnostics.writeMiniDump。既定 false。PKG-30 の仕様 4)。
    /// メモリの内容を含まない MiniDumpNormal だけを書く (ファイルの内容を含めないため。UI-57 の仕様 4)。
    /// </summary>
    public static bool WriteMiniDump { get; set; }

    /// <summary>異常終了の直前に未保存の編集内容を書き出す処理 (App が設定する)。引数は打ち切るまでの時間。</summary>
    public static Action<TimeSpan>? WriteRecovery { get; set; }

    public static string Folder => _folder;

    public static void Initialize(IAppEnvironment environment)
    {
        _environment = environment;
        _folder = environment.Locations.Crash;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Handle(e.ExceptionObject as Exception, exit: false);
        TaskScheduler.UnobservedTaskException += (_, e) => Handle(e.Exception, exit: true);
    }

    /// <summary>
    /// 未処理の例外を処理する。<paramref name="exit"/> が true ならその後プロセスを終える (放っておくと続行してしまう例外。
    /// UI スレッドと AppDomain の例外は、この後ランタイムがプロセスを終える)。
    /// </summary>
    public static void Handle(Exception? exception, bool exit)
    {
        if (Interlocked.Exchange(ref _handling, 1) == 1)
        {
            return;
        }

        try
        {
            WriteRecovery?.Invoke(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 復旧用データを書けなくても、クラッシュ情報は書く。
        }

        Write(exception);
        if (exit)
        {
            Environment.Exit(1);
        }
    }

    /// <summary>クラッシュ情報を書く。書けた場所を返す (どこにも書けなければ null)。</summary>
    public static string? Write(Exception? exception)
    {
        string name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt";
        string text = Format(exception);
        try
        {
            Directory.CreateDirectory(_folder);
            string path = Path.Combine(_folder, name);
            File.WriteAllText(path, text, Encoding.UTF8);
            if (WriteMiniDump)
            {
                TryWriteMiniDump(Path.ChangeExtension(path, ".dmp"));
            }

            Prune();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "HexEditor-crash-" + name);
                File.WriteAllText(fallback, text, Encoding.UTF8);
                return fallback;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// クラッシュ情報の本文: 版、配布形態、アーキテクチャ、OS の版、例外、直近のログ。ファイルの内容は書かない。
    /// ファイルのパスはデバッグのログが有効なときだけ残す (UI-57)。
    /// </summary>
    public static string Format(Exception? exception)
    {
        var text = new StringBuilder();
        text.AppendLine("HexEditor crash report");
        text.AppendLine($"Time: {DateTimeOffset.Now:O}");
        text.AppendLine($"Version: {_environment?.InformationalVersion ?? "?"}");
        text.AppendLine($"Distribution: {_environment?.Distribution.ToString() ?? "?"}");
        text.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})");
        text.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        text.AppendLine();
        text.AppendLine("Exception:");
        text.AppendLine(exception is null ? "(unknown)" : Redact(exception.ToString()));
        text.AppendLine();
        text.AppendLine($"Recent log (last {AppLog.Capacity} lines):");
        foreach (string line in AppLog.Recent())
        {
            text.AppendLine(Redact(line));
        }

        return text.ToString();
    }

    /// <summary>前回以前の未確認のクラッシュ情報のうち最新のもの。なければ null (PKG-30 の仕様 2)。</summary>
    public static string? FindUnseen()
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return null;
            }

            string? newest = Directory.EnumerateFiles(_folder, "*.txt")
                .Where(f => !Path.GetFileName(f).Equals(SeenFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .FirstOrDefault();
            string seenPath = Path.Combine(_folder, SeenFileName);
            string seen = File.Exists(seenPath) ? File.ReadAllText(seenPath).Trim() : string.Empty;
            return newest is not null && string.CompareOrdinal(Path.GetFileName(newest), seen) > 0 ? newest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>知らせたクラッシュ情報を確認済みにする (次の起動で再び知らせない)。</summary>
    public static void MarkSeen(string crashFile)
    {
        try
        {
            File.WriteAllText(Path.Combine(_folder, SeenFileName), Path.GetFileName(crashFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>最新の 10 件を残し、古いものを消す (仕様 3)。ミニダンプも同じく 10 件まで。</summary>
    private static void Prune()
    {
        foreach (string pattern in new[] { "*.txt", "*.dmp" })
        {
            foreach (string old in Directory.EnumerateFiles(_folder, pattern)
                .Where(f => !Path.GetFileName(f).Equals(SeenFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(KeepCount))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>ミニダンプ (MiniDumpNormal: スレッドとスタック、読み込んだモジュールの一覧。ヒープの内容は含まない) を書く。</summary>
    private static void TryWriteMiniDump(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            if (!MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle, MiniDumpNormal, 0, 0, 0))
            {
                AppLog.Warning($"MiniDumpWriteDump failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Mini dump not written: {ex.GetType().Name}");
        }
    }

    private const uint MiniDumpNormal = 0;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, Microsoft.Win32.SafeHandles.SafeFileHandle file,
        uint dumpType, nint exceptionParam, nint userStreamParam, nint callbackParam);

    private static string Redact(string text) => AppLog.DebugEnabled ? text : PathPattern().Replace(text, "<path>");

    /// <summary>ドライブ文字または UNC で始まるパス。</summary>
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\)[^\s""'<>|*?\r\n]*")]
    private static partial Regex PathPattern();
}
