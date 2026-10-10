using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HexEditor.App;

/// <summary>
/// フェーズ 2 の表示メニュー: セルの表示形式 (VIEW-10)、エンディアンと逆順表示 (VIEW-11)、バイトテーマ (VIEW-17)、レコード表示 (VIEW-18)、
/// 文字表 (VIEW-23)、テキスト列の追加・削除 (VIEW-24)、区切り線とページ表示 (VIEW-33) と、ミニマップ (VIEW-35)・画面分割 (VIEW-37)・
/// 新しいビュー (VIEW-38)・並べて表示 (VIEW-39) の項目。処理は各ファイル (MainWindow.Minimap.cs、MainWindow.Split.cs など)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>バイトテーマの設定キー (VIEW-17)。</summary>
    public const string ByteThemeKey = "view.byteTheme";

    private MenuFlyoutSubItem? _byteThemeMenu;

    private void InitializeViewPhase2Menu(MenuBarItem view, MenuFlyoutSubItem columns, MenuFlyoutSubItem encoding, MenuBarItem go)
    {
        // 文字表は設定フォルダに保存する (VIEW-23 の仕様 6)。数値の検索のエンディアンの既定はドキュメントのエンディアン (FIND-13 の仕様 4)。
        TableEncodings.Folder ??= Path.Combine(App.Settings.Folder, TableEncodings.FolderName);
        FindBar.DocumentBigEndian = e => e.View.BigEndian;

        // ---- 列の構成: セルの表示形式・エンディアン・テキスト列 ----
        columns.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutSubItem formats = Sub("Command_ViewCellFormat", "Menu_View_CellFormat");
        foreach (CellFormat format in Enum.GetValues<CellFormat>())
        {
            CellFormat f = format;
            formats.Items.Add(Radio("Command_ViewCellFormat" + f, Loc.Get("Menu_View_CellFormat" + f + "/Text"), "CellFormat", () => SetCellFormat(f),
                Loc.Get("Menu_View_CellFormat" + f + "/AccessKey"), v => v.CellFormat == f));
        }

        formats.Items.Add(new MenuFlyoutSeparator());
        formats.Items.Add(Toggle("Command_ViewSpacePadding", "Menu_View_SpacePadding", v => v.SpacePadding, (v, on) => v with { SpacePadding = on }));
        columns.Items.Add(formats);

        MenuFlyoutSubItem endian = Sub("Command_ViewEndian", "Menu_View_Endian");
        endian.Items.Add(Radio("Command_ViewEndianLittle", Loc.Get(MenuKey("Menu_View_EndianLittle", "Text")), "Endian", () => SetDocumentEndian(false),
            Loc.Get(MenuKey("Menu_View_EndianLittle", "AccessKey")), v => !v.BigEndian));
        endian.Items.Add(Radio("Command_ViewEndianBig", Loc.Get(MenuKey("Menu_View_EndianBig", "Text")), "Endian", () => SetDocumentEndian(true),
            Loc.Get(MenuKey("Menu_View_EndianBig", "AccessKey")), v => v.BigEndian));
        columns.Items.Add(endian);
        Commands.Register("view.endianToggle", () => SetDocumentEndian(Editor is { } e && !e.View.BigEndian), NeedsDocument);

        // グループ内のバイトを逆順に表示 (VIEW-11 の仕様 4・10。グループ化が 1 のとき・Hex 以外の形式では無効)。
        columns.Items.Add(Toggle("Command_ViewReverseGroups", "Menu_View_ReverseGroups", v => v.ReverseGroups, (v, on) => v with { ReverseGroups = on },
            v => v.GroupSize > 1 && v.CellFormat == CellFormat.Hex, Loc.Get("Command_ReverseNeedsGroup")));

        columns.Items.Add(new MenuFlyoutSeparator());
        columns.Items.Add(Item("Command_ViewAddTextColumn", "Menu_View_AddTextColumn", AddTextColumn,
            () => Editor is not { } e ? NeedsDocument()
                : !e.View.ShowTextColumn ? CommandState.Unavailable(Loc.Get("Command_NoTextColumn"))
                : e.View.TextColumnCount >= ViewSettings.MaxTextColumns ? CommandState.Unavailable(Loc.Get("Command_TextColumnLimit"))
                : CommandState.Available));
        columns.Items.Add(Item("Command_ViewRemoveTextColumn", "Menu_View_RemoveTextColumn", RemoveTextColumn,
            () => Editor is not { } e ? NeedsDocument()
                : e.View.TextColumnCount <= 1 ? CommandState.Unavailable(Loc.Get("Command_LastTextColumn")) : CommandState.Available));

        // ---- 文字コード: 文字表ファイル (VIEW-23) ----
        encoding.Items.Add(new MenuFlyoutSeparator());
        var loadTable = new MenuFlyoutItem
        {
            Text = Loc.Get(MenuKey("Menu_View_LoadTable", "Text")),
            AccessKey = Loc.Get(MenuKey("Menu_View_LoadTable", "AccessKey")),
        };
        Commands.Register("view.encoding.loadTable", LoadTableAsync, NeedsDocument);
        CommandUi.SetId(loadTable, "view.encoding.loadTable");
        AutomationProperties.SetAutomationId(loadTable, "Command_ViewLoadTable");
        _viewItems["Command_ViewLoadTable"] = loadTable;
        encoding.Items.Add(loadTable);

        // ---- 表示メニューの直下 ----
        int at = view.Items.IndexOf(_viewItems["Command_ViewSettings"]) + 1;

        // バイトテーマ (VIEW-17)。ハイコントラストでは無効と示す (仕様 4)。
        _byteThemeMenu = Sub("Command_ViewByteTheme", "Menu_View_ByteTheme");
        foreach ((string key, string id) in new[] { ("none", "None"), ("category", "Category"), ("gradient", "Gradient") })
        {
            string value = key;
            _byteThemeMenu.Items.Add(GlobalRadio("Command_ViewByteTheme" + id, "Menu_View_ByteTheme" + id, "ByteTheme",
                () => SetByteTheme(value), () => App.Settings.GetString(ByteThemeKey, "none") == value));
        }

        _byteThemeMenu.Items.Add(Bind(new RadioMenuFlyoutItem
        {
            Text = Loc.Get("Menu_View_ByteThemeCustom/Text"),
            AccessKey = Loc.Get("Menu_View_ByteThemeCustom/AccessKey"),
            GroupName = "ViewByteTheme",
        }, "Command_ViewByteThemeCustom", () => _ = LoadByteThemeAsync(),
            () => Toggle(App.Settings.GetString(ByteThemeKey, "none").StartsWith(ByteThemeStore.CustomPrefix, StringComparison.Ordinal))));

        // レコード表示 (VIEW-18)。
        MenuFlyoutSubItem records = Sub("Command_ViewRecordsMenu", "Menu_View_RecordsMenu");
        records.Items.Add(Toggle("Command_ViewRecords", "Menu_View_Records", v => v.RecordView, (v, on) => v with { RecordView = on }));
        records.Items.Add(Item("Command_ViewRecordSettings", "Menu_View_RecordSettings", ShowRecordSettings));
        records.Items.Add(Toggle("Command_ViewRecordPerRow", "Menu_View_RecordPerRow", v => v.RecordPerRow, (v, on) => v with { RecordPerRow = on, RecordView = on || v.RecordView },
            v => v.RecordLength <= ViewSettings.MaxBytesPerRow, Loc.Format("Command_RecordTooLong", ViewSettings.MaxBytesPerRow.ToString("N0", CultureInfo.CurrentCulture))));
        records.Items.Add(Toggle("Command_ViewRecordNumbers", "Menu_View_RecordNumbers", v => v.RecordNumbers, (v, on) => v with { RecordNumbers = on }));

        // 区切り線とページ表示 (VIEW-33)。
        MenuFlyoutSubItem separators = Sub("Command_ViewSeparatorMenu", "Menu_View_SeparatorMenu");
        foreach (SeparatorKind kind in new[] { SeparatorKind.None, SeparatorKind.Sector, SeparatorKind.Page })
        {
            SeparatorKind k = kind;
            string id = "Command_ViewSeparator" + k;
            separators.Items.Add(Bind(new RadioMenuFlyoutItem
            {
                Text = Loc.Get("Menu_View_Separator" + k + "/Text"),
                AccessKey = Loc.Get("Menu_View_Separator" + k + "/AccessKey"),
                GroupName = "ViewSeparator",
            }, id, () => ChangeView(v => v with { Separator = k }), () => SeparatorState(k)));
        }

        separators.Items.Add(Item("Command_ViewSeparatorCustom", "Menu_View_SeparatorCustom", ShowSeparatorInput));
        separators.Items.Add(new MenuFlyoutSeparator());
        separators.Items.Add(Toggle("Command_ViewSeparatorLabels", "Menu_View_SeparatorLabels", v => v.SeparatorLabels, (v, on) => v with { SeparatorLabels = on }));
        separators.Items.Add(Toggle("Command_ViewPageView", "Menu_View_PageView", v => v.PageView, (v, on) => v with { PageView = on }));

        // ミニマップ (VIEW-35)。
        MenuFlyoutSubItem minimap = Sub("Command_ViewMinimapMenu", "Menu_View_MinimapMenu");
        minimap.Items.Add(Bind(new ToggleMenuFlyoutItem { Text = Loc.Get("Menu_View_Minimap/Text"), AccessKey = Loc.Get("Menu_View_Minimap/AccessKey") },
            "Command_ViewMinimap", ToggleMinimap, () => Toggle(MinimapVisible)));
        minimap.Items.Add(new MenuFlyoutSeparator());
        foreach (MinimapContent content in Enum.GetValues<MinimapContent>())
        {
            MinimapContent c = content;
            minimap.Items.Add(GlobalRadio("Command_ViewMinimap" + c, "Menu_View_Minimap" + c, "MinimapContent", () => SetMinimapContent(c),
                () => MinimapContentSetting == c, () => c == MinimapContent.ByteTheme && MinimapRangeSetting != MinimapRange.Around
                    ? Loc.Get("Command_MinimapThemeNeedsAround") : null));
        }

        minimap.Items.Add(new MenuFlyoutSeparator());
        foreach (MinimapRange range in Enum.GetValues<MinimapRange>())
        {
            MinimapRange r = range;
            minimap.Items.Add(GlobalRadio("Command_ViewMinimap" + r, "Menu_View_Minimap" + r, "MinimapRange", () => SetMinimapRange(r),
                () => MinimapRangeSetting == r));
        }

        minimap.Items.Add(new MenuFlyoutSeparator());
        minimap.Items.Add(Bind(new ToggleMenuFlyoutItem { Text = Loc.Get("Menu_View_MinimapExact/Text"), AccessKey = Loc.Get("Menu_View_MinimapExact/AccessKey") },
            "Command_ViewMinimapExact", ToggleMinimapExact, () => Editor is null ? NeedsDocument() : Toggle(App.Settings.GetBool(MinimapExactKey, false))));

        // 画面分割 (VIEW-37)・新しいビュー (VIEW-38)・並べて表示 (VIEW-39)。
        MenuFlyoutSubItem split = Sub("Command_ViewSplitMenu", "Menu_View_SplitMenu");
        split.Items.Add(Item("Command_ViewSplitHorizontal", "Menu_View_SplitHorizontal", () => SplitSelected(Orientation.Vertical), () => SplitState(Orientation.Vertical)));
        split.Items.Add(Item("Command_ViewSplitVertical", "Menu_View_SplitVertical", () => SplitSelected(Orientation.Horizontal), () => SplitState(Orientation.Horizontal)));
        split.Items.Add(Item("Command_ViewSplitRemove", "Menu_View_SplitRemove", UnsplitSelected,
            () => Vm.Selected is { IsSplit: true } ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NotSplit"))));
        split.Items.Add(new MenuFlyoutSeparator());
        split.Items.Add(Bind(new ToggleMenuFlyoutItem { Text = Loc.Get(MenuKey("Menu_View_SplitSync", "Text")), AccessKey = Loc.Get(MenuKey("Menu_View_SplitSync", "AccessKey")) },
            "Command_ViewSplitSync", ToggleSplitSync,
            () => Vm.Selected is { IsSplit: true } d ? Toggle(d.PaneSyncEnabled) : new CommandState(false, Loc.Get("Command_NotSplit"), false)));
        split.Items.Add(Item("Command_ViewNextPane", "Menu_View_NextPane", () => FocusNextPane(),
            () => Vm.Selected is { IsSplit: true } ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NotSplit"))));
        Commands.Register("view.split", ToggleSplit, SplitToggleState);

        MenuFlyoutItem newView = Item("Command_ViewNewView", "Menu_View_NewView", OpenNewView, NewViewState);

        var sideBySide = new MenuFlyoutItem { Text = Loc.Get(MenuKey("Menu_View_SideBySide", "Text")), AccessKey = Loc.Get(MenuKey("Menu_View_SideBySide", "AccessKey")) };
        Commands.Register("view.sideBySide", new CommandHandler(argument => ShowSideBySideAsync(argument), SideBySideState));
        CommandUi.SetId(sideBySide, "view.sideBySide");
        AutomationProperties.SetAutomationId(sideBySide, "Command_ViewSideBySide");
        _viewItems["Command_ViewSideBySide"] = sideBySide;

        MenuFlyoutSubItem sync = Sub("Command_ViewSyncMenu", "Menu_View_SyncMenu");
        foreach ((SyncMode mode, string id) in new[]
        {
            (SyncMode.Off, "Off"), (SyncMode.SameOffset, "SameOffset"), (SyncMode.KeepDifference, "KeepDifference"), (SyncMode.Mapped, "Mapped"),
        })
        {
            SyncMode m = mode;
            sync.Items.Add(Bind(new RadioMenuFlyoutItem
            {
                Text = Loc.Get("Menu_View_Sync" + id + "/Text"),
                AccessKey = Loc.Get("Menu_View_Sync" + id + "/AccessKey"),
                GroupName = "ViewSync",
            }, "Command_ViewSync" + id, () => SetSyncMode(m), () => SideBySideOf(Vm.Selected) is not { } group
                ? new CommandState(false, Loc.Get("Command_NotSideBySide"), false)

                // 「比較に従う」は、並べた 2 つの比較の結果がある場合だけ選べる (VIEW-39 の仕様 2)。
                : m == SyncMode.Mapped && CompareMapperFor(group) is null ? new CommandState(false, Loc.Get("Command_NoCompareResult"), false)
                : Toggle(group.Mode == m)));
        }

        sync.Items.Add(new MenuFlyoutSeparator());
        sync.Items.Add(Bind(new ToggleMenuFlyoutItem { Text = Loc.Get("Menu_View_SyncDifferences/Text"), AccessKey = Loc.Get("Menu_View_SyncDifferences/AccessKey") },
            "Command_ViewSyncDifferences", ToggleSyncDifferences,
            () => SideBySideOf(Vm.Selected) is { } group ? Toggle(group.HighlightDifferences) : new CommandState(false, Loc.Get("Command_NotSideBySide"), false)));
        Commands.Register("view.syncToggle", ToggleSync,
            () => SideBySideOf(Vm.Selected) is { } group ? Toggle(group.Mode != SyncMode.Off) : CommandState.Unavailable(Loc.Get("Command_NotSideBySide")));

        foreach (MenuFlyoutItemBase item in (MenuFlyoutItemBase[])
            [new MenuFlyoutSeparator(), _byteThemeMenu, records, separators, minimap, new MenuFlyoutSeparator(), split, newView, sideBySide, sync])
        {
            view.Items.Insert(at++, item);
        }

        // ---- 移動メニュー: レコード (VIEW-18 の仕様 6)・ページ (VIEW-33 の仕様 5) ----
        int goAt = go.Items.IndexOf(go.Items.OfType<MenuFlyoutItem>().First(i => AutomationProperties.GetAutomationId(i) == "Command_GoEnd")) + 1;
        go.Items.Insert(goAt++, new MenuFlyoutSeparator());
        go.Items.Insert(goAt++, GoItem("Command_GoNextRecord", "Menu_Go_NextRecord", "go.nextRecord", () => Editor?.NextRecord()));
        go.Items.Insert(goAt++, GoItem("Command_GoPreviousRecord", "Menu_Go_PreviousRecord", "go.previousRecord", () => Editor?.PreviousRecord()));
        go.Items.Insert(goAt++, GoItem("Command_GoNextPage", "Menu_Go_NextPage", "go.nextPage", () => Editor?.MoveSection(forward: true),
            () => Editor is { SectionLength: > 0 } ? CommandState.Available : Editor is null ? NeedsDocument() : CommandState.Unavailable(Loc.Get("Command_NoSeparator"))));
        go.Items.Insert(goAt, GoItem("Command_GoPreviousPage", "Menu_Go_PreviousPage", "go.previousPage", () => Editor?.MoveSection(forward: false),
            () => Editor is { SectionLength: > 0 } ? CommandState.Available : Editor is null ? NeedsDocument() : CommandState.Unavailable(Loc.Get("Command_NoSeparator"))));
        UpdateByteThemeMenu();
    }

    /// <summary>コードで作るメニューの項目のリソース (<c>名前/Text</c>、<c>名前/AccessKey</c>)。</summary>
    private static string MenuKey(string name, string property) => name + "/" + property;

    /// <summary>移動メニューの項目 (コマンド ID を直接指定する)。</summary>
    private MenuFlyoutItem GoItem(string automationId, string key, string command, Action action, Func<CommandState>? state = null)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key + "/Text"), AccessKey = Loc.Get(key + "/AccessKey") };
        Commands.Register(command, action, state ?? NeedsDocument);
        CommandUi.SetId(item, command);
        AutomationProperties.SetAutomationId(item, automationId);
        _viewItems[automationId] = item;
        return item;
    }

    /// <summary>全体の設定を選ぶ項目 (文書がなくても選べる)。<paramref name="reason"/> が理由を返せば無効にする。</summary>
    private RadioMenuFlyoutItem GlobalRadio(string id, string key, string group, Action action, Func<bool> isChecked, Func<string?>? reason = null) =>
        Bind(new RadioMenuFlyoutItem { Text = Loc.Get(key + "/Text"), AccessKey = Loc.Get(key + "/AccessKey"), GroupName = "View" + group }, id, action,
            () => reason?.Invoke() is { } r ? new CommandState(false, r, isChecked()) : Toggle(isChecked()));

    // ---- VIEW-10 ----

    /// <summary>セルの表示形式を変える。1 行のバイト数が単位の倍数でなくなる場合は切り上げ、InfoBar で知らせる。</summary>
    private void SetCellFormat(CellFormat format)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        ViewSettings next = editor.View.WithCellFormat(format, out int? rounded);
        editor.ApplyView(next);
        if (rounded is { } n && !next.AutoBytesPerRow)
        {
            ShowNotice(Loc.Format("Notice_BytesPerRowRounded", n), InfoBarSeverity.Informational, Vm.Selected);
        }

        UpdateViewMenu();
        QueueStatusBarLayout();
    }

    // ---- VIEW-11 ----

    /// <summary>
    /// ドキュメントのエンディアンを変える (VIEW-11 の仕様 1・3)。エンディアンはドキュメントごとなので、同じドキュメントのすべてのビュー
    /// (分割したペイン・新しいビュー) に反映する。インスペクタで個別に切り替えている指定は変えない。
    /// </summary>
    private void SetDocumentEndian(bool big)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        foreach (EditorState e in EditorsOf(editor.Document))
        {
            e.ApplyView(e.View with { BigEndian = big });
        }

        UpdateViewMenu();
        QueueStatusBarLayout();
    }

    /// <summary>このウィンドウで、ドキュメントを表示しているすべてのビュー (タブ・分割したペイン・並べて表示)。</summary>
    private IEnumerable<EditorState> EditorsOf(Core.Engine.Document document)
    {
        var seen = new HashSet<EditorState>();
        foreach (DocumentViewModel doc in Vm.Documents.Where(d => ReferenceEquals(d.Document, document)))
        {
            foreach (EditorState e in doc.Panes)
            {
                if (seen.Add(e))
                {
                    yield return e;
                }
            }
        }
    }

    // ---- VIEW-24 ----

    private TextColumnSpec ActiveTextSpec(ViewSettings view)
    {
        IReadOnlyList<TextColumnSpec> columns = view.TextColumns;
        return columns[Math.Clamp(ActiveTextColumnIndex, 0, columns.Count - 1)];
    }

    /// <summary>操作中のテキスト列 (表示メニューの文字コードの開始位置の項目が対象にする列)。</summary>
    private int ActiveTextColumnIndex => Editor?.TextColumn ?? 0;

    private ViewSettings WithActiveTextSpec(ViewSettings view, Func<TextColumnSpec, TextColumnSpec> change)
    {
        List<TextColumnSpec> columns = [.. view.TextColumns];
        int index = Math.Clamp(ActiveTextColumnIndex, 0, columns.Count - 1);
        columns[index] = change(columns[index]);
        return view.WithTextColumns(columns);
    }

    /// <summary>
    /// 「テキスト列を追加…」(VIEW-24): 操作中のテキスト列と同じ文字コードの列を右に加えて操作中の列にし、文字コードの一覧を開く
    /// (一覧で選んだ文字コードがその列の文字コードになる)。5 列のときは追加しない。
    /// </summary>
    private void AddTextColumn()
    {
        if (Editor is not { } editor || editor.View.TextColumnCount >= ViewSettings.MaxTextColumns || !editor.View.ShowTextColumn)
        {
            return;
        }

        List<TextColumnSpec> columns = [.. editor.View.TextColumns];
        columns.Add(new TextColumnSpec(editor.TextEncoding.Id));
        editor.ApplyView(editor.View.WithTextColumns(columns));
        editor.Click(editor.Cursor, ActiveColumn.Text, false, false, columns.Count - 1);
        UpdateViewMenu();
        ShowEncodingList(null);
    }

    /// <summary>「テキスト列を削除」(VIEW-24): 操作中 (または最後) のテキスト列を削除する。1 列は残す。</summary>
    private void RemoveTextColumn()
    {
        if (Editor is not { } editor || editor.View.TextColumnCount <= 1)
        {
            return;
        }

        List<TextColumnSpec> columns = [.. editor.View.TextColumns];
        int index = editor.ActiveColumn == ActiveColumn.Text ? editor.TextColumn : columns.Count - 1;
        columns.RemoveAt(Math.Clamp(index, 0, columns.Count - 1));
        editor.ApplyView(editor.View.WithTextColumns(columns));
        UpdateViewMenu();
        QueueStatusBarLayout();
    }

    /// <summary>テキスト列の見出しのメニューの「文字コードを変更…」(VIEW-24 の仕様 3)。</summary>
    private void HexView_TextColumnEncodingRequested(object? sender, int column)
    {
        if (sender is HexView { Editor: { } editor })
        {
            editor.Click(editor.Cursor, ActiveColumn.Text, false, false, column);
            ShowEncodingList(null);
        }
    }

    // ---- VIEW-17 ----

    private void SetByteTheme(string key)
    {
        App.Settings.SetString(ByteThemeKey, key, "none");
        ApplyByteTheme();
    }

    /// <summary>バイトテーマを全部の Hex ビューに反映する。</summary>
    private void ApplyByteTheme()
    {
        ByteTheme? theme = new ByteThemeStore(App.Settings.Folder).Resolve(App.Settings.GetString(ByteThemeKey, "none"));
        foreach (HexView view in _views)
        {
            view.ByteTheme = theme;
        }

        UpdateByteThemeMenu();
        RefreshCommandUi();
    }

    /// <summary>ハイコントラストではバイトテーマを使わないことを、メニューの項目に示す (VIEW-17 の仕様 4)。</summary>
    private void UpdateByteThemeMenu()
    {
        if (_byteThemeMenu is null)
        {
            return;
        }

        bool hc = _views.FirstOrDefault()?.IsHighContrast ?? new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        string text = Loc.Get("Menu_View_ByteTheme/Text");
        _byteThemeMenu.Text = hc ? Loc.Format("Menu_View_ByteThemeDisabled", text) : text;
    }

    /// <summary>「独自…」(VIEW-17 の仕様 3): JSON を選んで読み込む。不正な JSON は行番号とキーを InfoBar で示し、テーマを変えない。</summary>
    private async Task LoadByteThemeAsync()
    {
        string? path = await PickFileAsync("HexEditor.ByteTheme", [".json", "*"]);
        if (path is null)
        {
            UpdateViewMenu();
            return;
        }

        try
        {
            (ByteTheme? theme, string? key, ByteThemeError? error) = new ByteThemeStore(App.Settings.Folder).Import(path);
            if (theme is null || key is null)
            {
                ShowNotice(Loc.Format("Notice_ByteThemeInvalid", error?.Line ?? 0, error?.Key ?? string.Empty), InfoBarSeverity.Error);
            }
            else
            {
                SetByteTheme(key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Notice_ByteThemeUnreadable", ex.Message), InfoBarSeverity.Error);
        }

        UpdateViewMenu();
    }

    /// <summary>ファイルを 1 つ選ぶ (テストではファイルのダイアログの代わりに指定のパス。テスト方針 7.2)。</summary>
    private async Task<string?> PickFileAsync(string settingsIdentifier, IReadOnlyList<string> extensions)
    {
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        foreach (string ext in extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

    // ---- VIEW-23 ----

    /// <summary>
    /// 「文字表ファイルを読み込む…」(VIEW-23): 読み込んで設定フォルダに保存し、テキスト列の文字コードにする。読み込み結果 (エントリ数、
    /// 無視した不正な行の数と行番号) を InfoBar で示す。
    /// </summary>
    private async Task LoadTableAsync()
    {
        string? path = await PickFileAsync("HexEditor.Table", [".tbl", "*"]);
        if (path is null || Vm.Selected is not { } doc)
        {
            return;
        }

        TableLoadResult result;
        try
        {
            result = TableEncodings.Import(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Notice_TableUnreadable", ex.Message), InfoBarSeverity.Error, doc);
            return;
        }

        string lines = string.Join(", ", result.InvalidLines.Select(l => Loc.Format("Notice_TableLine", l.Line, Loc.Get("TableError_" + l.Error))));
        if (result.Table is null)
        {
            string reason = result.Failure == TableLoadFailure.TooManyEntries
                ? Loc.Format("Notice_TableTooMany", TableFile.MaxEntries.ToString("N0", CultureInfo.CurrentCulture))
                : Loc.Get("Notice_TableEmpty");
            ShowNotice(reason, InfoBarSeverity.Error, doc);
            return;
        }

        string message = Loc.Format("Notice_TableLoaded", result.Table.Count, result.InvalidLines.Count);
        ShowNotice(result.InvalidLines.Count > 0 ? message + " " + Loc.Format("Notice_TableInvalidLines", lines) : message,
            result.InvalidLines.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, doc);
        SetEncoding(TableEncodings.IdOf(result.Table.Name));
    }

    // ---- VIEW-18 ----

    private Flyout? _recordFlyout;
    private TextBox? _recordLength;
    private TextBox? _recordStart;
    private CheckBox? _recordPerRow;
    private CheckBox? _recordNumbers;
    private TextBlock? _recordError;
    private Button? _recordOk;

    /// <summary>フライアウトに今の設定を入れている間 (入力の変化をその場で反映しない)。</summary>
    private bool _recordFilling;

    /// <summary>
    /// 「レコード表示…」(VIEW-18 の仕様 1): レコード長・開始オフセット (入力式)・1 行を 1 レコード・レコード番号の表示を、モーダルでない
    /// フライアウトで設定する。入力を変えるとその場で反映し (VIEW-18 の「画面」)、レコード表示をオンにする。不正な値は赤枠と説明文で、反映しない。
    /// </summary>
    private void ShowRecordSettings()
    {
        if (Editor is not { } editor || SelectedView() is not { } view)
        {
            return;
        }

        if (_recordFlyout is null)
        {
            _recordLength = new TextBox { Header = Loc.Get("RecordSettings_Length"), MinWidth = 240 };
            AutomationProperties.SetAutomationId(_recordLength, "RecordSettings_Length");
            _recordStart = new TextBox { Header = Loc.Get("RecordSettings_Start"), MinWidth = 240 };
            AutomationProperties.SetAutomationId(_recordStart, "RecordSettings_Start");
            _recordPerRow = new CheckBox { Content = Loc.Get("RecordSettings_PerRow") };
            AutomationProperties.SetAutomationId(_recordPerRow, "RecordSettings_PerRow");
            _recordNumbers = new CheckBox { Content = Loc.Get("RecordSettings_Numbers") };
            AutomationProperties.SetAutomationId(_recordNumbers, "RecordSettings_Numbers");
            _recordError = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            };
            AutomationProperties.SetAutomationId(_recordError, "RecordSettings_Error");
            AutomationProperties.SetLiveSetting(_recordError, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            _recordOk = new Button { Content = Loc.Get("Common_Ok"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            AutomationProperties.SetAutomationId(_recordOk, "RecordSettings_Ok");
            _recordOk.Click += (_, _) => CommitRecordSettings();
            _recordLength.TextChanged += (_, _) => ApplyRecordSettings();
            _recordStart.TextChanged += (_, _) => ApplyRecordSettings();
            _recordPerRow.Checked += (_, _) => ApplyRecordSettings();
            _recordPerRow.Unchecked += (_, _) => ApplyRecordSettings();
            _recordNumbers.Checked += (_, _) => ApplyRecordSettings();
            _recordNumbers.Unchecked += (_, _) => ApplyRecordSettings();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = Loc.Get("RecordSettings_Title"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            foreach (UIElement e in (UIElement[])[_recordLength, _recordStart, _recordPerRow, _recordNumbers, _recordError, _recordOk])
            {
                panel.Children.Add(e);
            }

            _recordFlyout = new Flyout { Content = panel };
            AutomationProperties.SetAutomationId(panel, "RecordSettings");
            _recordFlyout.Closed += (_, _) => FocusEditor();
        }

        ViewSettings v = editor.View;
        _recordFilling = true;
        try
        {
            _recordLength!.Text = v.RecordLength.ToString(CultureInfo.InvariantCulture);
            _recordStart!.Text = "0x" + v.RecordStart.ToString("X", CultureInfo.InvariantCulture);
            _recordPerRow!.IsChecked = v.RecordPerRow;
            _recordNumbers!.IsChecked = v.RecordNumbers;
        }
        finally
        {
            _recordFilling = false;
        }

        ValidateRecordSettings();
        _recordFlyout.ShowAt(view, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft, Position = new Windows.Foundation.Point(view.ContentLeft, 0) });
    }

    /// <summary>入力を確かめる。正しければ (長さ, 開始) を返す。</summary>
    private (long Length, long Start)? ValidateRecordSettings()
    {
        if (Editor is not { } editor || _recordLength is null || _recordStart is null)
        {
            return null;
        }

        string? error = null;
        long length = 0;
        long start = 0;

        // レコード長は 10 進で読む (00-overview 6.1 の※。「100」は 100 バイト)。
        if (!TryEvaluate(_recordLength.Text, editor, Core.Expressions.DefaultRadix.Decimal, out length) || length < 1 || length > int.MaxValue)
        {
            error = Loc.Get("RecordSettings_LengthRange");
        }
        else if (!TryEvaluate(_recordStart.Text, editor, editor.View.Radix == OffsetRadix.Decimal ? Core.Expressions.DefaultRadix.Decimal
            : Core.Expressions.DefaultRadix.Hexadecimal, out start) || start < 0 || start > editor.Document.Length)
        {
            error = Loc.Get("RecordSettings_StartRange");
        }

        // 「1 行を 1 レコードにする」はレコード長が 4,096 以下のときだけ選べる (仕様 1)。
        _recordPerRow!.IsEnabled = error is not null || length <= ViewSettings.MaxBytesPerRow;
        Microsoft.UI.Xaml.Media.Brush? critical = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _recordLength.BorderBrush = error == Loc.Get("RecordSettings_LengthRange") ? critical : null;
        _recordStart.BorderBrush = error == Loc.Get("RecordSettings_StartRange") ? critical : null;
        _recordError!.Text = error ?? string.Empty;
        _recordError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        _recordOk!.IsEnabled = error is null;
        return error is null ? (length, start) : null;
    }

    /// <summary>OK: 入力を反映して閉じる。</summary>
    private void CommitRecordSettings()
    {
        if (ApplyRecordSettings())
        {
            _recordFlyout?.Hide();
        }
    }

    /// <summary>入力が正しければ、その場でレコード表示に反映する (VIEW-18 の「画面」)。反映できたら true。</summary>
    private bool ApplyRecordSettings()
    {
        if (_recordFilling)
        {
            return false;
        }

        if (Editor is not { } editor || ValidateRecordSettings() is not { } values)
        {
            return false;
        }

        bool perRow = _recordPerRow!.IsChecked == true && values.Length <= ViewSettings.MaxBytesPerRow;
        ViewSettings next = editor.View with
        {
            RecordView = true,
            RecordLength = (int)values.Length,
            RecordStart = values.Start,
            RecordPerRow = perRow,
            RecordNumbers = _recordNumbers!.IsChecked == true,
        };
        if (next != editor.View)
        {
            editor.ApplyView(next);
            UpdateViewMenu();
            QueueStatusBarLayout();
        }

        return true;
    }

    // ---- VIEW-33 ----

    /// <summary>区切りの長さの項目の状態 (VIEW-33 の仕様 2: 1 行のバイト数の倍数でない・行の先頭のずれがある場合は選べない)。</summary>
    private CommandState SeparatorState(SeparatorKind kind)
    {
        if (Editor is not { } editor)
        {
            return NeedsDocument();
        }

        bool on = editor.View.Separator == kind;
        if (kind == SeparatorKind.None)
        {
            return Toggle(on);
        }

        long length = SectionLayout.LengthFor(editor.View with { Separator = kind }, editor.SectorSize);
        return SectionLayout.Validate(length, editor.BytesPerRow, editor.Layout.RowShift) is { } error
            ? new CommandState(false, SeparatorErrorText(error, editor), on)
            : Toggle(on);
    }

    private static string SeparatorErrorText(SeparatorError error, EditorState editor) => error switch
    {
        SeparatorError.NotMultipleOfRow => Loc.Format("Separator_NotMultiple", editor.BytesPerRow),
        SeparatorError.RowShift => Loc.Get("Separator_RowShift"),
        _ => Loc.Format("Separator_Range", editor.BytesPerRow, int.MaxValue.ToString("N0", CultureInfo.CurrentCulture)),
    };

    /// <summary>「任意の長さ…」(VIEW-33 の仕様 1・2): 1 行分〜2^31 − 1 で、1 行のバイト数の倍数。</summary>
    private void ShowSeparatorInput()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        ShowInput(Loc.Get("ViewInput_Separator"), editor.View.SeparatorLength.ToString(CultureInfo.InvariantCulture), text =>
        {
            if (!TryEvaluate(text, editor, Core.Expressions.DefaultRadix.Decimal, out long value))
            {
                return (false, Loc.Get("ViewInput_Invalid"));
            }

            return SectionLayout.Validate(value, editor.BytesPerRow, editor.Layout.RowShift) is { } error
                ? (false, SeparatorErrorText(error, editor))
                : (true, null);
        }, text =>
        {
            TryEvaluate(text, editor, Core.Expressions.DefaultRadix.Decimal, out long value);
            editor.ApplyView(editor.View with { Separator = SeparatorKind.Custom, SeparatorLength = value });
            UpdateViewMenu();
            QueueStatusBarLayout();
        });
    }

    /// <summary>Hex ビューを表示しているタブ (DocumentViewModel) を探す (分割したペインを含む)。</summary>
    private DocumentViewModel? DocumentOfView(HexView view) =>
        view.DataContext as DocumentViewModel ?? Vm.Documents.FirstOrDefault(d => d.Panes.Contains(view.Editor!));
}
