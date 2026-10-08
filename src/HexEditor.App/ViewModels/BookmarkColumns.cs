using HexEditor.App.Services;

namespace HexEditor.App.ViewModels;

/// <summary>ブックマーク一覧の列 (INSP-26 の仕様 1。値の列は型付きブックマーク INSP-29 とともに加える)。</summary>
public enum BookmarkColumn
{
    Number,
    Color,
    Name,
    Start,
    Length,
    Group,
    Comment,
}

/// <summary>
/// ブックマーク一覧の列の表示と順序 (INSP-26 の仕様 1「列の表示・順序は変えられる」)。アプリ全体で共通にし、state.json に保存する
/// (<c>bookmarks.columns</c>。例: <c>Number,Color,Name,Start,Length,!Group,!Comment</c>。<c>!</c> は非表示)。名前の列は常に表示する。
/// </summary>
public static class BookmarkColumns
{
    public const string StateKey = "bookmarks.columns";

    private static readonly (BookmarkColumn Column, bool Visible)[] Defaults =
    [
        (BookmarkColumn.Number, true),
        (BookmarkColumn.Color, true),
        (BookmarkColumn.Name, true),
        (BookmarkColumn.Start, true),
        (BookmarkColumn.Length, true),
        (BookmarkColumn.Group, false),
        (BookmarkColumn.Comment, false),
    ];

    private static List<(BookmarkColumn Column, bool Visible)>? _current;

    /// <summary>列の表示・順序が変わった。</summary>
    public static event EventHandler? Changed;

    /// <summary>すべての列 (順序どおり。非表示のものを含む)。</summary>
    public static IReadOnlyList<(BookmarkColumn Column, bool Visible)> All => _current ??= Load();

    /// <summary>表示する列 (順序どおり)。</summary>
    public static IReadOnlyList<BookmarkColumn> Visible => [.. All.Where(c => c.Visible).Select(c => c.Column)];

    /// <summary>列の幅 (epx。名前の列は残りの幅)。</summary>
    public static double WidthOf(BookmarkColumn column) => column switch
    {
        BookmarkColumn.Number => 28,
        BookmarkColumn.Color => 16,
        BookmarkColumn.Start => 96,
        BookmarkColumn.Length => 56,
        BookmarkColumn.Group => 80,
        BookmarkColumn.Comment => 120,
        _ => double.NaN,
    };

    /// <summary>列の表示を切り替える (名前の列は切り替えない)。</summary>
    public static void Toggle(BookmarkColumn column)
    {
        if (column == BookmarkColumn.Name)
        {
            return;
        }

        List<(BookmarkColumn Column, bool Visible)> list = [.. All];
        int index = list.FindIndex(c => c.Column == column);
        list[index] = (column, !list[index].Visible);
        Save(list);
    }

    /// <summary>列を表示されている列の中で左右に動かす (<paramref name="delta"/> は −1 か +1)。</summary>
    public static void Move(BookmarkColumn column, int delta)
    {
        List<(BookmarkColumn Column, bool Visible)> list = [.. All];
        int index = list.FindIndex(c => c.Column == column);
        int target = index;
        do
        {
            target += Math.Sign(delta);
        }
        while (target >= 0 && target < list.Count && !list[target].Visible);

        if (target < 0 || target >= list.Count)
        {
            return;
        }

        (list[index], list[target]) = (list[target], list[index]);
        Save(list);
    }

    /// <summary>既定に戻す。</summary>
    public static void Reset() => Save([.. Defaults]);

    /// <summary>保存されている文字列を読む (知らない列は無視し、足りない列は既定の位置と表示で後ろに加える)。</summary>
    internal static List<(BookmarkColumn Column, bool Visible)> Parse(string text)
    {
        var list = new List<(BookmarkColumn Column, bool Visible)>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool hidden = part.StartsWith('!');
            if (Enum.TryParse(hidden ? part[1..] : part, ignoreCase: true, out BookmarkColumn column) && list.All(c => c.Column != column))
            {
                list.Add((column, !hidden || column == BookmarkColumn.Name));
            }
        }

        foreach ((BookmarkColumn column, bool visible) in Defaults)
        {
            if (list.All(c => c.Column != column))
            {
                list.Add((column, visible));
            }
        }

