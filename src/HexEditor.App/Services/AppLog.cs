namespace HexEditor.App.Services;

/// <summary>
/// アプリのログ (直近の 200 行をメモリに持つ)。クラッシュ情報に添える (PKG-30 の仕様 1 の 2)。
/// ファイルの内容は書かない。ファイルのパスは <see cref="Debug"/> でだけ書く (UI-57)。
/// </summary>
public static class AppLog
{
    public const int Capacity = 200;

    private static readonly Queue<string> Lines = new();
    private static readonly object Lock = new();

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
        }
    }
}
