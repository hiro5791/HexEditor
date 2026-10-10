using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>形式を選択してコピー (EDIT-25)・形式を選択して貼り付け (EDIT-26) のダイアログとコマンド。変換は Core の CopyFormatter / PasteDetector。</summary>
public sealed partial class MainWindow
{
    private const string CopyAsLastKey = "edit.copyAs.last";
    private const string PasteSpecialLastKey = "edit.pasteSpecial.last";

    /// <summary>設定「前回の形式を優先する」(EDIT-26 の仕様 6。既定オン)。</summary>
    public const string PreferLastPasteFormatKey = "edit.pasteSpecial.preferLast";

    /// <summary>設定「Hex 列で自動判別したときに確認しない」(EDIT-23 の仕様 2。既定オフ)。</summary>
    public const string PasteWithoutConfirmationKey = "edit.paste.detectWithoutConfirmation";

    /// <summary>「形式を選択してコピー」の設定 (記憶するもの。EDIT-25 の仕様 2)。</summary>
    private sealed record CopyAsState
    {
        public CopyFormat Format { get; init; } = CopyFormat.HexSpaced;

        public bool LineFeedOnly { get; init; }

        public int IndentIndex { get; init; }

        public int BytesPerLine { get; init; } = 16;

        public bool UpperCase { get; init; } = true;

        public string VariableName { get; init; } = "data";

        public int ElementSizeIndex { get; init; }

        public bool BigEndian { get; init; }

        public bool Variant { get; init; }

        public int SyntaxIndex { get; init; }

        public bool Wrap { get; init; }

        public bool Delimiters { get; init; } = true;

        public string FileName { get; init; } = "data.bin";

        public int LayoutIndex { get; init; }

        public bool Colors { get; init; } = true;

        public int RecordBytes { get; init; } = 16;

        public long BaseAddress { get; init; }

        public string Header { get; init; } = string.Empty;

        public int JsonDataIndex { get; init; }

        public bool DecimalPosition { get; init; }

        public string Separator { get; init; } = ":";

        public string Prefix { get; init; } = string.Empty;

        public string Suffix { get; init; } = string.Empty;

        /// <summary>マルチ選択を要素ごとに分ける (EDIT-25 の仕様 7。既定は連結)。</summary>
        public bool SeparateRanges { get; init; }
    }

    private static CopyAsState LoadCopyAsState()
    {
        try
        {
            string json = AppState.GetString(CopyAsLastKey, string.Empty);
            return json.Length > 0 ? JsonSerializer.Deserialize<CopyAsState>(json) ?? new CopyAsState() : new CopyAsState();
        }
        catch (JsonException)
        {
            return new CopyAsState();
        }
    }

