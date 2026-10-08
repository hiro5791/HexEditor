using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace HexEditor.App;

/// <summary>
/// 表示メニューの表示設定 (VIEW-05〜VIEW-09、VIEW-12〜VIEW-16、VIEW-19〜VIEW-22、VIEW-42、UI-28) と、移動メニューの履歴の一覧 (VIEW-31)。
/// コマンドの登録 (UI-16) とコマンドパレット (UI-17) ができるまでは、メニューの項目として持つ。項目はコードで作り、表示メニューに加える。
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, MenuFlyoutItemBase> _viewItems = [];
    private MenuFlyoutSubItem? _historyMenu;
    private MenuFlyoutSubItem? _schemeMenu;
    private string _historyKey = string.Empty;
    private EditorState? _viewMenuEditor;

    private static readonly int[] BytesPerRowChoices = [8, 16, 32, 64];

    /// <summary>表示メニューと移動メニューに項目を加える (コンストラクタから 1 回呼ぶ)。</summary>
    private void InitializeViewMenu()
    {
        MenuBarItem view = MainMenu.Items.First(m => m.Items.Contains(StatusBarToggle));
        int at = view.Items.IndexOf(view.Items.OfType<MenuFlyoutSubItem>().First(i => AutomationProperties.GetAutomationId(i) == "Command_Encoding")) + 1;

        // 列の構成 (VIEW-05、VIEW-06、VIEW-12〜VIEW-16)
        MenuFlyoutSubItem columns = Sub("Command_ViewColumns", "Menu_View_Columns");
        columns.Items.Add(Toggle("Command_ViewOffsetColumn", "Menu_View_OffsetColumn", v => v.ShowOffsetColumn, (v, on) => v with { ShowOffsetColumn = on }));
        columns.Items.Add(Toggle("Command_ViewHexColumn", "Menu_View_HexColumn", v => v.ShowHexColumn, (v, on) => v with { ShowHexColumn = on }));
        columns.Items.Add(Toggle("Command_ViewTextColumn", "Menu_View_TextColumn", v => v.ShowTextColumn, (v, on) => v with { ShowTextColumn = on }));
        columns.Items.Add(Item("Command_ViewHexOnly", "Menu_View_HexOnly", () => ChangeView(v => v with { ShowHexColumn = true, ShowTextColumn = false })));
        columns.Items.Add(Item("Command_ViewTextOnly", "Menu_View_TextOnly", () => ChangeView(v => v with { ShowHexColumn = false, ShowTextColumn = true })));
        columns.Items.Add(Item("Command_ViewAllColumns", "Menu_View_AllColumns",
            () => ChangeView(v => v with { ShowOffsetColumn = true, ShowHexColumn = true, ShowTextColumn = true })));
        columns.Items.Add(new MenuFlyoutSeparator());
        columns.Items.Add(Toggle("Command_ViewRuler", "Menu_View_Ruler", v => v.ShowRuler, (v, on) => v with { ShowRuler = on }));
        columns.Items.Add(Toggle("Command_ViewCurrentRow", "Menu_View_CurrentRow", v => v.HighlightCurrentRow, (v, on) => v with { HighlightCurrentRow = on }));
        columns.Items.Add(Toggle("Command_ViewLowercase", "Menu_View_Lowercase", v => v.LowercaseHex, (v, on) => v with { LowercaseHex = on }));
        columns.Items.Add(Toggle("Command_ViewDimZeros", "Menu_View_DimZeros", v => v.DimZeros, (v, on) => v with { DimZeros = on }));
        columns.Items.Add(Toggle("Command_ViewAlternate", "Menu_View_Alternate", v => v.AlternateColumns, (v, on) => v with { AlternateColumns = on }));
        columns.Items.Add(Toggle("Command_ViewModified", "Menu_View_Modified", v => v.HighlightModified, (v, on) => v with { HighlightModified = on }));

        // 1 行のバイト数 (VIEW-08)
        MenuFlyoutSubItem bytesPerRow = Sub("Command_ViewBytesPerRow", "Menu_View_BytesPerRow");
        foreach (int n in BytesPerRowChoices)
        {
            bytesPerRow.Items.Add(Radio("Command_ViewBytesPerRow" + n, Loc.Format("Menu_View_GroupBytes", n), "BytesPerRow",
                () => SetBytesPerRow(n), accessKey: n.ToString(CultureInfo.InvariantCulture)[..1]));
        }

        bytesPerRow.Items.Add(Radio("Command_ViewBytesPerRowAuto", Loc.Get("Menu_View_BytesPerRowAuto/Text"), "BytesPerRow",
            () => ChangeView(v => v with { AutoBytesPerRow = true }), Loc.Get("Menu_View_BytesPerRowAuto/AccessKey")));
        bytesPerRow.Items.Add(Item("Command_ViewBytesPerRowCustom", "Menu_View_BytesPerRowCustom", ShowBytesPerRowInput));

        // グループ化 (VIEW-09)
        MenuFlyoutSubItem group = Sub("Command_ViewGroup", "Menu_View_Group");
        foreach (int g in ViewSettings.GroupSizes)
        {
            group.Items.Add(Radio("Command_ViewGroup" + g, Loc.Format("Menu_View_GroupBytes", g), "Group", () => SetGroupSize(g),
                accessKey: g.ToString(CultureInfo.InvariantCulture)[^1..]));
        }

        // オフセットの基数 (VIEW-19、VIEW-20)
        MenuFlyoutSubItem radix = Sub("Command_ViewRadix", "Menu_View_Radix");
        foreach ((OffsetRadix r, string key) in new[]
        {
            (OffsetRadix.Hex, "Menu_View_RadixHex"),
            (OffsetRadix.Decimal, "Menu_View_RadixDecimal"),
            (OffsetRadix.Octal, "Menu_View_RadixOctal"),
            (OffsetRadix.Sector, "Menu_View_RadixSector"),
        })
        {
            radix.Items.Add(Radio("Command_ViewRadix" + r, Loc.Get(key + "/Text"), "Radix", () => ChangeView(v => v with { Radix = r }),
                Loc.Get(key + "/AccessKey")));
        }

        radix.Items.Add(new MenuFlyoutSeparator());
        radix.Items.Add(Item("Command_ViewBaseAddress", "Menu_View_BaseAddress", ShowBaseAddressInput));
        radix.Items.Add(Item("Command_ViewRowShift", "Menu_View_RowShift", ShowRowShiftInput));
        radix.Items.Add(Item("Command_ViewSetReference", "Menu_View_SetReference", () => Editor?.SetReferencePoint()));
        radix.Items.Add(Item("Command_ViewClearReference", "Menu_View_ClearReference", () => Editor?.ClearReferencePoint()));

        // 配色 (UI-28)
        _schemeMenu = Sub("Command_ViewColorScheme", "Menu_View_ColorScheme");

        // 表示設定 (VIEW-42)
        MenuFlyoutSubItem settings = Sub("Command_ViewSettings", "Menu_View_ViewSettings");
        settings.Items.Add(Item("Command_ViewSaveDefault", "Menu_View_SaveDefault", () =>
        {
            if (Editor is { } editor)
            {
                ViewOptions.SaveAsDefault(App.Settings, editor);
            }
        }));
        settings.Items.Add(Item("Command_ViewResetDefault", "Menu_View_ResetDefault", () =>
        {
            if (Vm.Selected is { } doc)
            {
                ViewOptions.ResetToDefault(App.Settings, doc);
            }
        }));

        foreach (MenuFlyoutItemBase item in (MenuFlyoutItemBase[])[columns, bytesPerRow, group, radix, _schemeMenu, settings])
        {
            view.Items.Insert(at++, item);
        }

        // 現在位置を読み上げ (UI-51 の仕様 6)
        view.Items.Insert(at, Item("Command_AnnouncePosition", "Menu_View_AnnouncePosition", () => SelectedView()?.AnnounceCurrentPosition()));

        // 文字コード (VIEW-21 のうち VIEW-22 の表示規則を使うもの)
        MenuFlyoutSubItem encoding = view.Items.OfType<MenuFlyoutSubItem>().First(i => AutomationProperties.GetAutomationId(i) == "Command_Encoding");
        foreach ((string id, string key) in new[]
        {
            ("utf-8", "Menu_View_EncodingUtf8"),
            ("utf-16le", "Menu_View_EncodingUtf16LE"),
            ("utf-16be", "Menu_View_EncodingUtf16BE"),
            ("utf-32le", "Menu_View_EncodingUtf32LE"),
            ("utf-32be", "Menu_View_EncodingUtf32BE"),
            ("cp932", "Menu_View_EncodingShiftJis"),
        })
        {
            var radio = new RadioMenuFlyoutItem
            {
                Text = Loc.Get(key + "/Text"),
                AccessKey = Loc.Get(key + "/AccessKey"),
                GroupName = "Encoding",
                Tag = id,
            };
            AutomationProperties.SetAutomationId(radio, "Command_Encoding_" + id);
            radio.Click += Encoding_Click;
            encoding.Items.Add(radio);
            _viewItems[AutomationProperties.GetAutomationId(radio)] = radio;
        }

        encoding.Items.Add(new MenuFlyoutSeparator());
        encoding.Items.Add(Toggle("Command_ViewUtf16Odd", "Menu_View_Utf16Odd", v => v.Utf16Phase == 1, (v, on) => v with { Utf16Phase = on ? 1 : 0 }));

        // 移動 > 履歴の一覧 (VIEW-31 の仕様 8)
        MenuBarItem go = MainMenu.Items.First(m => m.Items.OfType<MenuFlyoutItem>().Any(i => AutomationProperties.GetAutomationId(i) == "Command_GoBack"));
        _historyMenu = Sub("Command_GoHistory", "Menu_Go_History");
        int forward = go.Items.IndexOf(go.Items.OfType<MenuFlyoutItem>().First(i => AutomationProperties.GetAutomationId(i) == "Command_GoForward"));
        go.Items.Insert(forward + 1, _historyMenu);
        UpdateSchemeMenu();
    }

    // ---- 項目を作る ----

    private MenuFlyoutSubItem Sub(string id, string key)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc.Get(key + "/Text"), AccessKey = Loc.Get(key + "/AccessKey") };
        AutomationProperties.SetAutomationId(sub, id);
        _viewItems[id] = sub;
        return sub;
    }

    private MenuFlyoutItem Item(string id, string key, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key + "/Text"), AccessKey = Loc.Get(key + "/AccessKey") };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => action();
        _viewItems[id] = item;
        return item;
    }

    private ToggleMenuFlyoutItem Toggle(string id, string key, Func<ViewSettings, bool> get, Func<ViewSettings, bool, ViewSettings> set)
    {
        var item = new ToggleMenuFlyoutItem { Text = Loc.Get(key + "/Text"), AccessKey = Loc.Get(key + "/AccessKey"), Tag = get };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => ChangeView(v => set(v, item.IsChecked));
        _viewItems[id] = item;
        return item;
    }

    private RadioMenuFlyoutItem Radio(string id, string text, string group, Action action, string accessKey)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "View" + group, AccessKey = accessKey };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => action();
        _viewItems[id] = item;
        return item;
    }

    /// <summary>選択中のタブの Hex ビュー。</summary>
    private HexView? SelectedView() => _views.FirstOrDefault(v => v.Editor == Editor);

    // ---- 表示設定を変える ----

    private void ChangeView(Func<ViewSettings, ViewSettings> change)
    {
        if (Editor is { } editor)
        {
            editor.ApplyView(change(editor.View));
            UpdateViewMenu();
        }
    }

    private void SetBytesPerRow(int n) => ChangeView(v =>
    {
        // 固定値がグループ化の倍数でない場合は、グループ化を 1 にはせず、倍数に切り上げる (メニューの値はグループ化 16 以下の倍数)。
        int value = v.ValidateBytesPerRow(n) is null ? n : (n + v.RowUnit - 1) / v.RowUnit * v.RowUnit;
        return v with { BytesPerRow = value, AutoBytesPerRow = false };
    });

    /// <summary>グループ化 (VIEW-09)。1 行のバイト数が倍数でなくなる場合は切り上げ、InfoBar で知らせる (仕様 4)。</summary>
    private void SetGroupSize(int g)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        ViewSettings next = editor.View.WithGroupSize(g, out int? rounded);
        editor.ApplyView(next);
        if (rounded is { } n && !next.AutoBytesPerRow)
        {
            ShowNotice(Loc.Format("Notice_BytesPerRowRounded", n), InfoBarSeverity.Informational, Vm.Selected);
        }

        UpdateViewMenu();
    }

    /// <summary>表示設定・配色を、選択中のタブに合わせてメニューに出す (UI-03 の仕様 3)。</summary>
    private void UpdateViewMenu()
    {
        EditorState? editor = Editor;
        if (!ReferenceEquals(editor, _viewMenuEditor))
        {
            if (_viewMenuEditor is not null)
            {
                _viewMenuEditor.ViewChanged -= Editor_ViewChanged;
                _viewMenuEditor.Changed -= Editor_ChangedForHistory;
            }

            _viewMenuEditor = editor;
            if (editor is not null)
            {
                editor.ViewChanged += Editor_ViewChanged;
                editor.Changed += Editor_ChangedForHistory;
            }
        }

        bool hasDoc = editor is not null;
        ViewSettings v = editor?.View ?? ViewSettings.Default;
        foreach ((string id, MenuFlyoutItemBase item) in _viewItems)
        {
            item.IsEnabled = hasDoc;
            if (item is ToggleMenuFlyoutItem { Tag: Func<ViewSettings, bool> get } toggle)
            {
                toggle.IsChecked = get(v);
            }
        }

        // Hex 列とテキスト列のうち最後の 1 つは非表示にできない (VIEW-16 の仕様 2)。
        _viewItems["Command_ViewHexColumn"].IsEnabled = hasDoc && !(v.ShowHexColumn && !v.ShowTextColumn);
        _viewItems["Command_ViewTextColumn"].IsEnabled = hasDoc && !(v.ShowTextColumn && !v.ShowHexColumn);
        SetChecked("Command_ViewBytesPerRowAuto", v.AutoBytesPerRow);
        foreach (int n in BytesPerRowChoices)
        {
            SetChecked("Command_ViewBytesPerRow" + n, !v.AutoBytesPerRow && v.BytesPerRow == n);
        }

        foreach (int g in ViewSettings.GroupSizes)
        {
            SetChecked("Command_ViewGroup" + g, v.GroupSize == g);
        }

        foreach (OffsetRadix r in Enum.GetValues<OffsetRadix>())
        {
            SetChecked("Command_ViewRadix" + r, v.Radix == r);
        }

        _viewItems["Command_ViewClearReference"].IsEnabled = editor?.ReferencePoint is not null;
        UpdateEncodingMenu();
        UpdateHistoryMenu();
        UpdateSchemeMenu();
    }

    private void SetChecked(string id, bool value)
    {
        if (_viewItems.TryGetValue(id, out MenuFlyoutItemBase? item) && item is RadioMenuFlyoutItem radio)
        {
            radio.IsChecked = value;
        }
    }

    private void Editor_ViewChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateViewMenu);

    private void Editor_ChangedForHistory(object? sender, EventArgs e) => UpdateHistoryMenu();

    // ---- 履歴の一覧 (VIEW-31 の仕様 8) ----

    /// <summary>最新 20 件を「アドレス + その位置から 8 バイトの Hex」の形で出す。履歴が変わったときだけ作り直す。</summary>
    private void UpdateHistoryMenu()
    {
        if (_historyMenu is null)
        {
            return;
        }

        EditorState? editor = Editor;
        IReadOnlyList<JumpPoint> recent = editor?.RecentJumps ?? [];
        string key = string.Join(',', recent.Select(p => p.Offset)) + "|" + (editor?.GetHashCode() ?? 0);
        _historyMenu.IsEnabled = recent.Count > 0;
        if (key == _historyKey)
        {
            return;
        }

        _historyKey = key;
        _historyMenu.Items.Clear();
        for (int i = 0; i < recent.Count; i++)
        {
            int index = i;
            JumpPoint point = recent[i];
            // 表示用の読み込み (キャッシュにあるものだけ。UI スレッドで I/O を待たない)。
            byte[] bytes = new byte[8];
            var states = new Core.Engine.ByteState[8];
            int n = point.Offset < editor!.Document.Length ? editor.Document.Current.ReadForDisplay(point.Offset, bytes, states) : 0;
            string hex = string.Join(' ', Enumerable.Range(0, n).Select(i => states[i] == Core.Engine.ByteState.Valid
                ? bytes[i].ToString(editor.View.LowercaseHex ? "x2" : "X2", CultureInfo.InvariantCulture) : "··"));
            var item = new MenuFlyoutItem { Text = editor.OffsetFormat.Status(point.Offset, CultureInfo.CurrentCulture) + "  " + hex };
            AutomationProperties.SetAutomationId(item, "Command_GoHistory_" + i);
            item.Click += (_, _) => Editor?.GoBackTo(index);
            _historyMenu.Items.Add(item);
        }
    }

    // ---- 配色 (UI-28) ----

    private void UpdateSchemeMenu()
    {
        if (_schemeMenu is null)
        {
            return;
        }

        string current = App.Settings.GetString(ViewOptions.ColorSchemeKey, ColorScheme.DefaultName);
        IReadOnlyList<ColorScheme> schemes = new ColorSchemeStore(App.Settings.Folder).All();
        if (_schemeMenu.Items.Count != schemes.Count)
        {
            _schemeMenu.Items.Clear();
            foreach (ColorScheme scheme in schemes)
            {
                string name = scheme.Name;
                var item = new RadioMenuFlyoutItem
                {
                    Text = scheme.BuiltIn ? Loc.Get("Scheme_" + name) : name,
                    GroupName = "ViewColorScheme",
                    Tag = name,
                };
                AutomationProperties.SetAutomationId(item, "Command_ViewColorScheme_" + name);
                item.Click += (_, _) =>
                {
                    App.Settings.SetString(ViewOptions.ColorSchemeKey, name, ColorScheme.DefaultName);
                    ApplyViewOptions();
                };
                _schemeMenu.Items.Add(item);
            }
        }

        foreach (RadioMenuFlyoutItem item in _schemeMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = string.Equals((string)item.Tag, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Hex 表示の設定 (配色・フォント・ツールチップなど) とドキュメントごとの表示設定を、すべてのタブに反映する。</summary>
    private void ApplyViewOptions()
    {
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            ViewOptions.Attach(App.Settings, doc);
        }

        foreach (HexView view in _views)
        {
            ViewOptions.ApplyTo(view, App.Settings);
        }

        UpdateViewMenu();
    }

    /// <summary>Hex ビューが読み込まれた: 表示の設定を反映し、列見出しのメニューからの変更をメニューに映す。</summary>
    private void ConfigureHexView(HexView view)
    {
        // フォントの一覧はバックグラウンドで作り、できたら描画に使うフォントを決め直す (UI-29 の仕様 2)。
        if (!FontCatalog.IsReady)
        {
            FontCatalog.WarmUp(() => DispatcherQueue.TryEnqueue(ApplyViewOptions));
        }

        ViewOptions.ApplyTo(view, App.Settings);
        view.ViewSettingsChanged -= HexView_ViewSettingsChanged;
        view.ViewSettingsChanged += HexView_ViewSettingsChanged;
    }

    private void HexView_ViewSettingsChanged(object? sender, EventArgs e) => UpdateViewMenu();

    // ---- 入力欄 (1 行のバイト数の指定、ベースアドレス、行の先頭のずれ。モーダルにしないフライアウト) ----

    private Flyout? _inputFlyout;
    private TextBox? _inputBox;
    private TextBlock? _inputError;
    private Button? _inputOk;
    private Func<string, (bool Ok, string? Error)>? _inputValidate;
    private Action<string>? _inputCommit;

    /// <summary>入力欄を開いてから、確定または閉じるまでの間。</summary>
    private bool _inputOpen;

    /// <summary>「1 行のバイト数 > 指定…」(VIEW-08 の仕様 1・5)。</summary>
    private void ShowBytesPerRowInput()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        ShowInput(Loc.Get("ViewInput_BytesPerRow"), editor.BytesPerRow.ToString(CultureInfo.InvariantCulture), text =>
        {
            if (!TryEvaluate(text, editor, DefaultRadix.Decimal, out long value))
            {
                return (false, Loc.Get("ViewInput_Invalid"));
            }

            return editor.View.ValidateBytesPerRow(value) switch
            {
                BytesPerRowError.OutOfRange => (false, Loc.Get("ViewInput_BytesPerRowRange")),
                BytesPerRowError.NotMultipleOfGroup => (false, Loc.Format("ViewInput_BytesPerRowGroup", editor.View.RowUnit)),
                _ => (true, null),
            };
        }, text =>
        {
            TryEvaluate(text, editor, DefaultRadix.Decimal, out long value);
            editor.ApplyView(editor.View with { BytesPerRow = (int)value, AutoBytesPerRow = false });
            UpdateViewMenu();
        });
    }

    /// <summary>「ベースアドレスを設定…」(VIEW-20 の仕様 1)。末尾の次の位置のアドレスが 2^64 − 1 を超える値は確定できない。</summary>
    private void ShowBaseAddressInput()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        string initial = "0x" + editor.View.BaseAddress.ToString("X", CultureInfo.InvariantCulture);
        DefaultRadix radix = editor.View.Radix == OffsetRadix.Decimal ? DefaultRadix.Decimal : DefaultRadix.Hexadecimal;
        ShowInput(Loc.Get("ViewInput_BaseAddress"), initial, text =>
        {
            if (!TryParseAddress(text, editor, radix, out ulong value))
            {
                return (false, Loc.Get("ViewInput_Invalid"));
            }

            return value > ulong.MaxValue - (ulong)editor.Layout.MaxCursor ? (false, Loc.Get("ViewInput_BaseAddressRange")) : (true, null);
        }, text =>
        {
            TryParseAddress(text, editor, radix, out ulong value);
            editor.ApplyView(editor.View with { BaseAddress = value });
            UpdateViewMenu();
        });
    }

    /// <summary>「行の先頭をずらす…」(VIEW-20 の仕様 5)。指定すると「行の先頭をアドレスの区切りにそろえる」をオフにする。</summary>
    private void ShowRowShiftInput()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        int max = editor.BytesPerRow - 1;
        ShowInput(Loc.Get("ViewInput_RowShift"), editor.Layout.RowShift.ToString(CultureInfo.InvariantCulture), text =>
        {
            if (!TryEvaluate(text, editor, DefaultRadix.Decimal, out long value))
            {
                return (false, Loc.Get("ViewInput_Invalid"));
            }

            return value < 0 || value > max ? (false, Loc.Format("ViewInput_RowShiftRange", max)) : (true, null);
        }, text =>
        {
            TryEvaluate(text, editor, DefaultRadix.Decimal, out long value);
            editor.ApplyView(editor.View with { RowShift = (int)value, AlignRowsToAddress = false });
            UpdateViewMenu();
        });
    }

    private static bool TryEvaluate(string text, EditorState editor, DefaultRadix radix, out long value) =>
        ExpressionEvaluator.TryEvaluate(text.Trim(), new EditorExpressionContext(editor), out value, out _, radix);

    /// <summary>アドレスの入力 (2^63 以上の 16 進の値も受け付ける)。</summary>
    private static bool TryParseAddress(string text, EditorState editor, DefaultRadix radix, out ulong value)
    {
        string t = text.Trim().Replace("_", string.Empty);
        string? hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..]
            : t.StartsWith('$') ? t[1..]
            : t.EndsWith('h') || t.EndsWith('H') ? t[..^1]
            : radix == DefaultRadix.Hexadecimal && t.Length > 0 && t.All(Uri.IsHexDigit) ? t
            : null;
        if (hex is { Length: > 0 and <= 16 } && hex.All(Uri.IsHexDigit))
        {
            return ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        if (radix == DefaultRadix.Decimal && t.Length > 0 && t.All(char.IsAsciiDigit))
        {
            return ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        bool ok = TryEvaluate(t, editor, radix, out long signed) && signed >= 0;
        value = ok ? (ulong)signed : 0;
        return ok;
    }

    /// <summary>入力欄のフライアウトを出す。不正な値は赤枠と説明文を出し、確定ボタンを無効にする (VIEW-08 の仕様 5 など)。</summary>
    private void ShowInput(string title, string initial, Func<string, (bool Ok, string? Error)> validate, Action<string> commit)
    {
        if (SelectedView() is not { } view)
        {
            return;
        }

        if (_inputFlyout is null)
        {
            _inputBox = new TextBox { MinWidth = 240 };
            AutomationProperties.SetAutomationId(_inputBox, "ViewInput_Box");
            _inputError = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
            AutomationProperties.SetAutomationId(_inputError, "ViewInput_Error");
            AutomationProperties.SetLiveSetting(_inputError, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            _inputOk = new Button { Content = Loc.Get("Common_Ok"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            AutomationProperties.SetAutomationId(_inputOk, "ViewInput_Ok");
            _inputOk.Click += (_, _) => CommitInput();
            _inputBox.TextChanged += (_, _) => ValidateInput();
            _inputBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    CommitInput();
                    e.Handled = true;
                }
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Name = "ViewInputTitle" });
            panel.Children.Add(_inputBox);
            panel.Children.Add(_inputError);
            panel.Children.Add(_inputOk);
            _inputFlyout = new Flyout { Content = panel };
            _inputFlyout.Closed += (_, _) =>
            {
                _inputOpen = false;
                FocusEditor();
            };
        }

        ((TextBlock)((StackPanel)_inputFlyout.Content).Children[0]).Text = title;
        AutomationProperties.SetName(_inputBox!, title);
        _inputValidate = validate;
        _inputCommit = commit;
        _inputBox!.Text = initial;
        _inputOpen = true;
        ValidateInput();
        _inputFlyout.ShowAt(view, new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft, Position = new Windows.Foundation.Point(view.ContentLeft, 0) });
        _inputBox.SelectAll();
    }

    private (bool Ok, string? Error) ValidateInput()
    {
        if (_inputBox is null || _inputValidate is null)
        {
            return (false, null);
        }

        (bool ok, string? error) = _inputValidate(_inputBox.Text);
        _inputError!.Text = error ?? string.Empty;
        _inputError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        _inputBox.BorderBrush = ok ? null : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _inputOk!.IsEnabled = ok;
        return (ok, error);
    }

    private void CommitInput()
    {
        if (_inputBox is null || !ValidateInput().Ok)
        {
            return;
        }

        string text = _inputBox.Text;
        _inputOpen = false;
        _inputFlyout!.Hide();
        _inputCommit?.Invoke(text);
    }
}
