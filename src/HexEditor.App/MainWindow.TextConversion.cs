using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Notifications;
using HexEditor.Core.Saving;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// データ > 文字コード変換 (EDIT-38)、大文字・小文字の変換 (EDIT-39)。変換は Core の <see cref="CharsetConverter"/> と <see cref="CaseConversion"/>。
/// 4 MiB を超える対象は長時間処理として実行し、結果は 1 つの編集グループで反映する。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>大文字・小文字の変換の方法の設定 (EDIT-39 の仕様 1)。</summary>
    public const string CaseConversionKey = "edit.caseConversion";

    private const string ConvertEncodingStateKey = "data.convertEncoding.dialog";

    private void RegisterTextConversionCommands()
    {
        Commands.Register("data.convertEncoding", ConvertEncodingAsync, () => NeedsEditable(DataTargetReason));
        foreach ((string op, CaseOperation operation) in new[] { ("upper", CaseOperation.Upper), ("lower", CaseOperation.Lower), ("swap", CaseOperation.Swap) })
        {
            Commands.Register("data.case." + op, () => ConvertCaseAsync(operation, null), () => NeedsEditable(DataTargetReason));
            Commands.Register($"data.case.{op}.ascii", () => ConvertCaseAsync(operation, CaseConversionMode.AsciiOnly), () => NeedsEditable(DataTargetReason));
            Commands.Register($"data.case.{op}.text", () => ConvertCaseAsync(operation, CaseConversionMode.EncodingAware), () => NeedsEditable(DataTargetReason));
        }
    }

    /// <summary>右クリックメニューの「文字コード変換...」(EDIT-38 の「呼び出し」)。</summary>
    private void ExtendHexViewTextMenu(MenuFlyout menu, int index)
    {
        const string id = "HexViewMenu_data.convertEncoding";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == id))
        {
            var item = new MenuFlyoutItem { Text = Loc.Get("Cmd_data_convertEncoding") };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => _ = Commands.ExecuteAsync("data.convertEncoding");
            menu.Items.Insert(Math.Min(index, menu.Items.Count), item);
        }
    }

    /// <summary>変換の対象 (選択範囲。なければドキュメント全体)。</summary>
    private IReadOnlyList<TargetRange> TextTargetOf(DocumentViewModel doc) =>
        SelectionRangesOf(doc) is { Count: > 0 } ranges ? ranges : [new TargetRange(0, doc.Document.Length)];

    // ---- 文字コード変換 (EDIT-38) ----

    private async Task ConvertEncodingAsync()
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc))
        {
            return;
        }

        IReadOnlyList<TargetRange> ranges = TextTargetOf(doc);
        if (await ShowConvertEncodingDialogAsync(doc, ranges) is not { } options)
        {
            return;
        }

        string name = Loc.Get("Operation_ConvertEncoding");
        Document document = doc.Document;
        DocumentSnapshot snapshot = document.Current;
        Func<ContentSink> sink = () => ContentSink.For(document, CheckTempSpace(document));
        IReadOnlyList<RangeReplacement>? parts = await RunTextTransformAsync(doc, name, ranges,
            op => CharsetConverter.ConvertAll(snapshot, ranges, options, sink, op));
        ApplyTextTransform(doc, parts, "文字コード変換");
        FocusEditor();
    }

    /// <summary>一時領域の空きを確かめる (足りなければ <see cref="Core.Editing.TempSpaceException"/>)。</summary>
    private static Action<long> CheckTempSpace(Document document) => required =>
    {
        IVolumeInfoProvider volumes = TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance;
        string folder = Path.Combine(document.Options.TempDirectory, document.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        if (volumes.GetVolume(folder)?.AvailableFreeSpace is long available && available < required)
        {
            throw new Core.Editing.TempSpaceException(required, available);
        }
    };

    private async Task<CharsetConversionOptions?> ShowConvertEncodingDialogAsync(DocumentViewModel doc, IReadOnlyList<TargetRange> ranges)
    {
        EncodingEntry[] entries = [.. EncodingCatalog.All.Where(e => e.Selectable)];
        string current = doc.Editor.TextEncoding.Id;

        // 変換元・変換先は検索できるドロップダウン (名前・ID・コードページ番号の一部で絞り込める。EDIT-38 の「画面」)。
        var source = new EncodingPicker("ConvertEncoding_Source", Loc.Get("ConvertEncoding_Source"), entries, current);
        var target = new EncodingPicker("ConvertEncoding_Target", Loc.Get("ConvertEncoding_Target"), entries, "utf-8");
        ComboBox invalid = DialogParts.Combo("ConvertEncoding_Invalid", Loc.Get("ConvertEncoding_Invalid"),
            [Loc.Get("ConvertEncoding_Invalid_Error"), Loc.Get("ConvertEncoding_Invalid_Fffd"), Loc.Get("ConvertEncoding_Invalid_Keep")], 0);
        ComboBox unmappable = DialogParts.Combo("ConvertEncoding_Unmappable", Loc.Get("ConvertEncoding_Unmappable"),
            [Loc.Get("ConvertEncoding_Unmappable_Error"), Loc.Get("ConvertEncoding_Unmappable_Question"), Loc.Get("ConvertEncoding_Unmappable_Custom")], 0);
        TextBox replacement = DialogParts.Field("ConvertEncoding_Replacement", Loc.Get("ConvertEncoding_Replacement"), "?", monospace: false);
        CheckBox stripBom = DialogParts.Check("ConvertEncoding_StripBom", Loc.Get("ConvertEncoding_StripBom"), true);
        CheckBox addBom = DialogParts.Check("ConvertEncoding_AddBom", Loc.Get("ConvertEncoding_AddBom"), false);
        ComboBox newline = DialogParts.Combo("ConvertEncoding_Newline", Loc.Get("ConvertEncoding_Newline"),
            [Loc.Get("ConvertEncoding_Newline_Keep"), "CRLF", "LF", "CR"], 0);
        ComboBox normalization = DialogParts.Combo("ConvertEncoding_Normalization", Loc.Get("ConvertEncoding_Normalization"),
            [Loc.Get("ConvertEncoding_Normalization_None"), "NFC", "NFD", "NFKC", "NFKD"], 0);
        TextBlock before = DialogParts.Caption("ConvertEncoding_PreviewBefore", monospace: true);
        TextBlock after = DialogParts.Caption("ConvertEncoding_PreviewAfter", monospace: true);
        TextBlock beforeText = DialogParts.Caption("ConvertEncoding_PreviewBeforeText");
        TextBlock afterText = DialogParts.Caption("ConvertEncoding_PreviewAfterText");
        TextBlock length = DialogParts.Caption("ConvertEncoding_Length");
        TextBlock error = DialogParts.Caption("ConvertEncoding_Error");
        error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var body = new StackPanel { Spacing = 8, MinWidth = 440 };
        foreach (UIElement e in new UIElement[] { source, target, invalid, unmappable, replacement, stripBom, addBom, newline, normalization,
            before, beforeText, after, afterText, length, error })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ConvertEncodingDialog", Loc.Get("ConvertEncoding_Title"),
            new ScrollViewer { Content = body, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) }, Loc.Get("ConvertEncoding_Convert"));
        LoadComboStates(ConvertEncodingStateKey, (invalid, "invalid"), (unmappable, "unmappable"), (newline, "newline"), (normalization, "normalization"));
        CharsetConversionOptions? result = null;
        void Update()
        {
            result = null;
            replacement.Visibility = unmappable.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            var options = new CharsetConversionOptions(source.Selected.Id, target.Selected.Id)
            {
                InvalidSource = invalid.SelectedIndex switch { 1 => InvalidSourceHandling.ReplaceWithFffd, 2 => InvalidSourceHandling.KeepBytes, _ => InvalidSourceHandling.Error },
                Unmappable = unmappable.SelectedIndex switch { 1 => UnmappableHandling.Question, 2 => UnmappableHandling.Custom, _ => UnmappableHandling.Error },
                CustomReplacement = replacement.Text,
                StripSourceBom = stripBom.IsChecked == true,
                AddTargetBom = addBom.IsChecked == true,
                Newline = (NewlineConversion)Math.Max(0, newline.SelectedIndex),
                Normalization = (UnicodeNormalization)Math.Max(0, normalization.SelectedIndex),
            };
            error.Text = before.Text = after.Text = beforeText.Text = afterText.Text = length.Text = string.Empty;
            DialogParts.MarkInvalid(replacement, false);
            try
            {
                // プレビュー: 先頭の範囲の先頭 256 バイトの変換前と変換後、変換後の推定の長さ (仕様 8)。
                CharsetConversionPreview preview = CharsetConverter.Preview(doc.Document.Current, ranges[0], options);
                before.Text = Loc.Format("DataOp_PreviewBeforeLine", DialogParts.Hex(preview.Before));
                after.Text = Loc.Format("DataOp_PreviewAfterLine", DialogParts.Hex(preview.After));
                beforeText.Text = DecodeForPreview(options.SourceEncodingId, preview.Before);
                afterText.Text = DecodeForPreview(options.TargetEncodingId, preview.After);
                long estimate = preview.EstimatedLength + ranges.Skip(1).Sum(r => r.Length);
                length.Text = Loc.Format(preview.LengthIsExact && ranges.Count == 1 ? "ConvertEncoding_LengthExact" : "ConvertEncoding_LengthEstimate",
                    StatusFormat.Number(ranges.Sum(r => r.Length), Culture), StatusFormat.Number(estimate, Culture));
                if (preview.Error is { } e)
                {
                    error.Text = ConversionErrorText(e);
                }

                bool changesLength = preview.LengthIsExact && ranges.Count == 1 ? preview.EstimatedLength != ranges[0].Length : true;
                if (!doc.Document.CanResize && preview.LengthIsExact && ranges.Count == 1 && changesLength)
                {
                    error.Text = Loc.Get("ConvertEncoding_Error_FixedLength");
                }
                else
                {
                    result = options;
                }
            }
            catch (ArgumentException ex)
            {
                error.Text = SettingsErrorText(ex);
                DialogParts.MarkInvalid(replacement, ex is CharsetSettingsException { Error: CharsetSettingsError.InvalidReplacement });
            }
            catch (IOException ex)
            {
                error.Text = Loc.Format("Fill_Error_File", ex.Message);
            }

            dialog.IsPrimaryButtonEnabled = result is not null;
        }

        foreach (ComboBox combo in new[] { invalid, unmappable, newline, normalization })
        {
            combo.SelectionChanged += (_, _) => Update();
        }

        source.SelectionChanged += (_, _) => Update();
        target.SelectionChanged += (_, _) => Update();

        foreach (CheckBox check in new[] { stripBom, addBom })
        {
            check.Checked += (_, _) => Update();
            check.Unchecked += (_, _) => Update();
        }

        replacement.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || result is null)
        {
            return null;
        }

        SaveComboStates(ConvertEncodingStateKey, (invalid, "invalid"), (unmappable, "unmappable"), (newline, "newline"), (normalization, "normalization"));
        return result;
    }

    private static string DecodeForPreview(string encodingId, byte[] bytes)
    {
        try
        {
            return TextEncoding.FromId(encodingId).Decode(bytes).Replace('\r', '␍').Replace('\n', '␊').Replace('\0', '␀');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string ConversionErrorText(CharsetConversionException e) => e.Kind == CharsetConversionErrorKind.UndecodableSource
        ? Loc.Format("ConvertEncoding_Error_Undecodable", StatusFormat.Hex(e.Offset), DialogParts.Hex(e.Bytes))
        : Loc.Format("ConvertEncoding_Error_Unmappable", StatusFormat.Hex(e.Offset), e.Text ?? string.Empty);

    /// <summary>設定の誤り (使えない文字コード、表せない置き換えの文字列) の文言。Core の例外の文言は使わない (地域化のため)。</summary>
    private static string SettingsErrorText(ArgumentException e) => e switch
    {
        CharsetSettingsException { Error: CharsetSettingsError.UnknownEncoding } s => Loc.Format("ConvertEncoding_Error_UnknownEncoding", s.Value),
        CharsetSettingsException s => Loc.Format("ConvertEncoding_Error_InvalidReplacement", s.Value),
        _ => Loc.Get("ConvertEncoding_Error_Settings"),
    };

    private static void LoadComboStates(string key, params (ComboBox Combo, string Name)[] combos)
    {
        try
        {
            string json = AppState.GetString(key, string.Empty);
            if (json.Length > 0 && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json) is { } state)
            {
                foreach ((ComboBox combo, string name) in combos)
                {
                    if (state.TryGetValue(name, out int i) && i >= 0 && i < combo.Items.Count)
                    {
                        combo.SelectedIndex = i;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    private static void SaveComboStates(string key, params (ComboBox Combo, string Name)[] combos) =>
        AppState.SetString(key, System.Text.Json.JsonSerializer.Serialize(combos.ToDictionary(c => c.Name, c => c.Combo.SelectedIndex)));

    // ---- 大文字・小文字の変換 (EDIT-39) ----

    /// <summary>大文字・小文字に変換する。<paramref name="mode"/> が null なら設定の方法 (既定は ASCII の英字だけ)。</summary>
    private async Task ConvertCaseAsync(CaseOperation operation, CaseConversionMode? mode)
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc))
        {
            return;
        }

        CaseConversionMode method = mode ?? (App.Settings.GetString(CaseConversionKey, "ascii") == "encoding"
            ? CaseConversionMode.EncodingAware : CaseConversionMode.AsciiOnly);
        IReadOnlyList<TargetRange> ranges = TextTargetOf(doc);
        Document document = doc.Document;
        DocumentSnapshot snapshot = document.Current;
        string encoding = doc.Editor.TextEncoding.Id;
        Func<ContentSink> sink = () => ContentSink.For(document, CheckTempSpace(document));
        var stats = new CaseConversionStats();
        IReadOnlyList<RangeReplacement>? parts = await RunTextTransformAsync(doc, Loc.Get("Operation_ConvertCase"), ranges,
            op => CaseConversion.ConvertAll(snapshot, ranges, operation, method, encoding, sink, op, stats));

        // 値が変わらない変換は反映しない (元に戻す操作を作らない)。
        if (parts is not null && !stats.Changed)
        {
            TransformApplier.DisposeAll(parts);
            ShowNotice(Loc.Get("DataOp_Unchanged"), InfoBarSeverity.Informational, doc);
            return;
        }

        ApplyTextTransform(doc, parts, "大文字・小文字の変換");
    }

    // ---- 共通 ----

    private async Task<IReadOnlyList<RangeReplacement>?> RunTextTransformAsync(DocumentViewModel doc, string name, IReadOnlyList<TargetRange> ranges,
        Func<Core.Operations.LongRunningOperation?, IReadOnlyList<RangeReplacement>> work)
    {
        try
        {
            return await RunTransformAsync(doc, name, TextTransforms.TotalLength(ranges), TextTransforms.IsLongRunning(TextTransforms.TotalLength(ranges)), work);
        }
        catch (CharsetConversionException e)
        {
            // 最初の箇所のオフセットと文字を示し、「そのオフセットへ移動」を付ける。ドキュメントは変えない。
            ShowNotice(ConversionErrorText(e), InfoBarSeverity.Error, doc, actions:
                [new NotificationAction(Loc.Get("ConvertEncoding_GoToOffset"), () =>
                {
                    doc.Editor.RecordJump();
                    doc.Editor.GoTo(e.Offset);
                    FocusEditor();
                })]);
            return null;
        }
        catch (ArgumentException e)
        {
            ShowNotice(SettingsErrorText(e), InfoBarSeverity.Error, doc);
            return null;
        }
    }

    private void ApplyTextTransform(DocumentViewModel doc, IReadOnlyList<RangeReplacement>? parts, string description)
    {
        if (parts is null)
        {
            return;
        }

        if (!doc.Document.CanResize && TextTransforms.ChangesLength(parts))
        {
            TransformApplier.DisposeAll(parts);
            ShowNotice(Loc.Get("ConvertEncoding_Error_FixedLength"), InfoBarSeverity.Error, doc);
            return;
        }

        // 値が変わらなければ反映しない。
        IReadOnlyList<TargetRange> after = TransformApplier.ResultRanges(parts);
        if (!ApplyEdit(doc, () => TransformApplier.Apply(doc.Document, parts, description)))
        {
            TransformApplier.DisposeAll(parts);
            return;
        }

        // 1 つの範囲なら、変換した結果を選択する (長さが変わる場合も結果の範囲)。
        if (after.Count == 1 && after[0].Length > 0)
        {
            doc.Editor.Select(after[0].Offset, after[0].Length);
        }
    }
}
