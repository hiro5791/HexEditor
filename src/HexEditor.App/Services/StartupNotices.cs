namespace HexEditor.App.Services;

/// <summary>起動時の知らせの重要度 (InfoBar の Severity に対応する)。</summary>
public enum StartupNoticeSeverity
{
    Informational,
    Warning,
    Error,
}

/// <summary>
/// 起動時の知らせ 1 件。<see cref="Key"/> は文言のリソースのキー (<see cref="Loc.Format"/> に <see cref="Args"/> を渡す)。
/// <see cref="ActionKey"/> はボタンの文言のキー (なければ null)。
/// </summary>
public sealed record StartupNotice(string Key, StartupNoticeSeverity Severity, object[] Args, string? ActionKey = null)
{
    public string Message => Loc.Format(Key, Args);
}

/// <summary>
/// 起動処理 (XAML の前) で見つかった、アプリ全体の InfoBar で知らせること。メインウィンドウが読み込まれたら
/// <see cref="TakeAll"/> で取り出して表示する。
/// - Startup_RedirectTimedOut: 既存のインスタンスが応答せず、独立して起動した (UI-15 の仕様 6)
/// - Startup_DataFolderReadOnly: データフォルダに書き込めない (PKG-06 の仕様 5 の 4。ボタン Startup_ExportSettings)
/// - Startup_OldRecoveryDeleted: 30 日以上前の復旧用データを消した (PKG-13 の仕様 5)
/// </summary>
public static class StartupNotices
{
    private static readonly List<StartupNotice> Items = [];
    private static readonly object Lock = new();

    public static void Add(StartupNotice notice)
    {
        lock (Lock)
        {
            Items.Add(notice);
        }
    }

    public static IReadOnlyList<StartupNotice> TakeAll()
    {
        lock (Lock)
        {
            List<StartupNotice> all = [.. Items];
            Items.Clear();
            return all;
        }
    }
}
