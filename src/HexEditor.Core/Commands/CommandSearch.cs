namespace HexEditor.Core.Commands;

/// <summary>どの欄で一致したか (UI-17 の仕様 4)。</summary>
public enum CommandMatchField
{
    DisplayName,
    EnglishName,
    Alias,
    Category,
    Id,
}

/// <summary>
/// コマンドパレットの候補 1 つ。表示名・英語名・カテゴリ名・別名は表示言語で解決したもの (App が作る)。
/// 正規化した文字列を前もって作っておき、1 文字入力するごとの絞り込みを速くする (UI-17 の仕様 9)。
/// </summary>
public sealed class CommandSearchItem
{
    /// <param name="menuNames">メニューの項目の表示名 (アクセスキーの「(X)」を除く)。メニューと同じ名前で見つかるように、別名と同じく
    /// 一致の対象にする (UI-03 の仕様 3 の受け入れ基準)。</param>
    public CommandSearchItem(string id, string category, string displayName, string englishName, string aliases, IEnumerable<string>? menuNames = null)
    {
        Id = id;
        Category = new SearchText(category);
        DisplayName = new SearchText(displayName);
        EnglishName = new SearchText(englishName);
        Aliases = [.. aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => new SearchText(a)),
            .. (menuNames ?? []).Where(n => n.Length > 0).Select(n => new SearchText(n))];
        IdText = new SearchText(id);
    }

    public string Id { get; }

    public SearchText Category { get; }

    public SearchText DisplayName { get; }

    public SearchText EnglishName { get; }

    public IReadOnlyList<SearchText> Aliases { get; }

    public SearchText IdText { get; }

    /// <summary>「カテゴリ: 表示名」(候補の表示。UI-17 の仕様 5)。</summary>
    public string Title => $"{Category.Original}: {DisplayName.Original}";
}

/// <summary>絞り込みの結果 1 つ。<see cref="Match"/> の位置は一致した欄 (表示名か英語名) の文字列の中の位置。</summary>
public sealed record CommandSearchResult(CommandSearchItem Item, CommandMatchField Field, FuzzyMatch Match);

/// <summary>コマンドパレットのコマンドモードの絞り込み (UI-17 の仕様 3、4、6)。</summary>
public static class CommandSearch
{
    /// <summary>最近使ったコマンドを空の入力のときに出す件数。</summary>
    public const int RecentShown = 10;

    /// <summary>
    /// 入力に一致する候補を点数の順に返す。表示名・英語名・コマンド ID・カテゴリ名・別名のどれかに一致すれば候補にする。
    /// 同じ点数なら <paramref name="order"/> (表示言語の並び順) で並べる。
    /// </summary>
    public static List<CommandSearchResult> Filter(IReadOnlyList<CommandSearchItem> items, string query, IComparer<string> order)
    {
        string q = SearchText.Normalize(query.Trim());
        var results = new List<CommandSearchResult>();
        foreach (CommandSearchItem item in items)
        {
            if (Best(item, q) is { } result)
            {
                results.Add(result);
            }
        }

        results.Sort((a, b) =>
        {
            int c = b.Match.Score.CompareTo(a.Match.Score);
            return c != 0 ? c : order.Compare(a.Item.Title, b.Item.Title);
        });
        return results;
    }

    /// <summary>
    /// 入力が空のときの並び (UI-17 の仕様 6): 最近使ったコマンド (最大 10 件、新しい順) と、それ以外の全コマンド
    /// (表示言語の並び順)。最近使ったものは <paramref name="recent"/> の 2 つ目の結果に入れる。
    /// </summary>
    public static (List<CommandSearchItem> Recent, List<CommandSearchItem> Others) EmptyQuery(
        IReadOnlyList<CommandSearchItem> items, IEnumerable<string> recent, IComparer<string> order)
    {
        var byId = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var recentItems = recent.Select(id => byId.GetValueOrDefault(id)).OfType<CommandSearchItem>().Take(RecentShown).ToList();
        var others = items.Except(recentItems).OrderBy(i => i.Title, order).ToList();
        return (recentItems, others);
    }

    private static CommandSearchResult? Best(CommandSearchItem item, string q)
    {
        CommandSearchResult? best = null;
        void Try(SearchText text, CommandMatchField field, int penalty)
        {
            FuzzyMatch m = FuzzyMatcher.Match(text, q);
            if (m.Success)
            {
                var candidate = new CommandSearchResult(item, field, m with { Score = m.Score - penalty });
                if (best is null || candidate.Match.Score > best.Match.Score)
                {
                    best = candidate;
                }
            }
        }

        // 同じ一致の種類なら、表示名 > 英語名 > 別名 > カテゴリ > ID の順に優先する。
        Try(item.DisplayName, CommandMatchField.DisplayName, 0);
        Try(item.EnglishName, CommandMatchField.EnglishName, 1);
        foreach (SearchText alias in item.Aliases)
        {
            Try(alias, CommandMatchField.Alias, 2);
        }

        Try(item.Category, CommandMatchField.Category, 3);
        Try(item.IdText, CommandMatchField.Id, 4);
        return best;
    }
}

/// <summary>最近使ったコマンド (UI-17 の仕様 6)。新しい順に最大 50 件。state.json に保存する。</summary>
public sealed class RecentCommands
{
    public const int MaxStored = 50;

    /// <summary>state.json のキー。</summary>
    public const string StateKey = "commandPalette.recent";

    private readonly List<string> _ids = [];

    public RecentCommands(IEnumerable<string>? ids = null)
    {
        if (ids is not null)
        {
            _ids.AddRange(ids.Distinct(StringComparer.Ordinal).Take(MaxStored));
        }
    }

    public IReadOnlyList<string> Ids => _ids;

    public void Clear() => _ids.Clear();

    public void Add(string id)
    {
        _ids.Remove(id);
        _ids.Insert(0, id);
        if (_ids.Count > MaxStored)
        {
            _ids.RemoveRange(MaxStored, _ids.Count - MaxStored);
        }
    }
}
