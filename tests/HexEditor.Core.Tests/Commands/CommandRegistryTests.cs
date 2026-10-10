using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HexEditor.Core.Commands;
using HexEditor.Core.Tests.Engine;
using HexEditor.Core.Tests.I18n;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Commands;

/// <summary>
/// コマンド登録 (UI-16)、コマンドパレットの検索 (UI-17)、ショートカット一覧 (UI-39)。絞り込みの時間 (TC-UI-17-03) を計るので、
/// 他のテストと並列に動かさない。
/// </summary>
[Collection(SerialCollection.Name)]
public sealed partial class CommandRegistryTests
{
    private static string AppFolder => Path.GetDirectoryName(SourceTests.FindRepoFile("src/HexEditor.App/HexEditor.App.csproj"))!;

    private static Dictionary<string, string> Strings(string language) =>
        ResourceChecker.LoadResw(Path.Combine(AppFolder, "Strings", language, "Resources.resw"));

    [Fact]
    [Trait(TC, "TC-UI-16-01")]
    public void Every_menu_item_refers_to_a_registered_command_and_ids_are_unique()
    {
        var catalog = CommandCatalog.CreateBuiltIn();

        // コマンド ID の重複がない (登録で例外になる) ことと、形式。
        Assert.Equal(BuiltInCommands.All.Count, BuiltInCommands.All.Select(c => c.Id).Distinct().Count());
        Assert.All(catalog.All, c => Assert.Matches(@"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$", c.Id));
        Assert.Throws<InvalidOperationException>(() => catalog.Register(new CommandDefinition("file.open", "file")));
        Assert.Throws<ArgumentException>(() => new CommandDefinition("File.Open", "file"));

        // メインメニュー・タブの右クリックメニュー・ツールバーの右クリックメニューのすべての項目がコマンド ID を持つ。
        // (Hex ビューの右クリックメニューはコードで作る: HexView.Input.cs の CreateContextMenu。VIEW の担当で、フェーズ 1 の
        // 間はコマンド ID ではなく編集のコマンドを直接呼ぶ。)
        XDocument xaml = XDocument.Load(Path.Combine(AppFolder, "MainWindow.xaml"));
        XName id = XName.Get("CommandUi.Id", "using:HexEditor.App.Commands");
        var items = xaml.Descendants().Where(e => e.Name.LocalName is "MenuFlyoutItem" or "ToggleMenuFlyoutItem" or "RadioMenuFlyoutItem").ToList();
        Assert.True(items.Count > 30);
        var errors = items.Where(e => e.Attribute(id) is not { } a || !catalog.Contains(a.Value))
            .Select(e => $"{e.Attribute(XName.Get("Uid", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value ?? e.ToString()}: {e.Attribute(id)?.Value ?? "コマンド ID なし"}")
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    [Trait(TC, "TC-UI-16-02")]
    public void Every_command_has_an_english_name_and_display_name_keys()
    {
        Dictionary<string, string> en = Strings("en"), ja = Strings("ja");
        var errors = new List<string>();
        foreach (CommandDefinition c in BuiltInCommands.All)
        {
            // 引数を尋ねるコマンドは、入力欄の案内 (CmdArg_…) も持つ (UI-17 の仕様 7)。
            string[] keys = c.Argument is null ? [c.NameKey, c.AliasKey, c.CategoryKey] : [c.NameKey, c.AliasKey, c.CategoryKey, "CmdArg_" + CommandDefinition.KeyPart(c.Id)];
            foreach (string key in keys)
            {
                if (!en.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
                {
                    errors.Add($"en: {key}");
                }

                if (!ja.ContainsKey(key))
                {
                    errors.Add($"ja: {key}");
                }
            }

            // ja の別名には表示名の読み (ひらがな) を必ず置く (UI-16 の仕様 1)。
            if (ja.TryGetValue(c.AliasKey, out string? alias) && !alias.Any(ch => ch is >= 'ぁ' and <= 'ゖ'))
            {
                errors.Add($"ja: {c.AliasKey} に読み (ひらがな) がありません");
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        // 訳のない言語では英語の表示名を使う (MRT のフォールバック。未翻訳の 21 言語は CI の機械翻訳で埋まる。UI-49)。
        Dictionary<string, string> de = Strings("de");
        string missing = BuiltInCommands.All.Select(c => c.NameKey).FirstOrDefault(k => !de.ContainsKey(k)) ?? BuiltInCommands.All[0].NameKey;
        Assert.False(string.IsNullOrEmpty(de.GetValueOrDefault(missing) ?? en[missing]));
    }

    private static List<CommandSearchItem> Items(string language)
    {
        Dictionary<string, string> en = Strings("en");
        Dictionary<string, string> local = Strings(language);
        string Get(string key) => local.GetValueOrDefault(key) ?? en[key];
        return [.. BuiltInCommands.All.Select(c => new CommandSearchItem(c.Id, Get(c.CategoryKey), Get(c.NameKey), en[c.NameKey], Get(c.AliasKey)))];
    }

    [Fact]
    [Trait(TC, "TC-UI-17-02")]
    public void Japanese_ui_finds_commands_by_english_name_and_reading()
    {
        var items = Items("ja");
        IComparer<string> order = StringComparer.Create(new CultureInfo("ja-JP"), ignoreCase: true);

        List<CommandSearchResult> open = CommandSearch.Filter(items, "open", order);
        CommandSearchResult result = Assert.Single(open, r => r.Item.Id == "file.open");
        Assert.Equal("ファイル: 開く", result.Item.Title);
        Assert.Equal(CommandMatchField.EnglishName, result.Field);
        Assert.Equal("Open", result.Item.EnglishName.Original);
        Assert.Equal([0, 1, 2, 3], result.Match.Positions);

        Assert.Contains(CommandSearch.Filter(items, "ひらく", order), r => r.Item.Id == "file.open");
        Assert.Contains(CommandSearch.Filter(items, "ヒラク", order), r => r.Item.Id == "file.open");
    }

    [Fact]
    public void Fuzzy_matching_ranks_prefix_word_start_contiguous_scattered()
    {
        static FuzzyMatch M(string text, string q) => FuzzyMatcher.Match(new SearchText(text), SearchText.Normalize(q));
        Assert.Equal(MatchKind.Prefix, M("Go to offset", "go").Kind);
        Assert.Equal(MatchKind.WordStart, M("Go to offset", "off").Kind);
        Assert.Equal(MatchKind.Contiguous, M("Go to offset", "ffs").Kind);
        Assert.Equal(MatchKind.Scattered, M("Go to offset", "gtof").Kind);
        Assert.False(M("Go to offset", "xyz").Success);
        Assert.True(M("Go to offset", "go").Score > M("Go to offset", "off").Score);

        // 全角・アクセント記号は無視する。
        Assert.True(M("Café", "ＣＡＦＥ").Success);

        var items = new List<CommandSearchItem>
        {
            new("a.scattered", "A", "Overflow offset", "Overflow offset", string.Empty),
            new("a.prefix", "A", "Offset", "Offset", string.Empty),
            new("a.word", "A", "Go to offset", "Go to offset", string.Empty),
        };
        Assert.Equal(["a.prefix", "a.word", "a.scattered"], CommandSearch.Filter(items, "offs", StringComparer.Ordinal).Select(r => r.Item.Id).Take(3));
    }

    [Fact]
    [Trait(TC, "TC-UI-17-03")]
    public void Filtering_a_thousand_commands_takes_under_16ms_per_keystroke()
    {
        var items = Enumerable.Range(1, 1000).Select(i => new CommandSearchItem($"script.test{i:0000}", "Script", $"Test command {i:0000}", $"Test command {i:0000}", string.Empty))
            .Concat(Items("en")).ToList();
        IComparer<string> order = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);
        // 入力の長さごとに通る処理を先に一度ずつ動かしておく (JIT の分を計らない)。
        for (int length = 1; length <= "test05".Length; length++)
        {
            CommandSearch.Filter(items, "test05"[..length], order);
        }

        var times = new List<double>();
        for (int round = 0; round < 20; round++)
        {
            string typed = string.Empty;
            foreach (char c in "test05")
            {
                typed += c;

                // 並列に動く他のテストの GC などの揺れを除くため、各入力を 3 回測って最小を取る。
                double best = double.MaxValue;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var sw = Stopwatch.StartNew();
                    List<CommandSearchResult> r = CommandSearch.Filter(items, typed, order);
                    sw.Stop();
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                    Assert.NotEmpty(r);
                }

                times.Add(best);
            }
        }

        Assert.True(times.Max() < 16, $"最大 {times.Max():F2} ms");
    }

    [Fact]
    [Trait(TC, "TC-UI-17-05")]
    public void Recent_commands_are_newest_first_and_survive_a_restart()
    {
        string dir = Directory.CreateTempSubdirectory("hexeditor-state").FullName;
        try
        {
            using (var state = new HexEditor.Core.Settings.StateStore(dir))
            {
                state.Load();
                var recent = new RecentCommands();
                recent.Add("file.open");
                recent.Add("view.toolbar");
                state.Set(RecentCommands.StateKey, new System.Text.Json.Nodes.JsonArray([.. recent.Ids.Select(i => (System.Text.Json.Nodes.JsonNode?)i)]));
            }

            using var again = new HexEditor.Core.Settings.StateStore(dir);
            again.Load();
            var ids = again.Get(RecentCommands.StateKey)!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Equal(["view.toolbar", "file.open"], ids);
            (List<CommandSearchItem> first, _) = CommandSearch.EmptyQuery(Items("en"), ids, StringComparer.Ordinal);
            Assert.Equal("view.toolbar", first[0].Id);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var many = new RecentCommands();
        for (int i = 0; i < 60; i++)
        {
            many.Add("c.c" + i);
        }

        Assert.Equal(RecentCommands.MaxStored, many.Ids.Count);
        Assert.Equal("c.c59", many.Ids[0]);
    }

    [Fact]
    [Trait(TC, "TC-UI-17-06")]
    public void Turkish_culture_gives_the_same_results_for_i_and_I()
    {
        var items = Items("en");
        items.Add(new CommandSearchItem("view.panel.dataInspector", "View", "Data inspector", "Data inspector", "INSPECTOR"));
        items.Add(new CommandSearchItem("help.info", "Help", "Information", "Information", string.Empty));
        CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        try
        {
            var sets = new Dictionary<string, string>();
            foreach (string name in new[] { "en-US", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
                IComparer<string> order = StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true);
                foreach (string q in new[] { "inspector", "INSPECTOR", "info", "INFO" })
                {
                    sets[$"{name}:{q}"] = string.Join(",", CommandSearch.Filter(items, q, order).Select(r => r.Item.Id).Order(StringComparer.Ordinal));
                }
            }

            foreach (string q in new[] { "inspector", "INSPECTOR", "info", "INFO" })
            {
                Assert.Equal(sets["en-US:" + q], sets["tr-TR:" + q]);
            }

            Assert.Equal(sets["tr-TR:inspector"], sets["tr-TR:INSPECTOR"]);
            Assert.Equal(sets["tr-TR:info"], sets["tr-TR:INFO"]);
            Assert.Contains("view.panel.dataInspector", sets["tr-TR:INSPECTOR"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }

    [Fact]
    [Trait(TC, "TC-UI-39-02")]
    public void Html_export_has_a_heading_for_every_category()
    {
        var keys = new KeyMap(CommandCatalog.CreateBuiltIn());
        var rows = ShortcutList.Build(keys, c => c.Id, cat => "Category " + cat, k => k.ToString(), s => KeyScopes.Name(s), includeUnassigned: false);
        string html = ShortcutList.ToHtml(rows, "Shortcuts <&>", "Key", "Command", "Scope", "en");
        XDocument doc = XDocument.Parse(html.Replace("<!DOCTYPE html>", string.Empty));
        var headings = doc.Descendants("h2").Select(h => h.Value).ToList();
        Assert.Equal(rows.Select(r => r.Category).Distinct(), headings);
        foreach (string category in new[] { "file", "edit", "search", "go", "view", "settings", "help" })
        {
            Assert.Contains("Category " + category, headings);
        }

        Assert.Contains("Shortcuts &lt;&amp;&gt;", html);
        Assert.DoesNotContain(rows, r => !r.HasKeys);
        Assert.Contains(ShortcutList.Build(keys, c => c.Id, c => c, k => k.ToString(), s => KeyScopes.Name(s), includeUnassigned: true), r => !r.HasKeys);
    }

    [Fact]
    public void Shortcut_rows_can_be_found_by_key_text()
    {
        var keys = new KeyMap(CommandCatalog.CreateBuiltIn());
        var rows = ShortcutList.Build(keys, c => c.Id, c => c, k => k.ToString(), s => KeyScopes.Name(s), false);
        ShortcutRow goTo = rows.Single(r => r.CommandId == "go.goTo");
        Assert.True(ShortcutList.Matches(goTo, "ctrl+g", "Go to offset", "Ctrl+G"));
        Assert.True(ShortcutList.Matches(goTo, "offset", "Go to offset", "Ctrl+G"));
        Assert.False(ShortcutList.Matches(goTo, "ctrl+q", "Go to offset", "Ctrl+G"));
    }

    // ---- TC-UI-39-03: 既定の割り当てと 00-overview.md 8 章の表の突き合わせ ----

    /// <summary>
    /// 8 章の表のコマンドの欄 → コマンド ID (キーが複数ある欄は、キーと同じ数の ID を順に。1 つならすべてのキーに)。
    /// まだコマンドがない欄 (後のフェーズ・別の担当の機能、部品が自分で処理するキー) は理由を書く。コマンドを登録したら ID に直す。
    /// </summary>
    private static readonly Dictionary<string, string[]> TableCommands = new()
    {
        ["新規作成"] = ["file.new"],
        ["開く"] = ["file.open"],
        ["ディスクを開く"] = ["file.openDisk"],
        ["プロセスのメモリを開く"] = ["file.openProcess"],
        ["新しいウィンドウ"] = ["window.new"],
        ["保存"] = ["file.save"],
        ["名前を付けて保存"] = ["file.saveAs"],
        ["再読み込み"] = ["file.reload"],
        ["タブを閉じる"] = ["file.close"],
        ["閉じたタブを開き直す"] = ["file.reopenClosed"],
        ["次 / 前のタブ (最近使った順)"] = ["tab.next", "tab.previous"],
        ["次 / 前のタブ (並び順)"] = ["tab.nextInOrder", "tab.previousInOrder"],
        ["次 / 前の領域へフォーカスを移す"] = ["view.nextRegion", "view.previousRegion"],
        ["印刷"] = ["-F4-06 (フェーズ 4)"],
        ["元に戻す"] = ["edit.undo"],
        ["やり直し"] = ["edit.redo"],
        ["スクリプトを実行"] = ["-F3-05 (フェーズ 3)"],
        ["検索"] = ["search.find"],
        ["次を検索 / 前を検索"] = ["search.findNext", "search.findPrevious"],
        ["置換"] = ["search.replace"],
        ["複数ファイル検索"] = ["search.multiFile"],
        ["オフセットへ移動"] = ["go.goTo"],
        ["戻る / 進む"] = ["go.back", "go.forward"],
        ["ブックマークの設定 / 解除"] = ["go.bookmark.toggle"],
        ["次 / 前のブックマーク"] = ["go.bookmark.next", "go.bookmark.previous"],
        ["番号付きブックマークの設定"] = [.. Enumerable.Range(1, 9).Select(n => $"go.bookmark.set{n}")],
        ["番号付きブックマークへ移動"] = [.. Enumerable.Range(1, 9).Select(n => $"go.bookmark.goto{n}")],
        ["次 / 前の差分"] = ["compare.nextDiff", "compare.previousDiff"],
        ["定義へ移動 (テンプレート) / 分岐先へ移動 (逆アセンブル)"] = ["-F3-01 / F4-01"],
        ["コマンドパレット"] = ["help.commandPalette"],
        ["設定"] = ["settings.open"],
        ["拡大 / 縮小 / 100% に戻す"] = ["view.zoomIn", "view.zoomOut", "view.zoomReset"],
        ["全画面表示"] = ["view.fullScreen"],
        ["データインスペクタの表示切り替え"] = ["view.panel.inspector"],
        ["マクロの記録開始 / 停止"] = ["-F3-06 (フェーズ 3)"],
        ["画面を分割"] = ["view.split"],
        ["カーソル移動 (VIEW-25)"] = ["-Hex ビューが処理する移動キー"],
        ["Hex 列とテキスト列の切り替え (順 / 逆)"] = ["go.toggleColumn"],
        ["前 / 次のグループへ移動"] = ["go.previousGroup", "go.nextGroup"],
        ["カーソルを動かさずに 1 行スクロール"] = ["view.scrollLineUp", "view.scrollLineDown"],
        ["選択しながら移動 (EDIT-02)"] = ["-Hex ビューが処理する"],
        ["すべて選択"] = ["edit.selectAll"],
        ["切り取り / コピー / 貼り付け"] = ["edit.cut", "edit.copy", "edit.paste"],
        ["切り取り / コピー / 貼り付け (Windows 従来のキー)"] = ["edit.cut", "edit.copy", "edit.paste"],
        ["上書き貼り付け"] = ["edit.pasteOverwrite"],
        ["形式を選択して貼り付け"] = ["edit.pasteSpecial"],
        ["形式を選択してコピー"] = ["edit.copyAs"],
        ["範囲を選択 (開始・終了・長さを入力)"] = ["edit.selectRange"],
        ["上書き / 挿入の切り替え"] = ["edit.toggleInsert"],
        ["削除 / 前のバイトを削除 (挿入モード)"] = ["-Hex ビューが処理する"],
        ["矩形選択"] = ["-Hex ビューが処理する (Alt+ドラッグ・Alt+Shift+矢印)"],
        ["範囲をマルチ選択に追加"] = ["-Hex ビューが処理する (マウス)"],
        ["カーソルを追加"] = ["-Hex ビューが処理する (マウス)"],
        ["カーソルを上 / 下の行に追加"] = ["edit.caret.addAbove", "edit.caret.addBelow"],
        ["選択の解除、マルチカーソルを 1 つに戻す"] = ["-Hex ビューが処理する"],
        ["次を検索 / 前を検索 (検索バー)"] = ["-検索バーが処理する"],
        ["すべて検索"] = ["search.findAll"],
        ["検索履歴"] = ["-検索バーが処理する"],
        ["実行中の検索を取り消す。実行中でなければ検索バーを閉じる"] = ["-検索バーが処理する"],
        ["実行 / 続行"] = ["-F3-05 (フェーズ 3)"],
        ["停止"] = ["-F3-05 (フェーズ 3)"],
        ["ブレークポイントの設定 / 解除"] = ["-F3-05 (フェーズ 3)"],
        ["ステップオーバー"] = ["-F3-05 (フェーズ 3)"],
        ["ステップイン (グローバルの全画面表示より優先)"] = ["-F3-05 (フェーズ 3)"],
        ["ステップアウト"] = ["-F3-05 (フェーズ 3)"],
        ["定義へ移動"] = ["-F3-01 (フェーズ 3)"],
        ["選択した項目へ移動、または編集"] = ["-一覧のパネルが処理する"],
        ["選択した項目を削除"] = ["-一覧のパネルが処理する"],
        ["選択した項目の切り替え (ビットの反転、チェックの切り替えなど)"] = ["-一覧のパネルが処理する"],
        ["パネル内の一時的な状態 (編集中・配列モードなど) を抜ける。何もなければエディタにフォーカスを戻す"] = ["-一覧のパネルが処理する"],
        ["右クリックメニュー"] = ["-OS 標準のキー"],
    };

    [GeneratedRegex(@"^### 8\.(\d) ")]
    private static partial Regex Section();

    [Fact]
    [Trait(TC, "TC-UI-39-03")]
    public void Default_shortcuts_match_the_overview_table()
    {
        string[] lines = File.ReadAllLines(SourceTests.FindRepoFile("docs/spec/00-overview.md"));
        var expected = new HashSet<string>();
        var errors = new List<string>();
        KeyScope? scope = null;
        foreach (string line in lines)
        {
            if (Section().Match(line) is { Success: true } m)
            {
                scope = m.Groups[1].Value switch
                {
                    "2" => KeyScope.Global,
                    "3" => KeyScope.Editor,
                    "4" => KeyScope.FindBar,
                    "5" => KeyScope.CodeEditor,
                    "6" => KeyScope.Panel,
                    _ => null,
                };
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                scope = null;
            }

            if (scope is null || !line.StartsWith('|') || line.Contains("---") || line.Contains("| キー |"))
            {
                continue;
            }

            string[] cells = [.. line.Trim('|').Split('|').Select(c => c.Trim())];
            for (int i = 0; i + 1 < cells.Length; i += 3)
            {
                string keyCell = cells[i].Replace("\\\\", "\\"), command = cells[i + 1];
                if (keyCell.Length == 0)
                {
                    continue;
                }

                // 検索バーの「次を検索 / 前を検索」はグローバルの F3 と同じ名前なので区別する。
                string name = scope == KeyScope.FindBar && command == "次を検索 / 前を検索" ? command + " (検索バー)" : command;
                if (!TableCommands.TryGetValue(name, out string[]? ids))
                {
                    errors.Add($"表のコマンド「{name}」をテストの対応表 (TableCommands) に足してください。");
                    continue;
                }

                if (ids[0].StartsWith('-'))
                {
                    continue;
                }

                string[] keys = [.. keyCell.Split(" / ").SelectMany(Expand)];
                for (int k = 0; k < keys.Length; k++)
                {
                    string id = ids.Length == 1 ? ids[0] : ids[k % ids.Length];
                    if (!KeyChord.TryParse(keys[k], out KeyChord chord))
                    {
                        errors.Add($"表のキー「{keys[k]}」を読めません。");
                        continue;
                    }

                    expected.Add($"{id} {chord} {scope}");
                }
            }
        }

        var actual = BuiltInCommands.All.SelectMany(c => c.DefaultBindings.Select(b => $"{c.Id} {b.Chord} {b.Scope}")).ToHashSet();
        errors.AddRange(expected.Except(actual).Select(e => $"表にあるがコマンド登録の既定にない: {e}"));
        errors.AddRange(actual.Except(expected).Select(e => $"コマンド登録の既定にあるが表にない: {e}"));
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>「Ctrl+Shift+1〜9」を 9 個のキーに展開する。</summary>
    private static IEnumerable<string> Expand(string key)
    {
        Match m = Regex.Match(key, @"^(.*\+)(\d)〜(\d)$");
        if (!m.Success)
        {
            return [key];
        }

        return Enumerable.Range(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value) - int.Parse(m.Groups[2].Value) + 1)
            .Select(n => m.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture));
    }
}