    /// <summary>記憶した設定と、ドキュメントの状態 (文字コード・変更されたバイト・1 行のバイト数) から変換の設定を作る。</summary>
    private static CopyOptions OptionsOf(CopyAsState s, DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        long start = editor.HasSelection ? editor.SelectionStart : editor.Cursor;
        long length = editor.SelectionLength;
        string color = Application.Current.Resources["SystemFillColorCriticalBrush"] is SolidColorBrush brush
            ? $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}"
            : "#C42B1C";
        return new CopyOptions
        {
            NewLine = s.LineFeedOnly ? "\n" : "\r\n",
            Indent = s.IndentIndex switch { 1 => "  ", 2 => "\t", _ => "    " },
            BytesPerLine = s.BytesPerLine,
            UpperCase = s.UpperCase,
            VariableName = s.VariableName,
            ElementSize = 1 << Math.Clamp(s.ElementSizeIndex, 0, 3),
            BigEndian = s.BigEndian,
            CSharpSpan = s.Variant,
            PythonBytesLiteral = s.Variant,
            Assembly = (AssemblySyntax)Math.Clamp(s.SyntaxIndex, 0, 2),
            Base64UrlSafe = s.Variant,
            Base64Wrap = s.Wrap,
            Base32Hex = s.Variant,
            Ascii85 = s.Variant ? Ascii85Variant.Z85 : Ascii85Variant.Adobe,
            Ascii85Delimiters = s.Delimiters,
            EncodedFileName = s.FileName,
            Layout = s.LayoutIndex == 1 ? DocumentLayout.Table : DocumentLayout.Dump,
            IncludeColors = s.Colors,
            ModifiedRanges = length > 0 ? [.. doc.Document.Current.EnumerateModifiedRanges(start, length)] : [],
            ModifiedColor = color,
            ScreenBytesPerRow = editor.BytesPerRow,

            // 画面表示どおり: グループ化・中央区切り・セルの表示形式・逆順表示・オフセットの基数・列の有無も画面に合わせる
            // (EDIT-25 の仕様 6、VIEW-11 の仕様 5)。
            ScreenGroupSize = editor.View.GroupSize,
            ScreenMiddleSeparator = editor.View.EffectiveMiddleSeparator(editor.BytesPerRow),
            ScreenCellFormat = editor.View.CellFormat,
            ScreenReverseGroups = editor.View.ReverseGroups,
            ScreenBigEndian = editor.View.BigEndian,
            ScreenSpacePadding = editor.View.SpacePadding,
            DecimalOffsets = editor.View.Radix == OffsetRadix.Decimal,
            ShowOffset = editor.View.ShowOffsetColumn,
            ShowHex = editor.View.ShowHexColumn,
            ShowText = editor.View.ShowTextColumn || !editor.View.ShowHexColumn,
            Encoding = editor.TextEncoding,
            RecordBytes = s.RecordBytes,
            BaseAddress = s.BaseAddress,
            SRecordHeader = s.Header,
            JsonData = (JsonDataEncoding)Math.Clamp(s.JsonDataIndex, 0, 2),
            DecimalPosition = s.DecimalPosition,
            PositionLengthFormat = Loc.Get("CopyAs_PositionLength"),
            CustomSeparator = s.Separator,
            CustomPrefix = s.Prefix,
            CustomSuffix = s.Suffix,
        };
    }

    /// <summary>出力の推定サイズ (バイト。クリップボードのテキストは UTF-16)。</summary>
    private long EstimateBytes(CopyFormat format, CopyOptions options, DocumentSnapshot snapshot, long start, long length) =>
        _copyAsRanges is { } ranges
            ? CopyFormatter.EstimateChars(format, options, CopyFormatter.Concatenated(ranges, (o, d) => snapshot.Read(o, d)), 0, ranges.Sum(r => r.Length)) * 2
            : CopyFormatter.EstimateChars(format, options, (o, d) => snapshot.Read(o, d), start, length) * 2;

    // マルチ選択・矩形選択の「形式を選択してコピー」の要素と、連結するか分けるか (EDIT-25 の仕様 7)。単一の選択では null。
    private IReadOnlyList<Core.Selection.ByteRange>? _copyAsRanges;
    private CopyRangesMode _copyAsMode;

    /// <summary>今の選択からマルチ選択の要素を決める (矩形は行ごとに改行する)。</summary>
    private void PrepareCopyAsRanges(EditorState editor, bool separate)
    {
        _copyAsRanges = editor.HasMultipleRanges ? [.. editor.SelectedRanges] : null;
        _copyAsMode = editor.SelectionKind == SelectionKind.Rectangle ? CopyRangesMode.Rows : separate ? CopyRangesMode.Separate : CopyRangesMode.Concatenate;
    }

    /// <summary>出力を書く (マルチ選択なら要素ごと・連結)。</summary>
    private IReadOnlyList<CopyNote> WriteCopyAs(CopyFormat format, CopyOptions options, DocumentSnapshot snapshot, long start, long length,
        TextWriter writer, CancellationToken cancellationToken = default, Action<long>? progress = null) =>
        _copyAsRanges is { } ranges
            ? CopyFormatter.WriteRanges(format, options, (o, d) => snapshot.Read(o, d), ranges, _copyAsMode, writer, cancellationToken, progress)
            : CopyFormatter.Write(format, options, (o, d) => snapshot.Read(o, d), start, length, writer, cancellationToken, progress);

    // ---- 形式を選択してコピー (EDIT-25) ----

    private async void CopyAs_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await ShowCopyAsDialogAsync(doc);
        }
    }

    /// <summary>前回の形式でコピー (ダイアログを開かない。EDIT-25 の仕様 2)。</summary>
    private async void CopyAsLast_Click(object sender, RoutedEventArgs e) => await CopyInFormatAsync(null);

    /// <summary>
    /// ダイアログを開かずにコピーする (<paramref name="format"/> が null なら前回の形式。指定したらその形式を前回の形式として記憶する)。
    /// 形式ごとの設定は前回の設定を使う (EDIT-25 の仕様 2、「呼び出し」の「形式を選択してコピー: &lt;形式名&gt;」)。
    /// </summary>
    private async Task CopyInFormatAsync(CopyFormat? format)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        CopyAsState state = LoadCopyAsState();
        if (format is { } chosen && chosen != state.Format)
        {
            state = state with { Format = chosen };
            AppState.SetString(CopyAsLastKey, JsonSerializer.Serialize(state));
        }

        CopyOptions options = OptionsOf(state, doc);
        long start = doc.Editor.HasSelection ? doc.Editor.SelectionStart : doc.Editor.Cursor;
        long length = doc.Editor.SelectionLength;
        PrepareCopyAsRanges(doc.Editor, state.SeparateRanges);
        if (CopyFormatter.Validate(state.Format, options, start, length) is not null)
        {
            ShowNotice(Loc.Get("CopyAs_Error_Options"), InfoBarSeverity.Error, doc);
            return;
        }

        if (EstimateBytes(state.Format, options, doc.Document.Current, start, length) > ClipboardService.SystemLimit)
        {
            ShowNotice(Loc.Get("CopyAs_TooLarge"), InfoBarSeverity.Error, doc);
            return;
        }

        await CopyFormattedAsync(doc, state.Format, options, start, length);
    }

    /// <summary>出力を作ってクリップボードに入れる。0.5 秒以上かかれば長時間処理として表示され、キャンセルしたらクリップボードを変えない (仕様 9)。</summary>
    private async Task CopyFormattedAsync(DocumentViewModel doc, CopyFormat format, CopyOptions options, long start, long length)
    {
        DocumentSnapshot snapshot = doc.Document.Current;
        try
        {
            (string text, string? html, string? rtf) = await Vm.Operations.RunAsync(Loc.Get("Operation_CopyAs"), OperationKind.ReadOnly, doc.Document, length,
                op =>
                {
                    string Render(CopyFormat f)
                    {
                        var writer = new StringWriter(CultureInfo.InvariantCulture);
                        WriteCopyAs(f, options, snapshot, start, length, writer, op.CancellationToken, op.Report);
                        return writer.ToString();
                    }

                    string main = Render(format);
                    return Task.FromResult(format switch
                    {
                        // HTML・RTF は、その形式とテキスト (画面表示どおりのダンプ) の両方で入れる。
                        CopyFormat.Html => (Render(CopyFormat.ScreenDump), (string?)main, (string?)null),
                        CopyFormat.Rtf => (Render(CopyFormat.ScreenDump), null, main),
                        _ => (main, null, null),
                    });
                });
            _clipboard.CopyFormatted(text, html, rtf);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ShowCopyAsDialogAsync(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        long start = editor.HasSelection ? editor.SelectionStart : editor.Cursor;
        long length = editor.SelectionLength;
        CopyAsState state = LoadCopyAsState();
        CopyFormat[] all = Enum.GetValues<CopyFormat>();
        string Name(CopyFormat f) => Loc.Get("CopyAs_Format_" + f);

        var search = new TextBox { PlaceholderText = Loc.Get("CopyAs_Search"), Width = 220 };
        AutomationProperties.SetAutomationId(search, "CopyAs_Search");
        AutomationProperties.SetName(search, Loc.Get("CopyAs_Search"));
        var list = new ListView { Width = 220, Height = 360, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(list, "CopyAs_Formats");
        AutomationProperties.SetName(list, Loc.Get("CopyAs_FormatsName"));
        var shown = new List<CopyFormat>();
        void Fill()
        {
            CopyFormat? selected = list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : state.Format;
            shown.Clear();
            list.Items.Clear();
            foreach (CopyFormat f in all)
            {
                string label = Loc.Get("CopyAs_Category_" + CopyFormatter.CategoryOf(f)) + ": " + Name(f);
                if (search.Text.Length == 0 || label.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase))
                {
                    shown.Add(f);
                    list.Items.Add(label);
                }
            }

            list.SelectedIndex = selected is { } s ? shown.IndexOf(s) : -1;
            if (list.SelectedIndex < 0 && shown.Count > 0)
            {
                list.SelectedIndex = 0;
            }
        }

        // 形式ごとの設定 (使わない設定は隠す)。
        TextBox bytesPerLine = DialogParts.Field("CopyAs_BytesPerLine", Loc.Get("CopyAs_BytesPerLine"), state.BytesPerLine.ToString(CultureInfo.InvariantCulture));
        CheckBox upper = DialogParts.Check("CopyAs_UpperCase", Loc.Get("CopyAs_UpperCase"), state.UpperCase);
        ComboBox newLine = DialogParts.Combo("CopyAs_NewLine", Loc.Get("CopyAs_NewLine"), ["CRLF", "LF"], state.LineFeedOnly ? 1 : 0);
        ComboBox indent = DialogParts.Combo("CopyAs_Indent", Loc.Get("CopyAs_Indent"),
            [Loc.Get("CopyAs_Indent4"), Loc.Get("CopyAs_Indent2"), Loc.Get("CopyAs_IndentTab")], state.IndentIndex);
        TextBox variable = DialogParts.Field("CopyAs_Variable", Loc.Get("CopyAs_Variable"), state.VariableName);
        ComboBox elementSize = DialogParts.Combo("CopyAs_ElementSize", Loc.Get("CopyAs_ElementSize"), ["1", "2", "4", "8"], state.ElementSizeIndex);
        CheckBox bigEndian = DialogParts.Check("CopyAs_BigEndian", Loc.Get("CopyAs_BigEndian"), state.BigEndian);
        CheckBox variant = DialogParts.Check("CopyAs_Variant", string.Empty, state.Variant);
        ComboBox syntax = DialogParts.Combo("CopyAs_Assembly", Loc.Get("CopyAs_Assembly"), ["NASM", "MASM", "GAS"], state.SyntaxIndex);
        CheckBox wrap = DialogParts.Check("CopyAs_Wrap", Loc.Get("CopyAs_Wrap"), state.Wrap);
        CheckBox delimiters = DialogParts.Check("CopyAs_Delimiters", Loc.Get("CopyAs_Delimiters"), state.Delimiters);
        TextBox fileName = DialogParts.Field("CopyAs_FileName", Loc.Get("CopyAs_FileName"), state.FileName);
        ComboBox layout = DialogParts.Combo("CopyAs_Layout", Loc.Get("CopyAs_Layout"), [Loc.Get("CopyAs_LayoutDump"), Loc.Get("CopyAs_LayoutTable")], state.LayoutIndex);
        CheckBox colors = DialogParts.Check("CopyAs_Colors", Loc.Get("CopyAs_Colors"), state.Colors);
        TextBox recordBytes = DialogParts.Field("CopyAs_RecordBytes", Loc.Get("CopyAs_RecordBytes"), state.RecordBytes.ToString(CultureInfo.InvariantCulture));
        TextBox baseAddress = DialogParts.Field("CopyAs_BaseAddress", Loc.Get("CopyAs_BaseAddress"), RangeSelectionModel.Format(state.BaseAddress));
        TextBox header = DialogParts.Field("CopyAs_Header", Loc.Get("CopyAs_Header"), state.Header);
        ComboBox jsonData = DialogParts.Combo("CopyAs_JsonData", Loc.Get("CopyAs_JsonData"), ["Base64", "Hex", Loc.Get("CopyAs_JsonNumbers")], state.JsonDataIndex);
        CheckBox decimalPosition = DialogParts.Check("CopyAs_DecimalPosition", Loc.Get("CopyAs_DecimalPosition"), state.DecimalPosition);
        TextBox separator = DialogParts.Field("CopyAs_Separator", Loc.Get("CopyAs_Separator"), state.Separator);
        TextBox prefix = DialogParts.Field("CopyAs_Prefix", Loc.Get("CopyAs_Prefix"), state.Prefix);
        TextBox suffix = DialogParts.Field("CopyAs_Suffix", Loc.Get("CopyAs_Suffix"), state.Suffix);
        TextBlock settingsError = DialogParts.Caption("CopyAs_SettingsError");

        // マルチ選択: 「連結する」(既定) / 「要素ごとに分ける」(EDIT-25 の仕様 7)。矩形選択は行ごとに改行する。
        CheckBox separateRanges = DialogParts.Check("CopyAs_SeparateRanges", Loc.Get("CopyAs_SeparateRanges"), state.SeparateRanges);
        separateRanges.Visibility = editor.SelectionKind == SelectionKind.Multiple ? Visibility.Visible : Visibility.Collapsed;
        PrepareCopyAsRanges(editor, state.SeparateRanges);
        var settings = new StackPanel { Spacing = 6, Width = 300 };
        foreach (UIElement el in new UIElement[]
        {
            separateRanges, bytesPerLine, upper, newLine, indent, variable, elementSize, bigEndian, variant, syntax, wrap, delimiters, fileName, layout, colors,
            recordBytes, baseAddress, header, jsonData, decimalPosition, separator, prefix, suffix, settingsError,
        })
        {
            settings.Children.Add(el);
        }

        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = DialogParts.Mono,
            Height = 200,
            Width = 540,
            FlowDirection = FlowDirection.LeftToRight,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(preview, "CopyAs_Preview");
        AutomationProperties.SetName(preview, Loc.Get("CopyAs_PreviewName"));
        TextBlock estimate = DialogParts.Caption("CopyAs_Estimate");
        TextBlock note = DialogParts.Caption("CopyAs_Note");

        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(search);
        left.Children.Add(list);
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        top.Children.Add(left);
        top.Children.Add(new ScrollViewer { Content = settings, Height = 400 });
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(top);
        body.Children.Add(preview);
        body.Children.Add(estimate);
        body.Children.Add(note);

        ContentDialog dialog = DialogParts.Dialog(Root, "CopyAsDialog", Loc.Get("CopyAs_Title"), body, Loc.Get("CopyAs_Copy"), Loc.Get("CopyAs_SaveToFile"));
        dialog.CloseButtonText = Loc.Get("Common_Close");
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        CopyFormat current = state.Format;
        CopyOptions options = OptionsOf(state, doc);
        bool valid = false;
        CopyAsState Read()
        {
            return new CopyAsState
            {
                Format = current,
                LineFeedOnly = newLine.SelectedIndex == 1,
                IndentIndex = Math.Max(0, indent.SelectedIndex),
                BytesPerLine = int.TryParse(bytesPerLine.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bpl) ? bpl : -1,
                UpperCase = upper.IsChecked == true,
                VariableName = variable.Text,
                ElementSizeIndex = Math.Max(0, elementSize.SelectedIndex),
                BigEndian = bigEndian.IsChecked == true,
                Variant = variant.IsChecked == true,
                SyntaxIndex = Math.Max(0, syntax.SelectedIndex),
                Wrap = wrap.IsChecked == true,
                Delimiters = delimiters.IsChecked == true,
                FileName = fileName.Text,
                LayoutIndex = Math.Max(0, layout.SelectedIndex),
                Colors = colors.IsChecked == true,
                RecordBytes = int.TryParse(recordBytes.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rb) ? rb : -1,
                BaseAddress = DialogParts.TryEvaluate(baseAddress.Text, new EditorExpressionContext(editor), out long ba, out _) ? ba : -1,
                Header = header.Text,
                JsonDataIndex = Math.Max(0, jsonData.SelectedIndex),
                DecimalPosition = decimalPosition.IsChecked == true,
                Separator = separator.Text,
                Prefix = prefix.Text,
                Suffix = suffix.Text,
                SeparateRanges = separateRanges.IsChecked == true,
            };
        }

        void Update()
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count)
            {
                current = shown[list.SelectedIndex];
            }

            CopyFormatCategory category = CopyFormatter.CategoryOf(current);
            bool array = category == CopyFormatCategory.Array;
            void Show(FrameworkElement el, bool visible) => el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Show(bytesPerLine, category is CopyFormatCategory.HexString or CopyFormatCategory.Numbers or CopyFormatCategory.Array && current != CopyFormat.HexUrl);
            Show(upper, current is not (CopyFormat.Decimal or CopyFormat.Octal or CopyFormat.Binary or CopyFormat.Base64 or CopyFormat.Base32
                or CopyFormat.Ascii85 or CopyFormat.UUEncode or CopyFormat.XXEncode or CopyFormat.Text));
            Show(newLine, current != CopyFormat.Position);
            Show(indent, array);
            Show(variable, array);
            Show(elementSize, array && current != CopyFormat.ArrayPython);
            Show(bigEndian, array && current != CopyFormat.ArrayPython);
            (string? variantText, bool hasVariant) = current switch
            {
                CopyFormat.ArrayCSharp => (Loc.Get("CopyAs_Variant_CSharp"), true),
                CopyFormat.ArrayPython => (Loc.Get("CopyAs_Variant_Python"), true),
                CopyFormat.Base64 => (Loc.Get("CopyAs_Variant_Base64"), true),
                CopyFormat.Base32 => (Loc.Get("CopyAs_Variant_Base32"), true),
                CopyFormat.Ascii85 => (Loc.Get("CopyAs_Variant_Ascii85"), true),
                _ => (null, false),
            };
            variant.Content = variantText;
            Show(variant, hasVariant);
            Show(syntax, current == CopyFormat.ArrayAssembly);
            Show(wrap, current == CopyFormat.Base64);
            Show(delimiters, current == CopyFormat.Ascii85);
            Show(fileName, current is CopyFormat.UUEncode or CopyFormat.XXEncode);
            Show(layout, current is CopyFormat.Html or CopyFormat.Markdown or CopyFormat.Tex);
            Show(colors, current is CopyFormat.Html or CopyFormat.Rtf);
            Show(recordBytes, current is CopyFormat.IntelHex or CopyFormat.SRecord);
            Show(baseAddress, current is CopyFormat.IntelHex or CopyFormat.SRecord);
            Show(header, current == CopyFormat.SRecord);
            Show(jsonData, current == CopyFormat.Json);
            Show(decimalPosition, current == CopyFormat.Position);
            Show(separator, current == CopyFormat.HexCustom);
            Show(prefix, current == CopyFormat.HexCustom);
            Show(suffix, current == CopyFormat.HexCustom);

            CopyAsState s = Read();
            options = OptionsOf(s, doc);
            CopyOptionError? error = s.BaseAddress < 0 ? CopyOptionError.AddressTooLarge : CopyFormatter.Validate(current, options, start, length);
            DialogParts.MarkInvalid(variable, error == CopyOptionError.InvalidVariableName);
            DialogParts.MarkInvalid(bytesPerLine, error == CopyOptionError.InvalidLineLength && (s.BytesPerLine is < 1 or > 1024));
            DialogParts.MarkInvalid(recordBytes, error == CopyOptionError.InvalidLineLength && (s.RecordBytes is < 1 or > 255));
            settingsError.Text = error is { } e ? Loc.Get("CopyAs_Error_" + e) : string.Empty;
            valid = error is null;
            if (!valid)
            {
                preview.Text = string.Empty;
                estimate.Text = string.Empty;
                note.Text = string.Empty;
                dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
                return;
            }

            // プレビュー: 出力の先頭 4 KiB と、出力全体の推定サイズ (仕様 1)。
            DocumentSnapshot snapshot = doc.Document.Current;
            var limited = new LimitedWriter(CopyFormatter.PreviewChars);
            IReadOnlyList<CopyNote> notes = [];
            try
            {
                notes = WriteCopyAs(current, options, snapshot, start, length, limited);
            }
            catch (LimitedWriter.FullException)
            {
            }

            preview.Text = limited.ToString();
            long bytes = EstimateBytes(current, options, snapshot, start, length);
            estimate.Text = Loc.Format("CopyAs_Estimate", StatusFormat.ShortSize(bytes, Culture) ?? StatusFormat.Number(bytes, Culture), StatusFormat.Number(bytes, Culture));
            bool tooLarge = bytes > ClipboardService.SystemLimit;
            note.Text = string.Join(" ", new[]
            {
                notes.Contains(CopyNote.PaddedLastElement) || IsArrayPadded(current, options, length) ? Loc.Get("CopyAs_Note_Padded") : null,
                tooLarge ? Loc.Format("CopyAs_TooLargeNote", StatusFormat.ShortSize(ClipboardService.SystemLimit, Culture) ?? string.Empty) : null,
            }.Where(t => t is not null));

            // 推定サイズが上限を超える場合はコピーできず、ファイルに保存する (仕様 8)。
            dialog.IsPrimaryButtonEnabled = !tooLarge;
            dialog.IsSecondaryButtonEnabled = current != CopyFormat.Position;
        }

        search.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) => Update();
        foreach (TextBox box in new[] { bytesPerLine, variable, fileName, recordBytes, baseAddress, header, separator, prefix, suffix })
        {
            box.TextChanged += (_, _) => Update();
        }

        foreach (ComboBox combo in new[] { newLine, indent, elementSize, syntax, layout, jsonData })
        {
            combo.SelectionChanged += (_, _) => Update();
        }

        separateRanges.Checked += (_, _) => PrepareCopyAsRanges(editor, separate: true);
        separateRanges.Unchecked += (_, _) => PrepareCopyAsRanges(editor, separate: false);
        foreach (CheckBox check in new[] { upper, bigEndian, variant, wrap, delimiters, colors, decimalPosition, separateRanges })
        {
            check.Checked += (_, _) => Update();
            check.Unchecked += (_, _) => Update();
        }

        Fill();
        Update();
        ContentDialogResult result = await ShowEditDialogAsync(dialog);
        if (result == ContentDialogResult.None || !valid)
        {
            return;
        }

        AppState.SetString(CopyAsLastKey, JsonSerializer.Serialize(Read()));
        if (result == ContentDialogResult.Primary)
        {
            await CopyFormattedAsync(doc, current, options, start, length);
        }
        else
        {
            await SaveFormattedAsync(doc, current, options, start, length);
        }
    }

    private static bool IsArrayPadded(CopyFormat format, CopyOptions options, long length) =>
        CopyFormatter.IsArray(format) && format != CopyFormat.ArrayPython && options.ElementSize > 1 && length % options.ElementSize != 0;

    /// <summary>「ファイルに保存…」: 同じ形式でファイルに書き出す。出力はストリームで書き、大きさの上限を設けない (仕様 8 と「巨大ファイル」)。</summary>
    private async Task SaveFormattedAsync(DocumentViewModel doc, CopyFormat format, CopyOptions options, long start, long length)
    {
        string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + ExtensionOf(format);
        string? path;
        if (!TestHooks.TrySavePicker(suggested, out path))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.CopyAs" };
            picker.FileTypeChoices.Add(Loc.Get("CopyAs_Format_" + format), [ExtensionOf(format)]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        DocumentSnapshot snapshot = doc.Document.Current;
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Operation_Export", Path.GetFileName(path)), OperationKind.WritesExternal, null, length, op =>
            {
                string temp = path + ".tmp";
                try
                {
                    using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false), 1024 * 1024))
                    {
                        WriteCopyAs(format, options, snapshot, start, length, writer, op.CancellationToken, op.Report);
                    }

                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    File.Delete(temp);
                    throw;
                }

                return Task.CompletedTask;
            });
            ShowNotice(Loc.Format("CopyAs_Saved", Path.GetFileName(path)), InfoBarSeverity.Success, doc);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("CopyAs_SaveError", ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    private static string ExtensionOf(CopyFormat format) => format switch
    {
        CopyFormat.ArrayC => ".c",
        CopyFormat.ArrayCpp => ".cpp",
        CopyFormat.ArrayCSharp => ".cs",
        CopyFormat.ArrayJava => ".java",
        CopyFormat.ArrayJavaScript => ".js",
        CopyFormat.ArrayPython => ".py",
        CopyFormat.ArrayRust => ".rs",
        CopyFormat.ArrayGo => ".go",
        CopyFormat.ArrayPascal => ".pas",
        CopyFormat.ArrayVisualBasic => ".vb",
        CopyFormat.ArrayPureBasic => ".pb",
        CopyFormat.ArrayAssembly => ".asm",
        CopyFormat.Html => ".html",
        CopyFormat.Rtf => ".rtf",
        CopyFormat.Markdown => ".md",
        CopyFormat.Tex => ".tex",
        CopyFormat.IntelHex => ".hex",
        CopyFormat.SRecord => ".srec",
        CopyFormat.Json => ".json",
        CopyFormat.UUEncode => ".uue",
        CopyFormat.XXEncode => ".xxe",
        _ => ".txt",
    };

    /// <summary>決まった文字数で書くのをやめる (プレビュー)。</summary>
    private sealed class LimitedWriter(int limit) : StringWriter(CultureInfo.InvariantCulture)
    {
        public sealed class FullException : Exception;

        public override void Write(char value)
        {
            Check(1);
            base.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            int room = limit - GetStringBuilder().Length;
            if (value.Length > room)
            {
                base.Write(value[..Math.Max(0, room)]);
                throw new FullException();
            }

            base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void Write(ReadOnlySpan<char> buffer) => Write(new string(buffer));

        private void Check(int n)
        {
            if (GetStringBuilder().Length + n > limit)
            {
                throw new FullException();
            }
        }
    }

    // ---- 形式を選択して貼り付け (EDIT-26) ----

    private async void PasteSpecial_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc && EnsureEditable(doc))
        {
            await PasteSpecialAsync(doc, await _clipboard.ReadSpecialAsync());
        }
    }

    private sealed record PasteChoice(PasteFormat? Format, string? File, string Label, bool Enabled);

    private async Task PasteSpecialAsync(DocumentViewModel doc, SpecialClipboard clipboard)
    {
        EditorState editor = doc.Editor;
        string? text = clipboard.Text;
        if (text is not null && text.Length > PasteDetector.MaxTextChars)
        {
            // 解釈するテキストの上限 (512 MiB) を超える場合はダイアログを開かない (仕様 7)。
            ShowNotice(Loc.Get("PasteSpecial_TooLarge"), InfoBarSeverity.Error, doc);
            return;
        }

        if (text is null && clipboard.Binary is null && clipboard.Files.Count == 0)
        {
            ShowNotice(Loc.Get("PasteSpecial_Empty"), InfoBarSeverity.Informational, doc);
            return;
        }

        bool preferLast = App.Settings.GetBool(PreferLastPasteFormatKey, true);
        PasteFormat? last = Enum.TryParse(AppState.GetString(PasteSpecialLastKey, string.Empty), out PasteFormat f) ? f : null;

        var list = new ListView { Width = 300, Height = 300, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(list, "PasteSpecial_Formats");
        AutomationProperties.SetName(list, Loc.Get("PasteSpecial_FormatsName"));
        ComboBox elementSize = DialogParts.Combo("PasteSpecial_ElementSize", Loc.Get("CopyAs_ElementSize"), ["1", "2", "4", "8"], 0);
        CheckBox bigEndian = DialogParts.Check("PasteSpecial_BigEndian", Loc.Get("CopyAs_BigEndian"), false);
        ComboBox newLines = DialogParts.Combo("PasteSpecial_NewLines", Loc.Get("PasteSpecial_NewLines"),
            [Loc.Get("PasteSpecial_NewLinesKeep"), "CRLF", "LF"], 0);
        CheckBox appendNul = DialogParts.Check("PasteSpecial_AppendNul", Loc.Get("PasteSpecial_AppendNul"), false);
        RadioButton atCursor = DialogParts.Radio("PasteSpecial_AtCursor", Loc.Get("PasteSpecial_AtCursor"), "PasteSpecialAddress", true);
        RadioButton atAddress = DialogParts.Radio("PasteSpecial_AtAddress", Loc.Get("PasteSpecial_AtAddress"), "PasteSpecialAddress", false);
        TextBox gapFill = DialogParts.Field("PasteSpecial_GapFill", Loc.Get("PasteSpecial_GapFill"), "FF");
        bool canInsert = doc.Document.CanResize;
        RadioButton insert = DialogParts.Radio("PasteSpecial_Insert", Loc.Get("PasteSpecial_Insert"), "PasteSpecialMode", canInsert && editor.InsertMode);
        RadioButton overwrite = DialogParts.Radio("PasteSpecial_Overwrite", Loc.Get("PasteSpecial_Overwrite"), "PasteSpecialMode", !canInsert || !editor.InsertMode);
        insert.IsEnabled = canInsert;
        TextBlock source = DialogParts.Caption("PasteSpecial_Source", monospace: true);
        source.Text = text is null ? string.Empty : Loc.Format("PasteSpecial_SourceText", text.Length > 200 ? text[..200] + "…" : text);
        source.MaxHeight = 80;
        TextBlock preview = DialogParts.Caption("PasteSpecial_Preview", monospace: true);
        TextBlock lengthText = DialogParts.Caption("PasteSpecial_Length");

        var options = new StackPanel { Spacing = 6, Width = 280 };
        foreach (UIElement el in new UIElement[] { elementSize, bigEndian, newLines, appendNul, atCursor, atAddress, gapFill })
        {
            options.Children.Add(el);
        }

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        top.Children.Add(list);
        top.Children.Add(options);
        var body = new StackPanel { Spacing = 8 };
        foreach (UIElement el in new UIElement[] { top, source, preview, lengthText, insert, overwrite })
        {
            body.Children.Add(el);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "PasteSpecialDialog", Loc.Get("PasteSpecial_Title"), new ScrollViewer { Content = body },
            Loc.Get("PasteSpecial_Paste"));
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;

        var choices = new List<PasteChoice>();
        IReadOnlyList<PasteCandidate> candidates = [];
        PasteOptions Options() => new()
        {
            Encoding = editor.TextEncoding,
            NewLines = (NewLineConversion)Math.Max(0, newLines.SelectedIndex),
            AppendNul = appendNul.IsChecked == true,
            ElementSize = 1 << Math.Max(0, elementSize.SelectedIndex),
            BigEndian = bigEndian.IsChecked == true,
            Preferred = preferLast ? last : null,
            GapFill = HexText.TryParse(gapFill.Text) is [byte g] ? g : (byte)0xFF,
        };

        PasteChoice? Selected() => list.SelectedIndex >= 0 && list.SelectedIndex < choices.Count ? choices[list.SelectedIndex] : null;
        PasteCandidate? CandidateOf(PasteChoice? c) => c?.Format is { } fmt ? candidates.FirstOrDefault(x => x.Format == fmt) : null;

        bool updating = false;
        void Detect()
        {
            updating = true;
            PasteFormat? keep = Selected()?.Format;
            candidates = PasteDetector.Detect(text, Options(), clipboard.Binary);
            choices.Clear();
            list.Items.Clear();
            foreach (PasteCandidate c in candidates)
            {
                string name = Loc.Get("PasteSpecial_Format_" + c.Format);
                string label = c.IsValid
                    ? Loc.Format("PasteSpecial_Candidate", name, StatusFormat.Number(c.Length, Culture))
                    : Loc.Format("PasteSpecial_CandidateError", name, c.Error!.Line, c.Error.Column);
                choices.Add(new PasteChoice(c.Format, null, label, c.IsValid));
            }

            foreach (string file in clipboard.Files)
            {
                choices.Add(new PasteChoice(null, file, Loc.Format("PasteSpecial_FileContent", Path.GetFileName(file)), true));
            }

            foreach (PasteChoice c in choices)
            {
                // 解釈できなかった形式は理由を付けて示し、選べないようにする (仕様 4)。
                var item = new ListViewItem { Content = c.Label, IsEnabled = c.Enabled };
                list.Items.Add(item);
            }

            int index = keep is { } k ? choices.FindIndex(c => c.Format == k && c.Enabled) : -1;
            if (index < 0)
            {
                // 最も確からしい形式を選んだ状態にする (仕様 1)。ファイルがあればファイルの内容。
                index = clipboard.Files.Count > 0 && clipboard.Binary is null ? choices.FindIndex(c => c.File is not null) : choices.FindIndex(c => c.Enabled);
            }

            list.SelectedIndex = index;
            updating = false;
            Update();
        }

        void Update()
        {
            if (updating)
            {
                return;
            }

            PasteChoice? choice = Selected();
            PasteCandidate? candidate = CandidateOf(choice);
            bool addressed = candidate?.HasAddresses == true;
            atCursor.Visibility = atAddress.Visibility = gapFill.Visibility = addressed ? Visibility.Visible : Visibility.Collapsed;
            elementSize.Visibility = bigEndian.Visibility = choice?.Format == PasteFormat.Array ? Visibility.Visible : Visibility.Collapsed;
            newLines.Visibility = appendNul.Visibility = choice?.Format == PasteFormat.Text ? Visibility.Visible : Visibility.Collapsed;
            bool toAddress = addressed && atAddress.IsChecked == true;
            insert.IsEnabled = canInsert && !toAddress;
            if (toAddress)
            {
                overwrite.IsChecked = true;
            }

            if (choice?.File is { } file)
            {
                preview.Text = file;
                lengthText.Text = string.Empty;
            }
            else if (candidate is { IsValid: true })
            {
                // プレビュー: 結果のバイト列の先頭 256 バイトと長さ (仕様 4)。
                preview.Text = Loc.Format("Dialog_Preview", DialogParts.Hex(candidate.Bytes.AsSpan(0, (int)Math.Min(256, candidate.Length))));
                lengthText.Text = toAddress
                    ? Loc.Format("PasteSpecial_Records", candidate.Segments!.Count, StatusFormat.Number(candidate.Segments.Sum(s => (long)s.Data.Length), Culture))
                    : Loc.Format("PasteSpecial_LengthText", StatusFormat.Number(candidate.Length, Culture), StatusFormat.Hex(candidate.Length));
            }
            else
            {
                preview.Text = lengthText.Text = string.Empty;
            }

            dialog.IsPrimaryButtonEnabled = choice is { Enabled: true };
        }

        list.SelectionChanged += (_, _) => Update();
        foreach (ComboBox combo in new[] { elementSize, newLines })
        {
            combo.SelectionChanged += (_, _) => Detect();
        }

        foreach (CheckBox check in new[] { bigEndian, appendNul })
        {
            check.Checked += (_, _) => Detect();
            check.Unchecked += (_, _) => Detect();
        }

        gapFill.TextChanged += (_, _) => Detect();
        atCursor.Checked += (_, _) => Update();
        atAddress.Checked += (_, _) => Update();
        Detect();

        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || Selected() is not { Enabled: true } chosen)
        {
            return;
        }

        if (chosen.File is { } path)
        {
            await InsertFileAsync(doc, path);
            return;
        }

        PasteFormat format = chosen.Format!.Value;
        AppState.SetString(PasteSpecialLastKey, format.ToString());
        PasteCandidate? result = CandidateOf(chosen);
        if (format != PasteFormat.Binary && text is not null && text.Length > PasteDetector.DetectionChars)
        {
            // 判別は先頭 64 KiB で行ったため、選んだ形式で全体を変換する。4 MiB を超える場合は長時間処理 (「巨大ファイル」)。
            PasteOptions opts = Options();
            try
            {
                result = await Vm.Operations.RunAsync(Loc.Get("Operation_PasteConvert"), OperationKind.ReadOnly, doc.Document, text.Length,
                    _ => Task.FromResult(PasteDetector.Parse(format, text, opts)));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (result is not { IsValid: true })
        {
            if (result?.Error is { } error)
            {
                ShowNotice(Loc.Format("PasteSpecial_ConvertError", error.Line, error.Column), InfoBarSeverity.Error, doc);
            }

            return;
        }

        if (result.ChecksumErrors > 0 && !await ConfirmChecksumAsync(result.ChecksumErrors))
        {
            return;
        }

        if (result.HasAddresses && atAddress.IsChecked == true)
        {
            WriteRecordsAtAddresses(doc, result.Segments!);
        }
        else
        {
            await PasteBytesAsync(doc, result.Bytes!, insert.IsChecked == true);
        }

        FocusEditor();
    }

    /// <summary>「チェックサムが一致しない行が N 行あります」(EDIT-26 の「エラー」)。</summary>
    private async Task<bool> ConfirmChecksumAsync(int lines)
    {
        ContentDialog dialog = DialogParts.Dialog(Root, "PasteChecksumDialog", Loc.Get("PasteSpecial_ChecksumTitle"),
            new TextBlock { Text = Loc.Format("PasteSpecial_ChecksumBody", lines), TextWrapping = TextWrapping.Wrap }, Loc.Get("PasteSpecial_IgnoreChecksum"));
        return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 「レコードのアドレスに書く」: 各レコードを (アドレス − ベースアドレス) のオフセットに上書きする。データのない領域は書かない (仕様 5)。
    /// 1 つの編集グループにする。
    /// </summary>
    private void WriteRecordsAtAddresses(DocumentViewModel doc, IReadOnlyList<AddressedSegment> segments)
    {
        long baseAddress = doc.Document.Source.BaseAddress;
        if (segments.Any(s => s.Address - baseAddress < 0 || s.Address - baseAddress > doc.Document.Length
            || !doc.Document.CanResize && s.Address - baseAddress + s.Data.Length > doc.Document.Length))
        {
            ShowNotice(Loc.Get("PasteSpecial_AddressOutOfRange"), InfoBarSeverity.Error, doc);
            return;
        }

        ApplyEdit(doc, () =>
        {
            using (doc.Document.BeginGroup("レコードのアドレスに書く"))
            {
                foreach (AddressedSegment s in segments.OrderBy(s => s.Address))
                {
                    doc.Document.OverwriteContent(s.Address - baseAddress, EditContent.Bytes(s.Data), "上書き貼り付け");
                }
            }
        });
    }

    /// <summary>バイト列を挿入、または上書きで貼る。挿入では選択範囲を置き換える (EDIT-23)。固定長で末尾を越える場合は確かめる。</summary>
    private async Task PasteBytesAsync(DocumentViewModel doc, byte[] bytes, bool insert)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        EditorState editor = doc.Editor;
        long at = editor.HasSelection ? editor.SelectionStart : editor.Cursor;
        if (insert && doc.Document.CanResize)
        {
            long remove = editor.SelectionLength;
            ApplyEdit(doc, () =>
            {
                using (doc.Document.BeginGroup("貼り付け"))
                {
                    if (remove > 0)
                    {
                        doc.Document.Delete(at, remove, "削除");
                    }

                    EditCommands.Insert(editor, at, EditContent.Bytes(bytes), selectInserted: true, "貼り付け");
                }
            });
            return;
        }

        long overflow = doc.Document.CanResize ? 0 : Math.Max(0, bytes.Length - (doc.Document.Length - at));
        if (overflow > 0)
        {
            if (!await ConfirmTruncateAsync(overflow))
            {
                return;
            }

            bytes = bytes[..^(int)overflow];
        }

        ApplyEdit(doc, () => EditCommands.Overwrite(editor, at, EditContent.Bytes(bytes), "上書き貼り付け"));
    }
}