        return list;
    }

    private static List<(BookmarkColumn Column, bool Visible)> Load() => Parse(AppState.GetString(StateKey, string.Empty));

    private static void Save(List<(BookmarkColumn Column, bool Visible)> list)
    {
        _current = list;
        AppState.SetString(StateKey, string.Join(',', list.Select(c => (c.Visible ? string.Empty : "!") + c.Column)));
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>
/// 一覧の行と見出しの列の配置 (<see cref="BookmarkColumns"/> と「すべてのドキュメント」のドキュメントの列から作る)。行のテンプレートは
/// 列ごとに 8 つの欄を持ち、この配置で各欄の位置・表示と欄の幅を決める (x:Bind で変更を受ける)。
/// </summary>
public sealed partial class BookmarkColumnLayout : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public const int Slots = 8;

    public static BookmarkColumnLayout Instance { get; } = new();

    private BookmarkColumnLayout()
    {
        BookmarkColumns.Changed += (_, _) => Update();
        Update();
    }

    private bool _showDocument;

    /// <summary>ドキュメントの列を出す (「すべてのドキュメント」)。</summary>
    public bool ShowDocument
    {
        get => _showDocument;
        set
        {
            if (_showDocument != value)
            {
                _showDocument = value;
                Update();
            }
        }
    }

    /// <summary>表示する列の並び (ドキュメントの列は先頭。null はドキュメントの列)。</summary>
    public IReadOnlyList<BookmarkColumn?> Order { get; private set; } = [];

    public int NumberColumn { get; private set; }

    public int ColorColumn { get; private set; }

    public int NameColumn { get; private set; }

    public int StartColumn { get; private set; }

    public int LengthColumn { get; private set; }

    public int GroupColumn { get; private set; }

    public int CommentColumn { get; private set; }

    public int DocumentColumn { get; private set; }

    public Microsoft.UI.Xaml.Visibility NumberVisibility { get; private set; }

    public Microsoft.UI.Xaml.Visibility ColorVisibility { get; private set; }

    public Microsoft.UI.Xaml.Visibility StartVisibility { get; private set; }

    public Microsoft.UI.Xaml.Visibility LengthVisibility { get; private set; }

    public Microsoft.UI.Xaml.Visibility GroupVisibility { get; private set; }

    public Microsoft.UI.Xaml.Visibility CommentVisibility { get; private set; }

    public Microsoft.UI.Xaml.Visibility DocumentVisibility { get; private set; }

    /// <summary>コメントの 1 行目を 2 行目に出す (コメントの列を出していないとき)。</summary>
    public Microsoft.UI.Xaml.Visibility CommentLineVisibility { get; private set; }

    /// <summary>2 行目 (コメント・削除の印) を置く欄と、またがる欄の数。</summary>
    public int DetailSpan { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width0 { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width1 { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width2 { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width3 { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width4 { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width5 { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width6 { get; private set; }

    public Microsoft.UI.Xaml.GridLength Width7 { get; private set; }

    private void Update()
    {
        List<BookmarkColumn?> order = [.. (ShowDocument ? [null] : Array.Empty<BookmarkColumn?>()), .. BookmarkColumns.Visible.Select(c => (BookmarkColumn?)c)];
        Order = order;
        var widths = new Microsoft.UI.Xaml.GridLength[Slots];
        for (int i = 0; i < Slots; i++)
        {
            widths[i] = new Microsoft.UI.Xaml.GridLength(0);
        }

        // 表示しない列は、使っていない欄 (幅 0) に置く。
        int unused = order.Count;
        int SlotOf(BookmarkColumn? column)
        {
            int index = order.IndexOf(column);
            return index >= 0 ? index : Math.Min(Slots - 1, unused++);
        }

        for (int i = 0; i < order.Count; i++)
        {
            double width = order[i] is { } c ? BookmarkColumns.WidthOf(c) : 100;
            widths[i] = double.IsNaN(width) ? new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) : new Microsoft.UI.Xaml.GridLength(width);
        }

        NumberColumn = SlotOf(BookmarkColumn.Number);
        ColorColumn = SlotOf(BookmarkColumn.Color);
        NameColumn = SlotOf(BookmarkColumn.Name);
        StartColumn = SlotOf(BookmarkColumn.Start);
        LengthColumn = SlotOf(BookmarkColumn.Length);
        GroupColumn = SlotOf(BookmarkColumn.Group);
        CommentColumn = SlotOf(BookmarkColumn.Comment);
        DocumentColumn = SlotOf(null);
        static Microsoft.UI.Xaml.Visibility V(bool on) => on ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        NumberVisibility = V(order.Contains(BookmarkColumn.Number));
        ColorVisibility = V(order.Contains(BookmarkColumn.Color));
        StartVisibility = V(order.Contains(BookmarkColumn.Start));
        LengthVisibility = V(order.Contains(BookmarkColumn.Length));
        GroupVisibility = V(order.Contains(BookmarkColumn.Group));
        CommentVisibility = V(order.Contains(BookmarkColumn.Comment));
        DocumentVisibility = V(ShowDocument);
        CommentLineVisibility = V(!order.Contains(BookmarkColumn.Comment));
        DetailSpan = Math.Max(1, order.Count - NameColumn);
        (Width0, Width1, Width2, Width3, Width4, Width5, Width6, Width7) = (widths[0], widths[1], widths[2], widths[3], widths[4], widths[5], widths[6], widths[7]);
        OnPropertyChanged(string.Empty);
    }
}
