using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Files;

namespace HexEditor.App.ViewModels;

/// <summary>最近使ったファイルの一覧の 1 行 (スタートページ、「すべて表示…」。UI-32 の仕様 4・6)。</summary>
public sealed partial class RecentEntryViewModel : ObservableObject
{
    public RecentEntryViewModel(RecentItem item)
    {
        Item = item;
        Name = item.DisplayName;
        Folder = System.IO.Path.GetDirectoryName(item.Path) ?? item.Path;
        ShortFolder = Shorten(Folder);
    }

    public RecentItem Item { get; }

    public string Path => Item.Path;

    public string Name { get; }

    public string Folder { get; }

    /// <summary>短縮したフォルダ (サブメニュー・スタートページ)。完全なパスはツールチップに出す (UI-32 の仕様 3)。</summary>
    public string ShortFolder { get; }

    public bool IsPinned => Item.Pinned;

    public string LastOpened => Item.LastOpenedUtc.ToLocalTime().ToString("g");

    /// <summary>ファイルのサイズ (存在の確認と一緒にバックグラウンドで取る)。</summary>
    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    /// <summary>見つからない (薄く表示し、「見つかりません」と付記する。UI-32 の仕様 6)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsMissing { get; set; }

    public double Opacity => IsMissing ? 0.5 : 1.0;

    public string StatusText => IsMissing ? Loc.Get("Recent_NotFound") : IsPinned ? Loc.Get("Recent_Pinned") : string.Empty;

    /// <summary>スクリーンリーダーが読む名前 (ファイル名、フォルダ、状態)。</summary>
    public string AccessibleName => string.Join(", ", new[] { Name, Folder, StatusText }.Where(s => s.Length > 0));

    /// <summary>
    /// 存在を確かめる (一覧を表示した後にバックグラウンドで。ネットワークパスは確かめない。UI-32 の仕様 6)。結果は
    /// <paramref name="dispatch"/> で UI スレッドに戻して反映する。
    /// </summary>
    public void CheckExistsInBackground(Action<Action> dispatch)
    {
        if (Item.Kind != RecentItemKind.File || Item.IsNetworkPath)
        {
            return;
        }

        string path = Item.Path;
        _ = Task.Run(() =>
        {
            var info = new FileInfo(path);
            bool exists = info.Exists;
            string size = exists ? Core.View.StatusFormat.ShortSize(info.Length, System.Globalization.CultureInfo.CurrentCulture)
                ?? Loc.Format("Size_Bytes", Core.View.StatusFormat.Number(info.Length, System.Globalization.CultureInfo.CurrentCulture)) : string.Empty;
            dispatch(() =>
            {
                IsMissing = !exists;
                SizeText = size;
                OnPropertyChanged(nameof(AccessibleName));
            });
        });
    }

    /// <summary>長いフォルダの途中を「…」にする (先頭のドライブと最後の 2 つのフォルダを残す)。</summary>
    public static string Shorten(string folder)
    {
        const int Limit = 48;
        if (folder.Length <= Limit)
        {
            return folder;
        }

        string[] parts = folder.Split(System.IO.Path.DirectorySeparatorChar);
        return parts.Length <= 3 ? folder : $"{parts[0]}{System.IO.Path.DirectorySeparatorChar}…{System.IO.Path.DirectorySeparatorChar}{parts[^2]}{System.IO.Path.DirectorySeparatorChar}{parts[^1]}";
    }
}

/// <summary>「はじめに」の選択肢 1 つ。</summary>
public sealed record StartChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// スタートページ (UI-38): 開く・新規作成、最近使ったファイル (ピン留め + 最近の 10 件)、前回のセッションの復元 (UI-30 の ask)、
/// 初回起動の「はじめに」(表示言語・テーマ・ショートカットのプリセットと、ネットワークを使う機能の 1 行)。
/// </summary>
public sealed partial class StartPageViewModel : ObservableObject
{
    public ObservableCollection<RecentEntryViewModel> RecentItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    public partial int RecentCount { get; set; }

    public bool HasRecent => RecentCount > 0;

    /// <summary>「はじめに」を出す (UI-38 の仕様 2・3)。</summary>
    [ObservableProperty]
    public partial bool ShowWelcome { get; set; }

    /// <summary>「前回のセッションを復元」を出す (UI-30 の ask)。</summary>
    [ObservableProperty]
    public partial bool CanRestoreSession { get; set; }

    /// <summary>表示言語を変えた (再起動で反映する。UI-43)。</summary>
    [ObservableProperty]
    public partial bool LanguageRestartNeeded { get; set; }

    public IReadOnlyList<StartChoice> Languages { get; } =
    [
        new("system", Loc.Get("Start_LanguageSystem")),
        .. Hosting.Localization.SupportedLanguages.Select(code => new StartChoice(code, NativeName(code))),
    ];

    public IReadOnlyList<StartChoice> Themes { get; } =
    [
        new("system", Loc.Get("Start_ThemeSystem")),
        new("light", Loc.Get("Start_ThemeLight")),
        new("dark", Loc.Get("Start_ThemeDark")),
    ];

    public IReadOnlyList<StartChoice> Presets { get; } =
    [
        new("default", Loc.Get("Start_PresetDefault")),
        new("hxd", Loc.Get("Start_PresetHxd")),
        new("010editor", Loc.Get("Start_Preset010")),
        new("vscode", Loc.Get("Start_PresetVsCode")),
    ];

    /// <summary>最近使ったファイルを並べ直す (ピン留め + 最近の 10 件。UI-32 の仕様 3、UI-38 の仕様 1)。</summary>
    public void SetRecent(IReadOnlyList<RecentItem> items, Action<Action> dispatch)
    {
        RecentItems.Clear();
        foreach (RecentItem item in items)
        {
            var entry = new RecentEntryViewModel(item);
            RecentItems.Add(entry);
            entry.CheckExistsInBackground(dispatch);
        }

        RecentCount = RecentItems.Count;
    }

    private static string NativeName(string code)
    {
        try
        {
            var culture = new System.Globalization.CultureInfo(code);
            return $"{culture.NativeName} ({code})";
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return code;
        }
    }
}
