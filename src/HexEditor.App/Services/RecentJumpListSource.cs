using HexEditor.Core.Files;
using HexEditor.Platform.Shell;

namespace HexEditor.App.Services;

/// <summary>
/// ジャンプリスト (UI-35) が読む「最近使ったファイル」: アプリの最近使ったファイルの一覧 (ENG-16、UI-32) をそのまま見せる。
/// ディスク・SFTP の項目はジャンプリストから開けないので、ファイルとディスクイメージだけを出す。
/// </summary>
public sealed class RecentJumpListSource : IRecentFilesSource
{
    private readonly RecentFileList _list;

    public RecentJumpListSource(RecentFileList list)
    {
        _list = list;
        _list.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<RecentFileEntry> Pinned => Entries(_list.Pinned);

    public IReadOnlyList<RecentFileEntry> Recent => Entries(_list.Recent);

    public event EventHandler? Changed;

    private static List<RecentFileEntry> Entries(IEnumerable<RecentItem> items) =>
        [.. items.Where(i => i.Kind is RecentItemKind.File or RecentItemKind.DiskImage).Select(i => new RecentFileEntry(i.Path, i.DisplayName))];
}
