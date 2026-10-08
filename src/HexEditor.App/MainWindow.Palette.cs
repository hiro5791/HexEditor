using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.App.Views;
using HexEditor.Core.Commands;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

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
            _pendingPick = null;
            FocusEditor();
        };

        // 候補の右クリック >「ショートカットを変更」(UI-18 の呼び出し)。
        Palette.ChangeShortcutRequested += (_, id) => OpenKeyboardSettingsFor(id);

        // アプリ全体のイベントは、ウィンドウを本当に閉じたときに外す。
        EventHandler catalogChanged = (_, _) => _paletteItems = null;
        Action bindingsChanged = () =>
        {
            _paletteItems = null;
            DispatcherQueue.TryEnqueue(UpdatePaletteButton);
        };
        CommandService.Catalog.Changed += catalogChanged;
        CommandService.BindingsChanged += bindingsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                CommandService.Catalog.Changed -= catalogChanged;
                CommandService.BindingsChanged -= bindingsChanged;
            }
        };

        InitializePaletteButton();
    }

    // ---- タイトルバーの入口 (UI-02 の仕様 4) ----

    /// <summary>この幅 (DIP) より狭いウィンドウでは、入口を虫眼鏡のアイコンだけにする。</summary>
    private const double PaletteButtonCompactWidth = 900;

    private Button? _paletteButton;
    private TextBlock? _paletteButtonText;

    /// <summary>
    /// タイトルバーのコマンドパレットの入口: 幅 240〜400 px のボタンに「コマンドを検索 (Ctrl+Shift+P)」(今の割り当てのキー) を
    /// 表示し、押すとコマンドモード (「&gt;」) で開く。ウィンドウ幅が 900 px 未満では虫眼鏡のアイコンだけにする。
    /// TitleBar の Content に置くので、ドラッグ領域から除かれる (仕様 5)。
    /// </summary>
    private void InitializePaletteButton()
    {
        _paletteButtonText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        // 文字列が入りきらない (翻訳が長い・メニューバーが広い) ときは、切れた文字列を出さずにアイコンだけにする。
        _paletteButtonText.IsTextTrimmedChanged += (_, _) =>
        {
            if (_paletteButtonText.IsTextTrimmed && !_paletteTextOverflow)
            {
                _paletteTextOverflow = true;
                UpdatePaletteButton();
            }
        };
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        Grid.SetColumn(_paletteButtonText, 1);
        content.Children.Add(_paletteButtonText);
        _paletteButton = new Button
        {
            Content = content,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0, 10, 0),
        };
        AutomationProperties.SetAutomationId(_paletteButton, "TitleBar_Palette");
        _paletteButton.Click += (_, _) => OpenPalette(">");
        AppTitleBar.Content = _paletteButton;
        Root.SizeChanged += (_, _) =>
        {
            // 幅が変わったら、文字列が入るかをもう一度確かめる。
            _paletteTextOverflow = false;
            UpdatePaletteButton();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_paletteButtonText.IsTextTrimmed && _paletteButtonText.Visibility == Visibility.Visible && !_paletteTextOverflow)
                {
                    _paletteTextOverflow = true;
                    UpdatePaletteButton();
                }
            });
        };
        UpdatePaletteButton();
    }

    /// <summary>入口の文字列が入りきらなかった (次にウィンドウの幅が変わるまでアイコンだけにする)。</summary>
    private bool _paletteTextOverflow;

    /// <summary>入口の表示 (キーの表記と、ウィンドウの幅による表示の切り替え) を更新する。</summary>
    private void UpdatePaletteButton()
    {
        if (_paletteButton is null || _paletteButtonText is null)
        {
            return;
        }

        KeyChord? first = CommandService.Keys.BindingsFor("help.commandPalette").Select(b => b.Binding.Chord).FirstOrDefault();
        string text = first is null ? Loc.Get("TitleBar_PaletteNoKey") : Loc.Format("TitleBar_Palette", KeyboardLayout.Format(first));
        bool compact = (Root.ActualWidth > 0 && Root.ActualWidth < PaletteButtonCompactWidth) || _paletteTextOverflow;
        _paletteButtonText.Text = text;
        _paletteButtonText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _paletteButton.MinWidth = compact ? 0 : 240;
        _paletteButton.MaxWidth = compact ? 48 : 400;
        _paletteButton.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        AutomationProperties.SetName(_paletteButton, text);
        ToolTipService.SetToolTip(_paletteButton, text);
    }

    /// <summary>入口の状態 (テスト用の命令)。</summary>
    internal (string Text, bool Compact, double Width) PaletteButtonState =>
        (_paletteButtonText?.Text ?? string.Empty, _paletteButtonText?.Visibility != Visibility.Visible, _paletteButton?.ActualWidth ?? 0);

    /// <summary>パレットを開く。<paramref name="prefix"/> は初めの入力 (Ctrl+Shift+P / F1 はコマンドモードの「&gt;」)。</summary>
    public void OpenPalette(string prefix)
    {
        _pendingArgument = null;
        _pendingPick = null;
        Palette.Open(prefix);
    }

    /// <summary>候補の検索用の文字列 (表示言語の表示名・英語名・別名・カテゴリ名)。コマンドが変わるまで使い回す。</summary>
    private List<CommandSearchItem> PaletteItems() => _paletteItems ??= [.. CommandService.Catalog.All.Where(c => !c.Hidden)
        .Select(c => new CommandSearchItem(c.Id, CommandService.CategoryName(c.Category), CommandService.DisplayName(c), CommandService.EnglishName(c), CommandService.Aliases(c),
            _menus?.Items.Where(i => i.Id == c.Id).Select(i => MenuItemName(i.Item.Text))))];

    /// <summary>メニューの項目の表示名から、日本語のアクセスキーの「(X)」と末尾の「…」を除く。</summary>
    private static string MenuItemName(string text)
    {
        string name = System.Text.RegularExpressions.Regex.Replace(text, @"\([A-Za-z0-9]\)$", string.Empty);
        return name.TrimEnd('…').TrimEnd('.').Trim();
    }

    private static IComparer<string> DisplayOrder => StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true);

    /// <summary>選ぶための一覧をパレットに出している間の検索 (ジャンプ履歴の一覧など)。閉じたら戻す。</summary>
    private Func<string, PaletteResult>? _pendingPick;

    /// <summary>「移動: 履歴の一覧」(VIEW-31 の仕様 8)。最新 20 件を「アドレス + その位置から 8 バイトの Hex」で出す。</summary>
    private void ShowHistoryInPalette()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        _pendingArgument = null;
        _pendingPick = query =>
        {
            IReadOnlyList<JumpPoint> recent = editor.RecentJumps;
            var entries = new List<PaletteEntry>();
            for (int i = 0; i < recent.Count; i++)
            {
                int index = i;
                string title = HistoryEntryText(editor, recent[i]);
                if (query.Trim().Length > 0 && !title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                entries.Add(new PaletteEntry
                {
                    Key = "history:" + i,
                    Title = title,
                    Invoke = () =>
                    {
                        _pendingPick = null;
                        if (Editor == editor)
                        {
                            editor.GoBackTo(index);
                        }

                        return Task.CompletedTask;
                    },
                });
            }

            return new PaletteResult(entries, entries.Count == 0 ? Loc.Get("Palette_NoMatch") : null);
        };
        Palette.Open(string.Empty);
    }

    private PaletteResult QueryPalette(string text)
    {
        if (_pendingPick is { } pick)
        {
            return pick(text);
        }

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
            CommandId = item.Id,
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

    /// <summary>
    /// 引数を尋ねる (UI-17 の仕様 7)。選べる値の一覧があるコマンド (文字コード・配色) は、入力で絞り込んだ一覧から選ぶ。
    /// 入力を確かめられるコマンド (1 行のバイト数) は、誤りを一覧の上に出して Enter を無効にする。
    /// </summary>
    private PaletteResult ArgumentMode(CommandDefinition command, string text)
    {
        // 案内は表示言語のリソース CmdArg_<ID の . を _ に>。
        string prompt = Loc.Get("CmdArg_" + CommandDefinition.KeyPart(command.Id));
        Func<Task> Run(string argument) => async () =>
        {
            _pendingArgument = null;
            await Commands.ExecuteAsync(command.Id, argument, fromPalette: true);
        };

        if (ArgumentChoices(command.Id) is { } choices)
        {
            string q = SearchText.Normalize(text.Trim());
            var entries = new List<PaletteEntry>();
            foreach ((string value, string label) in choices)
            {
                FuzzyMatch m = FuzzyMatcher.Match(new SearchText(label), q);
                if (m.Success || FuzzyMatcher.Match(new SearchText(value), q).Success)
                {
                    entries.Add(new PaletteEntry
                    {
                        Key = "argument:" + command.Id + ":" + value,
                        Title = label,
                        Highlights = m.Success ? m.Positions : [],
                        Shortcut = value,
                        Invoke = Run(value),
                    });
                }
            }

            return new PaletteResult(entries, entries.Count == 0 ? Loc.Get("Palette_NoMatch") : prompt, EnterEnabled: entries.Count > 0);
        }

        if (text.Trim().Length == 0)
        {
            return new PaletteResult([], prompt, EnterEnabled: false);
        }

        if (ArgumentError(command.Id, text) is { } error)
        {
            return new PaletteResult([], error, EnterEnabled: false);
        }

        return new PaletteResult([new PaletteEntry
        {
            Key = "argument:" + command.Id,
            Title = Loc.Format("Palette_RunWith", CommandService.Title(command), text),
            Invoke = Run(text),
        }], prompt);
    }

    /// <summary>引数の選べる値 (値と表示名)。一覧から選ぶコマンドでなければ null。</summary>
    private IReadOnlyList<(string Value, string Label)>? ArgumentChoices(string id) => id switch
    {
        EncodingSelectCommand => [.. EncodingCatalog.All.Where(e => e.Selectable).Select(e => (e.Id, EncodingDisplayText(e)))],
        ColorSchemeCommand => [.. new ColorSchemeStore(App.Settings.Folder).All().Select(s => (s.Name, s.BuiltIn ? Loc.Get("Scheme_" + s.Name) : s.Name))],
        _ => null,
    };

    /// <summary>入力した引数の誤り (なければ null)。</summary>
    private string? ArgumentError(string id, string text) => id switch
    {
        "view.bytesPerRowCustom" => BytesPerRowArgumentError(text, out _),
        _ => null,
    };

    /// <summary>1 行のバイト数の入力を確かめる (「表示 > 1 行のバイト数 > 指定…」と同じ規則。VIEW-08)。</summary>
    private string? BytesPerRowArgumentError(string text, out int bytesPerRow)
    {
        bytesPerRow = 0;
        if (Editor is not { } editor)
        {
            return Loc.Get("Command_NoDocument");
        }

        if (!TryEvaluate(text, editor, DefaultRadix.Decimal, out long value))
        {
            return Loc.Get("ViewInput_Invalid");
        }

        string? error = editor.View.ValidateBytesPerRow(value) switch
        {
            BytesPerRowError.OutOfRange => Loc.Get("ViewInput_BytesPerRowRange"),
            BytesPerRowError.NotMultipleOfGroup => Loc.Format("ViewInput_BytesPerRowGroup", editor.View.RowUnit),
            _ => null,
        };
        bytesPerRow = (int)value;
        return error;
    }

    /// <summary>
    /// 引数を受け取る処理を足す (表示のメニューが登録した処理を包む)。引数がなければ元の処理 (入力のダイアログ) を呼ぶ。
    /// RegisterCommandHandlers の最後 (表示のメニューの登録の後) に呼ぶ。
    /// </summary>
    private void RegisterArgumentHandlers()
    {
        if (Commands.HandlerOf("view.bytesPerRowCustom") is { } original)
        {
            Commands.Register("view.bytesPerRowCustom", new CommandHandler(argument =>
            {
                if (argument is not { Length: > 0 } text)
                {
                    return original.Execute(null);
                }

                if (BytesPerRowArgumentError(text, out int bytesPerRow) is { } error)
                {
                    ShowNotice(error, InfoBarSeverity.Warning);
                }
                else if (Editor is { } editor)
                {
                    editor.ApplyView(editor.View with { BytesPerRow = bytesPerRow, AutoBytesPerRow = false });
                    UpdateViewMenu();
                }

                return Task.CompletedTask;
            }, original.State));
        }
    }

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
            ? CommandService.Settings.All.Where(CommandService.Settings.IsShown).Select(s => new Core.Settings.SettingSearchResult(s, [])).ToList()
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
