namespace HexEditor.App.Services;

/// <summary>未処理の例外を crash.log に追記する (PKG-30 の最小限)。保存先は実行環境のクラッシュ情報のフォルダ (PKG-13)。</summary>
public static class CrashLog
{
    private static string _folder = Path.Combine(Path.GetTempPath(), "HexEditor");

    public static string PathName => Path.Combine(_folder, "crash.log");

    public static void Initialize(string folder)
    {
        _folder = folder;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception);
    }

    public static void Write(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.AppendAllText(PathName, $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
