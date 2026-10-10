using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Hashing;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// ハッシュパネルのフェーズ 2 の機能: カスタム CRC (ANA-20)、一致するアルゴリズムを探す (ANA-21 の仕様 5)、カーソル位置に書き込む
/// (ANA-22 の仕様 3・4)、マルチ選択の範囲 (ANA-18 の仕様 1)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>カスタム CRC を保存する設定のキー (ANA-20 の仕様 5。全ドキュメントで使う)。</summary>
    public const string CustomCrcSettingKey = "hash.customCrc";

    /// <summary>利用者のカスタム CRC (アプリ全体で 1 つ)。</summary>
    private static CustomCrcStore? s_customCrcs;

    private static CustomCrcStore CustomCrcs => s_customCrcs ??= LoadCustomCrcStore();

    /// <summary>設定からカスタム CRC を読み込み、アルゴリズムの一覧に反映する (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void LoadCustomCrcs() => _ = CustomCrcs;

    private static CustomCrcStore LoadCustomCrcStore()
    {
        CustomCrcStore store = CustomCrcStore.Load(App.Settings?.GetString(CustomCrcSettingKey, string.Empty));
        store.ApplyToCatalog();
        store.Changed += (_, _) =>
        {
            App.Settings?.SetString(CustomCrcSettingKey, store.Serialize(), string.Empty);
            store.ApplyToCatalog();
        };
        return store;
    }

    /// <summary>ハッシュパネルの view model に、ダイアログ・ファイルの選択を出す処理をつなぐ。</summary>
    private void ConfigureHashVm(HashPanelViewModel vm)
    {
        vm.WriteAtCursor = row => WriteHashAtCursorAsync(row);
        vm.CanWriteAtCursor = () => NeedsEditable().Enabled;
        vm.AddCustomCrc = () => ShowCustomCrcDialogAsync(null);
        vm.ExportCustomCrc = ExportCustomCrcAsync;
        vm.ImportCustomCrc = ImportCustomCrcAsync;
        vm.RemoveCustomCrc = name => CustomCrcs.Remove(name);
        vm.ConfirmFind = ConfirmFindAsync;
    }

    private void RegisterHashExtraCommands()
    {
        Commands.Register("analysis.hash.customCrc", () => ShowCustomCrcDialogAsync(null));
        Commands.Register("analysis.hash.findAlgorithm", async () =>
        {
            ShowPanel(HashPanelId);
            SyncHashTarget();
            await HashVm.FindMatchingAlgorithmsAsync();
        }, NeedsDocument);
        Commands.Register("analysis.hash.writeAtCursor", () => WriteHashAtCursorAsync(null), () =>
            NeedsEditable() is { Enabled: false } state ? state
            : _hashVm is { Rows.Count: > 0 } vm && vm.Rows.Any(r => !r.IsInvalid) ? CommandState.Available
            : CommandState.Unavailable(Loc.Get("Hash_NoResults")));
    }

    private async Task<bool> ConfirmFindAsync(long seconds)
    {
        ContentDialog dialog = DialogParts.Dialog(Root, "HashFindConfirmDialog", Loc.Get("Hash_FindAlgorithm/Content"),
            new TextBlock { Text = Loc.Format("Hash_FindConfirm", seconds), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            Loc.Get("Hash_FindRun"));
        return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    // ---- カーソル位置に書き込む (ANA-22 の仕様 3・4) ----

    /// <summary>
    /// 値のバイト列をカーソル位置 (または選択範囲の先頭) に上書きで書き込む。64 bit 以下の値はバイト順を選ぶ。書き込み先が計算の対象範囲に
    /// 含まれる (除外範囲の外) ときは警告を出す。Undo 1 回分。挿入モードでも上書きする。
    /// </summary>
    private async Task WriteHashAtCursorAsync(HashRowViewModel? row)
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc) || _hashVm is not { } vm)
        {
            return;
        }

        HashRowViewModel[] rows = [.. vm.Rows.Where(r => !r.IsInvalid)];
        if (rows.Length == 0)
        {
            return;
        }

        row ??= rows[0];
        EditorState editor = doc.Editor;
        long offset = editor.HasSelection ? editor.SelectionStart : editor.Cursor;
        ComboBox? choice = null;
        if (rows.Length > 1)
        {
            choice = DialogParts.Combo("HashWrite_Row", Loc.Get("HashWrite_Row"), rows.Select(r => r.Name), Array.IndexOf(rows, row));
        }

        RadioButton big = DialogParts.Radio("HashWrite_BigEndian", Loc.Get("HashWrite_BigEndian"), "HashWriteOrder", !vm.LittleEndian);
        RadioButton little = DialogParts.Radio("HashWrite_LittleEndian", Loc.Get("HashWrite_LittleEndian"), "HashWriteOrder", vm.LittleEndian);
        TextBlock info = DialogParts.Caption("HashWrite_Info", monospace: true);
        var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, Message = Loc.Get("HashWrite_Warning") };
        AutomationProperties.SetAutomationId(warning, "HashWrite_Warning");
        var body = new StackPanel { Spacing = 8, MinWidth = 380 };
        if (choice is not null)
        {
            body.Children.Add(choice);
        }

        body.Children.Add(new TextBlock { Text = Loc.Format("HashWrite_Position", StatusFormat.Hex(offset)) });
        body.Children.Add(big);
        body.Children.Add(little);
        body.Children.Add(info);
        body.Children.Add(warning);
        ContentDialog dialog = DialogParts.Dialog(Root, "HashWriteDialog", Loc.Get("HashWrite_Title"), body, Loc.Get("HashWrite_Write"));
        byte[] bytes = [];
        void Update()
        {
            HashRowViewModel selected = choice is null ? row : rows[Math.Max(0, choice.SelectedIndex)];
            bool numeric = selected.Row.IsNumeric;
            big.IsEnabled = little.IsEnabled = numeric;
            bytes = HashWriteBack.Bytes(selected.Row, numeric && little.IsChecked == true);
            long shortage = HashWriteBack.Shortage(offset, bytes.Length, doc.Document.Length);
            info.Text = shortage > 0
                ? Loc.Format("HashWrite_Error_End", doc.Document.Length - offset, bytes.Length)
                : DialogParts.Hex(bytes);
            bool overlaps = HashWriteBack.OverlapsTarget(offset, bytes.Length, vm.LastRanges, vm.LastExclusions);
            warning.IsOpen = overlaps;
            warning.Visibility = overlaps ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = shortage == 0;
        }

        big.Checked += (_, _) => Update();
        little.Checked += (_, _) => Update();
        if (choice is not null)
        {
            choice.SelectionChanged += (_, _) => Update();
        }

        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || bytes.Length == 0)
        {
            return;
        }

        ApplyEdit(doc, () => doc.Document.Overwrite(offset, bytes, "ハッシュ値の書き込み"));
        FocusEditor();
    }

    // ---- カスタム CRC (ANA-20) ----

    /// <summary>「カスタム CRC」ダイアログ。<paramref name="initial"/> は複製して編集する定義 (null なら新規)。</summary>
    private async Task ShowCustomCrcDialogAsync(CustomCrcDefinition? initial)
    {
        CustomCrcDefinition start = initial ?? new CustomCrcDefinition(CustomCrcDefinition.UniqueName("CRC", CustomCrcs.Items.Select(d => d.Name)), 16, 0x1021);
        HashAlgorithmInfo[] presets = [.. HashCatalog.BuiltIn.Where(a => a.Crc is not null)];
        ComboBox preset = DialogParts.Combo("CustomCrc_Preset", Loc.Get("CustomCrc_Preset"), presets.Select(p => p.Name), -1);
        var duplicate = new Button { Content = Loc.Get("CustomCrc_Duplicate") };
        AutomationProperties.SetAutomationId(duplicate, "CustomCrc_Duplicate");
        TextBox name = DialogParts.Field("CustomCrc_Name", Loc.Get("CustomCrc_Name"), start.Name, monospace: false);
        TextBox width = DialogParts.Field("CustomCrc_Width", Loc.Get("CustomCrc_Width"), start.Width.ToString(CultureInfo.InvariantCulture));
        TextBox poly = DialogParts.Field("CustomCrc_Poly", Loc.Get("CustomCrc_Poly"), "0x" + start.FormatValue(start.Poly));
        TextBox init = DialogParts.Field("CustomCrc_Init", Loc.Get("CustomCrc_Init"), "0x" + start.FormatValue(start.Init));
        CheckBox refIn = DialogParts.Check("CustomCrc_RefIn", Loc.Get("CustomCrc_RefIn"), start.RefIn);
        CheckBox refOut = DialogParts.Check("CustomCrc_RefOut", Loc.Get("CustomCrc_RefOut"), start.RefOut);
        TextBox xorOut = DialogParts.Field("CustomCrc_XorOut", Loc.Get("CustomCrc_XorOut"), "0x" + start.FormatValue(start.XorOut));
        TextBlock check = DialogParts.Caption("CustomCrc_Check", monospace: true);
        TextBlock residue = DialogParts.Caption("CustomCrc_Residue", monospace: true);
        TextBlock error = DialogParts.Caption("CustomCrc_Error");
        error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        presetRow.Children.Add(preset);
        duplicate.VerticalAlignment = VerticalAlignment.Bottom;
        presetRow.Children.Add(duplicate);
        var body = new StackPanel { Spacing = 8, MinWidth = 400 };
        foreach (UIElement e in new UIElement[] { presetRow, name, width, poly, init, refIn, refOut, xorOut, check, residue, error })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "CustomCrcDialog", Loc.Get("CustomCrc_Title"), new ScrollViewer { Content = body, MaxHeight = 560 },
            Loc.Get("CustomCrc_Save"));
        var fields = new Dictionary<CustomCrcField, Control>
        {
            [CustomCrcField.Name] = name,
            [CustomCrcField.Width] = width,
            [CustomCrcField.Poly] = poly,
            [CustomCrcField.Init] = init,
            [CustomCrcField.XorOut] = xorOut,
        };
        CustomCrcDefinition? result = null;
        var context = Vm.Selected is { } d ? new EditorExpressionContext(d.Editor) : null;
        bool Number(TextBox box, out ulong value)
        {
            if (CustomCrcJson.TryParseNumber(box.Text.Trim(), out value))
            {
                return true;
            }

            // 入力式 (00-overview 6 章) も受け付ける。
            if (context is not null && ExpressionEvaluator.TryEvaluate(box.Text, context, out long v, out _) && v >= 0)
            {
                value = (ulong)v;
                return true;
            }

            return false;
        }

        void Validate()
        {
            result = null;
            foreach (Control c in fields.Values)
            {
                DialogParts.MarkInvalid(c, false);
            }

            check.Text = residue.Text = string.Empty;
            var problems = new List<(CustomCrcField Field, string Message)>();
            if (!int.TryParse(width.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int w))
            {
                problems.Add((CustomCrcField.Width, Loc.Get("CustomCrc_Error_InvalidValue")));
            }

            ulong ParseOr(TextBox box, CustomCrcField field)
            {
                if (Number(box, out ulong v))
                {
                    return v;
                }

                problems.Add((field, Loc.Get("CustomCrc_Error_InvalidValue")));
                return 0;
            }

            ulong p = ParseOr(poly, CustomCrcField.Poly), i = ParseOr(init, CustomCrcField.Init), x = ParseOr(xorOut, CustomCrcField.XorOut);
            if (problems.Count == 0)
            {
                var definition = new CustomCrcDefinition(name.Text, w, p, i, refIn.IsChecked == true, refOut.IsChecked == true, x);
                foreach (CustomCrcError e in CustomCrcs.Validate(definition, initial?.Name is { } n && CustomCrcs.Find(n) is not null ? n : null))
                {
                    problems.Add((e.Field, CustomCrcErrorText(e)));
                }

                // check と residue は入力中にその場で表示する (仕様 3)。
                if (definition.Check is ulong c && definition.Residue is ulong r)
                {
                    check.Text = Loc.Format("CustomCrc_CheckValue", definition.FormatValue(c));
                    residue.Text = Loc.Format("CustomCrc_ResidueValue", definition.FormatValue(r));
                }

                if (problems.Count == 0)
                {
                    result = definition;
                }
            }

            foreach ((CustomCrcField field, _) in problems)
            {
                if (fields.TryGetValue(field, out Control? c))
                {
                    DialogParts.MarkInvalid(c, true);
                }
            }

            error.Text = string.Join(" ", problems.Select(x => x.Message).Distinct());
            dialog.IsPrimaryButtonEnabled = result is not null;
        }

        duplicate.Click += (_, _) =>
        {
            if (preset.SelectedIndex >= 0)
            {
                CustomCrcDefinition copy = CustomCrcDefinition.FromPreset(presets[preset.SelectedIndex], CustomCrcs.Items.Select(x => x.Name));
                name.Text = copy.Name;
                width.Text = copy.Width.ToString(CultureInfo.InvariantCulture);
                poly.Text = "0x" + copy.FormatValue(copy.Poly);
                init.Text = "0x" + copy.FormatValue(copy.Init);
                refIn.IsChecked = copy.RefIn;
                refOut.IsChecked = copy.RefOut;
                xorOut.Text = "0x" + copy.FormatValue(copy.XorOut);
                Validate();
            }
        };
        foreach (TextBox box in new[] { name, width, poly, init, xorOut })
        {
            box.TextChanged += (_, _) => Validate();
        }

        foreach (CheckBox box in new[] { refIn, refOut })
        {
            box.Checked += (_, _) => Validate();
            box.Unchecked += (_, _) => Validate();
        }

        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || result is null)
        {
            return;
        }

        IReadOnlyList<CustomCrcError> errors = CustomCrcs.Add(result);
        if (errors.Count > 0)
        {
            ShowNotice(string.Join(" ", errors.Select(CustomCrcErrorText)), InfoBarSeverity.Error, Vm.Selected);
            return;
        }

        // 定義したものを選んでおく。
        if (_hashVm?.Algorithms.FirstOrDefault(a => a.Id == result.Id) is { } item)
        {
            item.IsChecked = true;
        }
    }

    private static string CustomCrcErrorText(CustomCrcError error) => error.Code switch
    {
        CustomCrcErrorCode.ValueTooWide => Loc.Format("CustomCrc_Error_ValueTooWide", Loc.Get("CustomCrc_Field_" + error.Field), error.Width),
        CustomCrcErrorCode.MissingField or CustomCrcErrorCode.InvalidValue =>
            Loc.Format("CustomCrc_Error_" + error.Code, Loc.Get("CustomCrc_Field_" + error.Field)),
        _ => Loc.Get("CustomCrc_Error_" + error.Code),
    };

    private async Task ExportCustomCrcAsync()
    {
        string? path;
        if (!TestHooks.TrySavePicker("custom-crc.json", out path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = "custom-crc.json", SettingsIdentifier = "HexEditor.CustomCrc" };
            picker.FileTypeChoices.Add("JSON", [".json"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, CustomCrcs.Export(), new System.Text.UTF8Encoding(false));
            ShowNotice(Loc.Format("CustomCrc_Exported", Path.GetFileName(path)), InfoBarSeverity.Success, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Hash_SaveFailed", ex.Message), InfoBarSeverity.Error, null);
        }
    }

    private async Task ImportCustomCrcAsync()
    {
        const string settingsIdentifier = "HexEditor.CustomCrc";
        string? path = TestHooks.OpenPickerResult(settingsIdentifier)?.FirstOrDefault();
        if (path is null && TestHooks.OpenPickerResult(settingsIdentifier) is null)
        {
            var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add("*");
            path = (await picker.PickSingleFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            CustomCrcImportResult result = CustomCrcs.Import(await File.ReadAllTextAsync(path));
            if (result.Errors.Count > 0)
            {
                // 「n 件目の項目を読み込めません: 理由」(ANA-20 の「エラー」)。正しい項目は取り込む。
                string details = string.Join(" ", result.Errors.Select(e => e.ItemNumber > 0
                    ? Loc.Format("CustomCrc_ImportItemError", e.ItemNumber, CustomCrcErrorText(e.Error))
                    : CustomCrcErrorText(e.Error)));
                ShowNotice(details, InfoBarSeverity.Warning, null);
            }

            if (result.Items.Count > 0)
            {
                ShowNotice(Loc.Format("CustomCrc_Imported", result.Items.Count), InfoBarSeverity.Success, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Hash_SaveFailed", ex.Message), InfoBarSeverity.Error, null);
        }
    }
}
