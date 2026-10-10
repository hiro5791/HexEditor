namespace HexEditor.App.Services;

/// <summary>
/// アプリのログ (UI-57 の仕様 2)。ローカルの <c>logs\hexeditor.log</c> にだけ書き、1 MB × 5 ファイルで回す。
/// 直近の 200 行はメモリにも持ち、クラッシュ情報に添える (PKG-30 の仕様 1 の 2)。
/// ファイルの内容は書かない。ファイルのパスは <see cref="Debug"/> でだけ書く。
/// </summary>
public static class AppLog
{
    public const int Capacity = 200;
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxFiles = 5;
    public const string FileName = "hexeditor.log";

    private static readonly Queue<string> Lines = new();
    private static readonly object Lock = new();

    private static string? _folder;

    /// <summary>ログのフォルダ (書けない場合は null)。管理者権限の補助プロセスのログもここに置く (ENG-28 の仕様 9)。</summary>
    public static string? Folder => _folder;

    /// <summary>ログのファイルを書き始める。書けない場合はメモリだけに残す。</summary>
    public static void Initialize(string logsFolder)
    {
        try
        {
            Directory.CreateDirectory(logsFolder);
            _folder = logsFolder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _folder = null;
        }
    }

    /// <summary>true ならデバッグのログ (ファイルのパスを含む) も残す (設定 log.level = debug)。</summary>
    public static bool DebugEnabled { get; set; }

    public static void Info(string message) => Add("INF", message);

    public static void Warning(string message) => Add("WRN", message);

    public static void Error(string message) => Add("ERR", message);

    /// <summary>ファイルのパスなど、利用者の情報を含みうるもの。<see cref="DebugEnabled"/> のときだけ残す。</summary>
    public static void Debug(string message)
    {
        if (DebugEnabled)
        {
            Add("DBG", message);
        }
    }

    public static IReadOnlyList<string> Recent()
    {
        lock (Lock)
        {
            return Lines.ToList();
        }
    }

    private static void Add(string level, string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        lock (Lock)
        {
            if (Lines.Count == Capacity)
            {
                Lines.Dequeue();
            }

            Lines.Enqueue(line);
            WriteToFile(line);
        }
    }

    private static void WriteToFile(string line)
    {
        if (_folder is null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(_folder, FileName);
            if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
            {
                Rotate(path);
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログを書けなくても動作は続ける。
        }
    }

    /// <summary>hexeditor.log → .1 → .2 … と送り、最も古いものを消す。</summary>
    private static void Rotate(string path)
    {
        File.Delete($"{path}.{MaxFiles - 1}");
        for (int i = MaxFiles - 2; i >= 1; i--)
        {
            if (File.Exists($"{path}.{i}"))
            {
                File.Move($"{path}.{i}", $"{path}.{i + 1}");
            }
        }

        File.Move(path, $"{path}.1");
    }
}
