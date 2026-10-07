namespace HexEditor.App.Services;

/// <summary>未処理の例外を一時フォルダの crash.log に追記する (診断用)。</summary>
public static class CrashLog
{
    public static string PathName { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HexEditor", "crash.log");

    public static void Write(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            File.AppendAllText(PathName, $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
