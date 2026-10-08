using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.App.Views;
using HexEditor.Core.Commands;
using HexEditor.Core.View;

namespace HexEditor.App;

/// <summary>コマンドパレットの「@」モードの候補 (ブックマーク。05 が提供する)。</summary>
public sealed record PaletteBookmark(string Name, long Offset);

/// <summary>コマンドパレットの「ファイル」モードの候補 (最近使ったファイル。UI-32 が提供する)。</summary>
public sealed record PaletteRecentFile(string Name, string Path);

/// <summary>
/// コマンドパレット (UI-17) の入力の解釈: 接頭辞でモードを切り替え、候補を作る。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>「@」モードのブックマークの一覧 (ブックマーク INSP-* が設定する。未設定なら候補なし)。</summary>
    public static Func<DocumentViewModel, IEnumerable<PaletteBookmark>>? PaletteBookmarks { get; set; }

    /// <summary>「ファイル」モードの最近使ったファイル (UI-32 が設定する。未設定なら開いているタブだけ)。</summary>
    public static Func<IEnumerable<PaletteRecentFile>>? PaletteRecentFiles { get; set; }

    private List<CommandSearchItem>? _paletteItems;
    private CommandDefinition? _pendingArgument;

    private void InitializePalette()
    {
        Palette.Query = QueryPalette;
        Palette.Closed += (_, _) =>
        {
            _pendingArgument = null;
            FocusEditor();
        };
        CommandService.Catalog.Changed += (_, _) => _paletteItems = null;
        CommandService.BindingsChanged += () => _paletteItems = null;
    }

    /// <summary>パレットを開く。<paramref name="prefix"/> は初めの入力 (Ctrl+Shift+P / F1 はコマンドモードの「&gt;」)。</summary>
    public void OpenPalette(string prefix)
    {
        _pendingArgument = null;
        Palette.Open(prefix);
    }

    /// <summary>候補の検索用の文字列 (表示言語の表示名・英語名・別名・カテゴリ名)。コマンドが変わるまで使い回す。</summary>
    private List<CommandSearchItem> PaletteItems() => _paletteItems ??= [.. CommandService.Catalog.All.Where(c => !c.Hidden)
        .Select(c => new CommandSearchItem(c.Id, CommandService.CategoryName(c.Category), CommandService.DisplayName(c), CommandService.EnglishName(c), CommandService.Aliases(c)))];

    private static IComparer<string> DisplayOrder => StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true);

    private PaletteResult QueryPalette(string text)
    {
        if (_pendingArgument is { } waiting)
        {
            return ArgumentMode(waiting, text);
        }

        if (text.Length == 0)
        {
            return FilesMode(string.Empty);
        }

        return text[0] switch
        {
            '>' => CommandMode(text[1..]),
            ':' => OffsetMode(text[1..]),
            '@' => BookmarkMode(text[1..]),
            '#' => SettingsMode(text[1..]),
            '?' => HelpMode(),
            _ => FilesMode(text),
        };
    }

    private PaletteResult CommandMode(string query)
    {
        var items = PaletteItems();
        var entries = new List<PaletteEntry>();
        if (query.Trim().Length == 0)
        {
            (List<CommandSearchItem> recent, List<CommandSearchItem> others) = CommandSearch.EmptyQuery(items, CommandService.Recent.Ids, DisplayOrder);
            if (recent.Count > 0)
            {
                entries.Add(new PaletteEntry { Key = "header:recent", Title = Loc.Get("Palette_Recent"), IsHeader = true });
                entries.AddRange(recent.Select(i => CommandEntry(i, null)));
                entries.Add(new PaletteEntry { Key = "header:all", Title = Loc.Get("Palette_AllCommands"), IsHeader = true });
            }

            entries.AddRange(others.Select(i => CommandEntry(i, null)));
            return new PaletteResult(entries);
        }

        entries.AddRange(CommandSearch.Filter(items, query, DisplayOrder).Select(r => CommandEntry(r.Item, r)));
        return new PaletteResult(entries, entries.Count == 0 ? Loc.Get("Palette_NoMatch") : null);
    }

    private PaletteEntry CommandEntry(CommandSearchItem item, CommandSearchResult? match)
    {
        CommandState state = Commands.StateOf(item.Id);
        int offset = item.Title.Length - item.DisplayName.Original.Length;
        bool english = match?.Field == CommandMatchField.EnglishName && item.EnglishName.Original != item.DisplayName.Original;
        return new PaletteEntry
        {
            Key = "command:" + item.Id,
            Title = item.Title,
            Highlights = match?.Field == CommandMatchField.DisplayName ? [.. match.Match.Positions.Select(p => p + offset)]
                : match?.Field == CommandMatchField.Title ? match.Match.Positions : [],
            Secondary = english ? item.EnglishName.Original : null,
            SecondaryHighlights = english ? match!.Match.Positions : [],
            Shortcut = CommandService.ShortcutText(item.Id),
            Reason = state.Enabled ? string.Empty : state.Reason ?? string.Empty,
            Completion = ">" + item.DisplayName.Original,
            Invoke = () => InvokeFromPalette(item.Id),
        };
    }

    private async Task InvokeFromPalette(string id)
    {
        // 引数が必要なコマンドは、選んだ後に同じ入力欄で引数を尋ねる (UI-17 の仕様 7)。
        if (CommandService.Catalog.Find(id) is { Argument: not null } def && Commands.StateOf(id).Enabled)
        {
            _pendingArgument = def;
            Palette.Open(string.Empty);
            return;
        }

        await Commands.ExecuteAsync(id, fromPalette: true);
    }

    private PaletteResult ArgumentMode(CommandDefinition command, string text) =>
        new([new PaletteEntry
        {
            Key = "argument:" + command.Id,
            Title = Loc.Format("Palette_RunWith", CommandService.Title(command), text),
            Invoke = async () =>
            {
                _pendingArgument = null;
                await Commands.ExecuteAsync(command.Id, text, fromPalette: true);
            },
        }], Loc.Get(command.Argument!.PromptKey));

    /// <summary>「:」オフセットへ移動 (00-overview.md 6 章の入力式。Ctrl+G と同じ処理)。</summary>
    private PaletteResult OffsetMode(string text)
    {
        if (Editor is not { } editor)
        {
            return new PaletteResult([], Loc.Get("Command_NoDocument"), EnterEnabled: false);
        }

        if (text.Trim().Length == 0)
        {
            return new PaletteResult([], Loc.Get("Palette_OffsetHint"), EnterEnabled: false);
        }

        GoToResult r = GoToResolver.Resolve(text, GoToBase.Auto, GoToUnit.Bytes, editor);
        if (r.Error is { } error)
        {
            return new PaletteResult([], Loc.Format("GoTo_Error_" + error.Error, error.Detail), EnterEnabled: false);
        }

        if (r.OutOfRange)
        {
            return new PaletteResult([], r.Offset < 0 ? Loc.Get("GoTo_BeforeStart") : Loc.Format("GoTo_BeyondEnd", "0x" + editor.Layout.MaxCursor.ToString("X")), EnterEnabled: false);
        }

        long target = r.Offset;
        string offset = "0x" + target.ToString("X", CultureInfo.InvariantCulture);
        return new PaletteResult([new PaletteEntry
        {
            Key = "offset:" + offset,
            Title = Loc.Format("Palette_GoTo", offset, target.ToString("N0", CultureInfo.CurrentCulture)),
            Invoke = () =>
            {
                editor.GoTo(target);
                return Task.CompletedTask;
            },
        }]);
    }

    /// <summary>接頭辞なし: 開いているタブと最近使ったファイル。</summary>
    private PaletteResult FilesMode(string query)
    {
        string q = SearchText.Normalize(query.Trim());
        var entries = new List<PaletteEntry>();
        for (int i = 0; i < Vm.Documents.Count; i++)
        {
            DocumentViewModel doc = Vm.Documents[i];
            FuzzyMatch m = FuzzyMatcher.Match(new SearchText(doc.DisplayName), q);
            if (m.Success)
            {
                entries.Add(new PaletteEntry
                {
                    Key = "tab:" + i.ToString(CultureInfo.InvariantCulture),
                    Title = Loc.Format("Palette_Tab", doc.DisplayName),
                    Highlights = [.. m.Positions.Select(p => p + Loc.Format("Palette_Tab", string.Empty).Length)],
                    Shortcut = doc.FilePath ?? string.Empty,
                    Invoke = () =>
                    {
                        Vm.Selected = doc;
                        return Task.CompletedTask;
                    },
                });
            }
        }

        foreach (PaletteRecentFile file in PaletteRecentFiles?.Invoke() ?? [])
        {
            if (Vm.Documents.Any(d => string.Equals(d.FilePath, file.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            FuzzyMatch m = FuzzyMatcher.Match(new SearchText(file.Name), q);
            if (m.Success)
            {
                entries.Add(new PaletteEntry
                {
                    Key = "recent:" + file.Path,
                    Title = file.Name,
                    Highlights = m.Positions,
                    Shortcut = file.Path,
                    Invoke = () =>
                    {
                        TryOpen(file.Path);
                        return Task.CompletedTask;
                    },
                });
            }
        }

        return new PaletteResult(entries, entries.Count == 0 ? Loc.Get("Palette_FilesHint") : null);
    }

    private PaletteResult BookmarkMode(string query)
    {
        if (Vm.Selected is not { } doc)
        {
            return new PaletteResult([], Loc.Get("Command_NoDocument"), EnterEnabled: false);
        }

        string q = SearchText.Normalize(query.Trim());
        var entries = new List<PaletteEntry>();
        foreach (PaletteBookmark b in PaletteBookmarks?.Invoke(doc) ?? [])
        {
            FuzzyMatch m = FuzzyMatcher.Match(new SearchText(b.Name), q);
            if (m.Success)
            {
                entries.Add(new PaletteEntry
                {
                    Key = "bookmark:" + b.Name,
                    Title = b.Name,
                    Highlights = m.Positions,
                    Shortcut = "0x" + b.Offset.ToString("X", CultureInfo.InvariantCulture),
                    Invoke = () =>
                    {
                        doc.Editor.GoTo(b.Offset);
                        return Task.CompletedTask;
                    },
                });
            }
        }

        return new PaletteResult(entries, entries.Count == 0 ? Loc.Get("Palette_NoBookmarks") : null);
    }

    /// <summary>「#」設定: 設定項目を検索し、選ぶと設定画面のその項目を開く (UI-22)。</summary>
    private PaletteResult SettingsMode(string query)
    {
        var results = query.Trim().Length == 0
            ? CommandService.Settings.All.Where(s => s.ShowInPage).Select(s => new Core.Settings.SettingSearchResult(s, [])).ToList()
            : CommandService.Settings.Search(query, SettingsPage.Texts);
        var entries = results.Select(r =>
        {
            string category = SettingsPage.CategoryName(r.Setting.Category);
            string title = Loc.Format("Command_Title", category, Loc.Get(r.Setting.NameKey));
            int offset = title.Length - Loc.Get(r.Setting.NameKey).Length;
            return new PaletteEntry
            {
                Key = "setting:" + r.Setting.Key,
                Title = title,
                Highlights = [.. r.NamePositions.Select(p => p + offset)],
                Shortcut = r.Setting.Key,
                Invoke = () =>
                {
                    OpenSettingsPage(settingKey: r.Setting.Key);
                    return Task.CompletedTask;
                },
            };
        }).ToList();
        return new PaletteResult(entries, entries.Count == 0 ? Loc.Get("Palette_NoMatch") : null);
    }

    /// <summary>「?」: 接頭辞の一覧。</summary>
    private PaletteResult HelpMode() =>
        new([.. new[] { (">", "Palette_HelpCommands"), (string.Empty, "Palette_HelpFiles"), (":", "Palette_HelpOffset"), ("@", "Palette_HelpBookmarks"), ("#", "Palette_HelpSettings") }
            .Select(p => new PaletteEntry
            {
                Key = "help:" + p.Item1,
                Title = Loc.Get(p.Item2),
                Shortcut = p.Item1,
                Invoke = () =>
                {
                    DispatcherQueue.TryEnqueue(() => Palette.Open(p.Item1));
                    return Task.CompletedTask;
                },
            })]);
}
