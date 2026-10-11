using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Clipboard;

namespace HexEditor.App.ViewModels;

/// <summary>クリップボードパネルの 1 項目 (EDIT-28 の仕様 5)。ユーザークリップボード (番号 1〜9) か、履歴の項目。</summary>
public sealed class ClipboardItemRow
{
    /// <summary>ユーザークリップボードの番号 (1〜9)。履歴の項目なら 0。</summary>
    public required int Number { get; init; }

    /// <summary>履歴の項目の位置 (新しい順。ユーザークリップボードなら −1)。</summary>
    public required int HistoryIndex { get; init; }

    public required ClipboardEntry? Entry { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    /// <summary>「終了時に消えます」(仕様 6)。</summary>
    public required string Note { get; init; }

    public bool IsEmpty => Entry is null;

    public string AccessibleName => string.Join(", ", new[] { Title, Detail, Note }.Where(s => s.Length > 0));
}

/// <summary>
/// クリップボードパネル (EDIT-28): ユーザークリップボード 1〜9 と、アプリ内でコピーした直近の履歴。アプリ全体で共有する
/// <see cref="UserClipboards"/> を表示する (ウィンドウごとに 1 つ)。
/// </summary>
public sealed partial class ClipboardPanelViewModel : ObservableObject
{
    private readonly UserClipboards _clipboards;

    public ClipboardPanelViewModel(UserClipboards clipboards)
    {
        _clipboards = clipboards;
        _clipboards.Changed += Clipboards_Changed;
        Refresh();
    }

    public ObservableCollection<ClipboardItemRow> Slots { get; } = [];

    public ObservableCollection<ClipboardItemRow> History { get; } = [];

    public UserClipboards Clipboards => _clipboards;

    public void Detach() => _clipboards.Changed -= Clipboards_Changed;

    private void Clipboards_Changed(object? sender, EventArgs e) =>
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(Refresh);

    public void Refresh()
    {
        Slots.Clear();
        for (int n = 1; n <= UserClipboards.SlotCount; n++)
        {
            ClipboardEntry? entry = _clipboards.Get(n);
            string title = _clipboards.NameOf(n) is { } name ? Loc.Format("ClipboardPanel_SlotNamed", n, name) : Loc.Format("ClipboardPanel_Slot", n);
            Slots.Add(new ClipboardItemRow
            {
                Number = n,
                HistoryIndex = -1,
                Entry = entry,
                Title = title,
                Detail = entry is null ? Loc.Get("ClipboardPanel_Empty") : Describe(entry),
                Note = _clipboards.WillBeLost(n) ? Loc.Get("ClipboardPanel_LostOnExit") : string.Empty,
            });
        }

        History.Clear();
        for (int i = 0; i < _clipboards.History.Count; i++)
        {
            ClipboardEntry entry = _clipboards.History[i];
            History.Add(new ClipboardItemRow
            {
                Number = 0,
                HistoryIndex = i,
                Entry = entry,
                Title = entry.Time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture),
                Detail = Describe(entry),
                Note = string.Empty,
            });
        }
    }

    /// <summary>大きさ、先頭 16 バイトの Hex とテキスト、コピー元 (ファイル名とオフセット)。</summary>
    private static string Describe(ClipboardEntry entry)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string hex = HexText.Format(entry.Head);
        string text = new([.. entry.Head.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]);
        return Loc.Format("ClipboardPanel_Detail", entry.Length.ToString("N0", culture), hex, text, entry.SourceName, "0x" + entry.SourceOffset.ToString("X", culture));
    }

    /// <summary>テスト用の命令の通り道に返す一覧。</summary>
    internal JsonObject TestModel()
    {
        static JsonObject Row(ClipboardItemRow r) => new()
        {
            ["number"] = r.Number,
            ["title"] = r.Title,
            ["detail"] = r.Detail,
            ["note"] = r.Note,
            ["empty"] = r.IsEmpty,
            ["length"] = r.Entry?.Length,
            ["head"] = r.Entry is { } e ? HexText.Format(e.Head) : null,
            ["offset"] = r.Entry?.SourceOffset,
        };

        return new JsonObject
        {
            ["slots"] = new JsonArray([.. Slots.Select(r => (JsonNode?)Row(r))]),
            ["history"] = new JsonArray([.. History.Select(r => (JsonNode?)Row(r))]),
        };
    }
}
