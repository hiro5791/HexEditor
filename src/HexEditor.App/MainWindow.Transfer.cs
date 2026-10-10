using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Operations;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// インポート / エクスポート (TOOL-04〜TOOL-12) と、範囲の切り出しと保存 (TOOL-16)。変換は Core の <see cref="Importer"/>・<see cref="Exporter"/>
/// (Copy As と共通の部品) を使う。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>プレビューのためにファイル全体をデコードする大きさの上限 (これより大きいファイルは先頭だけでプレビューする)。</summary>
    private const long PreviewDecodeLimit = 16L * 1024 * 1024;

    private void RegisterTransferCommands()
    {
        Commands.Register("file.import", () => ShowImportAsync(null));
        Commands.Register("file.export", () => ShowExportAsync(selectionOnly: false), NeedsDocument);
        Commands.Register("file.exportSelection", () => ShowExportAsync(selectionOnly: true), NeedsSelection);
        Commands.Register("file.saveSelection", SaveSelectionAsync, NeedsSelection);
    }

    /// <summary>テスト用: 開いているインポート / エクスポートのダイアログ。</summary>
    internal TransferDialogState? TransferForTest { get; private set; }

    /// <summary>インポート / エクスポートのダイアログの状態 (テスト用の命令が読み書きする)。</summary>
    internal sealed class TransferDialogState
    {
        public required ContentDialog Dialog { get; init; }

        public required ComboBox Format { get; init; }

        public required TextBox Path { get; init; }

        public required TextBox Preview { get; init; }

        public required TextBlock Summary { get; init; }

        public required IReadOnlyList<string> Formats { get; init; }

        public Dictionary<string, Control> Fields { get; } = [];

        public Func<Task>? Refreshed { get; set; }

        public Task Pending { get; set; } = Task.CompletedTask;

        public RadioButton[] Targets { get; init; } = [];
    }

    // ---- 設定の欄 ----

    /// <summary>形式ごとの設定の欄を作り、値の変更を <paramref name="changed"/> で知らせる。</summary>
    private static StackPanel BuildFields(IReadOnlyList<TransferField> fields, Dictionary<string, string> values, Dictionary<string, Control> controls,
        string idPrefix, Action changed)
    {
        var panel = new StackPanel { Spacing = 6 };
        controls.Clear();
        foreach (TransferField field in fields)
        {
            string id = idPrefix + field.Key;
            string label = Loc.Get(field.LabelKey);
            Control control;
            switch (field.Kind)
            {
                case TransferFieldKind.Check:
                {
                    CheckBox box = DialogParts.Check(id, label, values[field.Key] == "true");
                    void Toggled(object sender, RoutedEventArgs e)
                    {
                        values[field.Key] = box.IsChecked == true ? "true" : "false";
                        changed();
                    }

                    box.Checked += Toggled;
                    box.Unchecked += Toggled;
                    control = box;
                    break;
                }

                case TransferFieldKind.Choice:
                {
                    IReadOnlyList<string> choices = field.Choices!;
                    ComboBox combo = DialogParts.Combo(id, label, choices.Select(c => Loc.Get("Transfer_Choice_" + c)),
                        Math.Max(0, choices.ToList().IndexOf(values[field.Key])));
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedIndex >= 0)
                        {
                            values[field.Key] = choices[combo.SelectedIndex];
                            changed();
                        }
                    };
                    control = combo;
                    break;
                }

                default:
                {
                    TextBox box = DialogParts.Field(id, label, values[field.Key]);
                    box.TextChanged += (_, _) =>
                    {
                        values[field.Key] = box.Text;
                        changed();
                    };
                    control = box;
                    break;
                }
            }

            controls[field.Key] = control;
            panel.Children.Add(control);
        }

        return panel;
    }

    private static Grid PathRow(TextBox path, Button browse)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(path);
        Grid.SetColumn(browse, 1);
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(browse);
        return row;
    }

    private static TextBox PreviewBox(string id) => new TextBox
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = DialogParts.Mono,
        Height = 160,
        FlowDirection = FlowDirection.LeftToRight,
        Header = Loc.Get("Transfer_Preview"),
    }.WithId(id);

    // ---- インポート (TOOL-04 の仕様 2) ----

    /// <summary>インポートのダイアログ。<paramref name="path"/> を渡すと、そのファイルを選んだ状態で開く。</summary>
    private async Task ShowImportAsync(string? path)
    {
        DocumentViewModel? target = Vm.Selected;
        IReadOnlyList<string> formats = FormatIds.Importable;
        TextBox pathBox = DialogParts.Field("Import_Path", Loc.Get("Import_File"), path ?? string.Empty, monospace: false);
        var browse = new Button { Content = Loc.Get("OpenAdv_Browse") }.WithId("Import_Browse");
        ComboBox format = DialogParts.Combo("Import_Format", Loc.Get("Transfer_Format"), formats.Select(FormatName), 0);
        var optionsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        RadioButton toNew = DialogParts.Radio("Import_ToNew", Loc.Get("Import_ToNew"), "ImportTarget", true);
        RadioButton toInsert = DialogParts.Radio("Import_ToInsert", Loc.Get("Import_ToInsert"), "ImportTarget", false);
        RadioButton toOverwrite = DialogParts.Radio("Import_ToOverwrite", Loc.Get("Import_ToOverwrite"), "ImportTarget", false);
        bool editable = target is not null && !target.Editor.ReadOnly;
        toInsert.IsEnabled = editable && target!.Document.CanResize;
        toOverwrite.IsEnabled = editable;
        TextBox preview = PreviewBox("Import_Preview");
        TextBlock summary = DialogParts.Caption("Import_Summary");
        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement e in new UIElement[]
        {
            PathRow(pathBox, browse), format, optionsHost,
            new TextBlock { Text = Loc.Get("Import_Target") }, toNew, toInsert, toOverwrite, preview, summary,
        })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ImportDialog", Loc.Get("Import_Title"),
            new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Import_Run"));
        var state = new TransferDialogState { Dialog = dialog, Format = format, Path = pathBox, Preview = preview, Summary = summary, Formats = formats,
            Targets = [toNew, toInsert, toOverwrite] };
        TransferForTest = state;

        Dictionary<string, string> values = [];
        string current = formats[0];
        ImportResult? previewResult = null;
        int version = 0;
        void Rebuild()
        {
            current = formats[Math.Max(0, format.SelectedIndex)];
            IReadOnlyList<TransferField> fields = TransferOptions.ImportFields(current);
            values = TransferOptions.Load("import.options." + current, fields);
            optionsHost.Content = BuildFields(fields, values, state.Fields, "Import_Opt_", () => state.Pending = RefreshPreviewAsync());
            bool ips = current is FormatIds.Ips or FormatIds.Ips32;
            toNew.IsEnabled = !ips;
            if (ips)
            {
                toOverwrite.IsChecked = editable;
            }
        }

        async Task RefreshPreviewAsync()
        {
            int mine = ++version;
            previewResult?.Dispose();
            previewResult = null;
            string file = pathBox.Text;
            bool exists = File.Exists(file);
            dialog.IsPrimaryButtonEnabled = exists && (current is not (FormatIds.Ips or FormatIds.Ips32) || editable);
            DialogParts.MarkInvalid(pathBox, file.Length > 0 && !exists);
            if (!exists)
            {
                preview.Text = string.Empty;
                summary.Text = string.Empty;
                return;
            }

            if (current is FormatIds.Ips or FormatIds.Ips32)
            {
                try
                {
                    IpsPatchData patch = await Task.Run(() => { using FileStream s = File.OpenRead(file); return IpsPatch.Read(s); });
                    summary.Text = Loc.Format("Import_IpsSummary", patch.Records.Count, patch.Ips32 ? "IPS32" : "IPS");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    summary.Text = ex.Message;
                }

                preview.Text = string.Empty;
                return;
            }

            ImportOptions options = TransferOptions.ToImportOptions(current, values);
            string temp = Vm.DocumentOptions.TempDirectory;
            try
            {
                long size = new FileInfo(file).Length;
                ImportResult result = await Task.Run(() =>
                {
                    if (size <= PreviewDecodeLimit)
                    {
                        return Importer.DecodeFile(file, options, temp);
                    }

                    // 大きいファイルは先頭だけをデコードしてプレビューする。
                    byte[] head = new byte[64 * 1024];
                    using (FileStream s = File.OpenRead(file))
                    {
                        Array.Resize(ref head, s.ReadAtLeast(head, head.Length, false));
                    }

                    using var ms = new MemoryStream(head);
                    return Importer.Decode(ms, options, temp, Path.GetFileName(file));
                });
                if (mine != version)
                {
                    result.Dispose();
                    return;
                }

                previewResult = result;
                preview.Text = Core.Clipboard.HexText.Format(result.Preview(256));
                summary.Text = ImportSummary(result, size > PreviewDecodeLimit);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                summary.Text = ex.Message;
            }
        }

        async Task Detect()
        {
            if (File.Exists(pathBox.Text))
            {
                string file = pathBox.Text;
                string detected = await Task.Run(() => Importer.DetectFile(file));
                int index = formats.ToList().IndexOf(detected);
                if (index >= 0 && format.SelectedIndex != index)
                {
                    format.SelectedIndex = index; // SelectionChanged でプレビューを作り直す
                    return;
                }
            }

            await RefreshPreviewAsync();
        }

        format.SelectionChanged += (_, _) =>
        {
            Rebuild();
            state.Pending = RefreshPreviewAsync();
        };
        state.Refreshed = () => state.Pending = Detect();
        pathBox.TextChanged += (_, _) => state.Pending = Detect();
        browse.Click += async (_, _) =>
        {
            if (await PickOneFileAsync("HexEditor.Import") is { } chosen)
            {
                pathBox.Text = chosen;
            }
        };
        Rebuild();
        state.Pending = Detect();
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowQueuedAsync();
        }
        finally
        {
            if (TransferForTest == state)
            {
                TransferForTest = null;
            }
        }

        previewResult?.Dispose();
        if (answer != ContentDialogResult.Primary)
        {
            return;
        }

        TransferOptions.Save("import.options." + current, values);
        AppState.SetString("import.format", current);
        string chosenFile = Path.GetFullPath(pathBox.Text);
        ImportTarget destination = toInsert.IsChecked == true ? ImportTarget.Insert : toOverwrite.IsChecked == true ? ImportTarget.Overwrite : ImportTarget.New;
        await RunImportAsync(chosenFile, TransferOptions.ToImportOptions(current, values), destination, target);
    }

    private enum ImportTarget
    {
        New,
        Insert,
        Overwrite,
    }

    /// <summary>プレビューの要約: 変換後のサイズ・アドレスの範囲・ギャップ・警告と誤りの件数 (TOOL-04 の仕様 2 の 5、TOOL-05 の画面)。</summary>
    private static string ImportSummary(ImportResult result, bool partial)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        var parts = new List<string> { Loc.Format("Import_Size", StatusFormat.Number(result.Length, c)) };
        if (result.AddressRange is { } r)
        {
            parts.Add(Loc.Format("Import_AddressRange", $"0x{r.First:X8}", $"0x{r.Last:X8}", StatusFormat.Number(result.DataBytes, c),
                StatusFormat.Number(result.GapBytes, c)));
        }

        if (result.StartAddress is { } start)
        {
            parts.Add(Loc.Format("Import_StartAddress", $"0x{start:X}"));
        }

        if (result.Header is { Length: > 0 } header)
        {
            parts.Add(Loc.Format("Import_Header", header));
        }

        parts.Add(Loc.Format("Import_IssueCounts", result.Issues.WarningCount, result.Issues.ErrorCount));
        foreach (ImportIssue issue in result.Issues.Items.Take(5))
        {
            parts.Add(IssueText(issue));
        }

        if (partial)
        {
            parts.Add(Loc.Get("Import_PartialPreview"));
        }

        return string.Join(Environment.NewLine, parts);
    }

    /// <summary>誤り 1 件の説明 (行・列・理由・期待値と実際の値)。</summary>
    internal static string IssueText(ImportIssue issue)
    {
        string reason = Loc.Get("ImportIssue_" + issue.Kind);
        if (issue.Expected is not null || issue.Actual is not null)
        {
            reason += " " + Loc.Format("ImportIssue_Values", issue.Expected ?? "-", issue.Actual ?? "-");
        }

        return Loc.Format("ImportIssue_Line", issue.Line, issue.Column, reason);
    }

    /// <summary>
    /// インポートを実行する (TOOL-04 の仕様 2 の 6)。誤りがあれば一覧を示して「誤りを無視して読み込む」「キャンセル」(既定はキャンセル) を選ばせる。
    /// カーソル位置への挿入・上書きは 1 回の Undo で戻せる。
    /// </summary>
    private async Task RunImportAsync(string path, ImportOptions options, ImportTarget destination, DocumentViewModel? target)
    {
        if (options.Format is FormatIds.Ips or FormatIds.Ips32)
        {
            await ApplyPatchAsync(path, target);
            return;
        }

        long size = new FileInfo(path).Length;
        string temp = Vm.DocumentOptions.TempDirectory;
        ImportResult result;
        try
        {
            result = await Vm.Operations.RunAsync(Loc.Format("Import_Operation", Path.GetFileName(path)), OperationKind.ReadOnly, null, size,
                op => Task.FromResult(Importer.DecodeFile(path, options, temp, new ImportProgress(op.CancellationToken, op.Report))));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_Open", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
            return;
        }

        using (result)
        {
            if (result.HasErrors && !await ConfirmImportErrorsAsync(result))
            {
                return;
            }

            SparseImage image = result.TakeImage();
            string name = Path.GetFileName(path);
            switch (destination)
            {
                case ImportTarget.New:
                {
                    DocumentViewModel vm = Vm.AddImported(image, name);
                    ViewOptions.Attach(App.Settings, vm);
                    if (image.Origin != 0)
                    {
                        vm.Editor.ApplyView(vm.Editor.View with { BaseAddress = (ulong)image.Origin });
                    }

                    if (options.GapBookmarks)
                    {
                        foreach ((long offset, long length) in image.GapsIn(0, image.Length).Take(10_000))
                        {
                            AnnotationsFor(vm).Bookmarks.Add(offset, length, Loc.Format("Import_GapBookmark", $"0x{offset + image.Origin:X}"));
                        }
                    }

                    break;
                }

                default:
                {
                    if (target is null || target.Document.IsDisposed)
                    {
                        image.Dispose();
                        return;
                    }

                    EditorState editor = target.Editor;
                    long at = editor.Cursor;
                    using EditContent content = EditContent.FromSource(image, 0, image.Length, owns: true);
                    try
                    {
                        using (target.Document.BeginGroup(Loc.Get("Import_Title")))
                        {
                            if (destination == ImportTarget.Insert)
                            {
                                target.Document.InsertContent(at, content, Loc.Get("Import_Title"));
                            }
                            else
                            {
                                target.Document.OverwriteContent(at, content, Loc.Get("Import_Title"));
                            }
                        }
                    }
                    catch (FixedLengthException)
                    {
                        ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, target);
                    }

                    break;
                }
            }
        }

        UpdateTitle();
    }

    /// <summary>誤りの一覧 (最大 1,000 件) と「誤りを無視して読み込む」「キャンセル」(既定: キャンセル)。TOOL-05 の仕様 2。</summary>
    private async Task<bool> ConfirmImportErrorsAsync(ImportResult result)
    {
        var list = new ListView { MaxHeight = 280, SelectionMode = ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(list, "ImportErrors_List");
        foreach (ImportIssue issue in result.Issues.Items)
        {
            list.Items.Add(new TextBlock
            {
                Text = IssueText(issue) + "  " + issue.Content,
                FontFamily = DialogParts.Mono,
                TextWrapping = TextWrapping.NoWrap,
                FlowDirection = FlowDirection.LeftToRight,
            });
        }

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.Format("ImportErrors_Body", result.Issues.ErrorCount)
                + (result.Issues.Count > ImportIssueList.MaxListed ? " " + Loc.Format("ImportErrors_More", result.Issues.Count) : string.Empty),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(list);
        ContentDialog dialog = DialogParts.Dialog(Root, "ImportErrorsDialog", Loc.Get("ImportErrors_Title"), body, Loc.Get("ImportErrors_Ignore"));
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>IPS パッチを今のドキュメントに当てる (TOOL-12 の仕様 4。長さ固定のドキュメントでは長さが変わるパッチを当てない)。</summary>
    private async Task ApplyPatchAsync(string path, DocumentViewModel? target)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            IpsPatchData patch = await Task.Run(() => { using FileStream s = File.OpenRead(path); return IpsPatch.Read(s); });
            IpsPatch.ApplyTo(target.Document, patch, Loc.Get("Import_ApplyPatch"));
        }
        catch (IpsFormatException ex)
        {
            ShowNotice(Loc.Format("Import_BrokenPatch", ex.Record, $"0x{ex.FileOffset:X}"), InfoBarSeverity.Error, target);
        }
        catch (FixedLengthException)
        {
            ShowNotice(Loc.Get("Import_PatchChangesLength"), InfoBarSeverity.Error, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_Open", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error, target);
        }
    }

    // ---- エクスポート (TOOL-04 の仕様 3) ----

    /// <summary>出力の元のデータ (ギャップを除く範囲を含む)。</summary>
    private static ExportSource SourceOf(DocumentViewModel doc)
    {
        DocumentSnapshot snapshot = doc.Document.Current;
        return new ExportSource
        {
            Read = (offset, destination) => snapshot.Read(offset, destination),
            Length = snapshot.Length,
            BaseAddress = (long)doc.Editor.View.BaseAddress,
            DataRanges = snapshot.DataRanges,
            FileName = doc.DisplayName,
        };
    }

    /// <summary>ダンプの既定: 画面の文字コード、変更バイト・ブックマークの強調、タイトル (TOOL-10 の仕様 1・2)。</summary>
    private DumpOptions DumpDefaults(DocumentViewModel doc)
    {
        var highlights = new List<DumpHighlight>();
        DocumentSnapshot snapshot = doc.Document.Current;
        foreach ((long offset, long length) in snapshot.EnumerateModifiedRanges().Take(100_000))
        {
            highlights.Add(new DumpHighlight(offset, length, DumpHighlightKind.Modified));
        }

        foreach (Core.Bookmarks.Bookmark b in AnnotationsFor(doc).Bookmarks.All.Take(100_000))
        {
            if (b.Length > 0)
            {
                highlights.Add(new DumpHighlight(b.Start, b.Length, DumpHighlightKind.Bookmark, b.Name));
            }
        }

        highlights.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return new DumpOptions
        {
            Encoding = doc.Editor.TextEncoding,
            BaseAddress = (long)doc.Editor.View.BaseAddress,
            Highlights = highlights,
            Title = doc.DisplayName,
        };
    }

    /// <summary>
    /// エクスポートのダイアログ: 対象 (ドキュメント全体 / 選択範囲 / オフセットの範囲)、形式と形式ごとの設定、出力の先頭 20 行のプレビューと
    /// おおよそのサイズ、保存先。
    /// </summary>
    private async Task ShowExportAsync(bool selectionOnly)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        IReadOnlyList<string> formats = FormatIds.Exportable;
        string last = AppState.GetString("export.format", FormatIds.IntelHex);
        ComboBox format = DialogParts.Combo("Export_Format", Loc.Get("Transfer_Format"), formats.Select(FormatName),
            Math.Max(0, formats.ToList().IndexOf(last)));
        RadioButton whole = DialogParts.Radio("Export_Whole", Loc.Get("Export_Whole"), "ExportTarget", !editor.HasSelection);
        RadioButton selection = DialogParts.Radio("Export_Selection", Loc.Get("Export_Selection"), "ExportTarget", editor.HasSelection);
        RadioButton range = DialogParts.Radio("Export_Range", Loc.Get("Export_Range"), "ExportTarget", false);
        selection.IsEnabled = editor.HasSelection;
        TextBox rangeStart = DialogParts.Field("Export_RangeStart", Loc.Get("OpenAdv_Start"), "0");
        TextBox rangeLength = DialogParts.Field("Export_RangeLength", Loc.Get("OpenAdv_Length"), "end");
        var optionsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        TextBox pathBox = DialogParts.Field("Export_Path", Loc.Get("Export_File"), string.Empty, monospace: false);
        var browse = new Button { Content = Loc.Get("OpenAdv_Browse") }.WithId("Export_Browse");
        TextBox preview = PreviewBox("Export_Preview");
        TextBlock summary = DialogParts.Caption("Export_Summary");
        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement e in new UIElement[]
        {
            new TextBlock { Text = Loc.Get("Export_Target") }, whole, selection, range, rangeStart, rangeLength, format, optionsHost,
            PathRow(pathBox, browse), preview, summary,
        })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ExportDialog", Loc.Get(selectionOnly ? "Export_SelectionTitle" : "Export_Title"),
            new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Export_Run"));
        var state = new TransferDialogState { Dialog = dialog, Format = format, Path = pathBox, Preview = preview, Summary = summary, Formats = formats,
            Targets = [whole, selection, range] };
        TransferForTest = state;
        var context = new EditorExpressionContext(editor);
        long? Evaluate(string text) => DialogParts.TryEvaluate(text, context, out long v, out _) && v >= 0 ? v : null;

        Dictionary<string, string> values = [];
        string current = formats[0];
        DumpOptions dumpDefaults = DumpDefaults(doc);
        IReadOnlyList<(long Offset, long Length)> Ranges()
        {
            if (selection.IsChecked == true && editor.HasSelection)
            {
                return [(editor.SelectionStart, editor.SelectionLength)];
            }

            if (range.IsChecked == true && Evaluate(rangeStart.Text) is { } s && Evaluate(rangeLength.Text) is { } l)
            {
                long start = Math.Min(s, doc.Document.Length);
                return [(start, Math.Max(0, Math.Min(l, doc.Document.Length - start)))];
            }

            return [(0, doc.Document.Length)];
        }

        ExportOptions Options() => TransferOptions.ToExportOptions(current, values, Evaluate, dumpDefaults,
            doc.Encoded?.StartAddress, doc.Encoded?.Header);

        void Rebuild()
        {
            current = formats[Math.Max(0, format.SelectedIndex)];
            IReadOnlyList<TransferField> fields = TransferOptions.ExportFields(current, editor.View, doc.DisplayName);
            values = TransferOptions.Load("export.options." + current, fields);
            optionsHost.Content = BuildFields(fields, values, state.Fields, "Export_Opt_", Refresh);
            if (pathBox.Text.Length > 0)
            {
                pathBox.Text = Path.ChangeExtension(pathBox.Text, FormatIds.Extension(current));
            }

            Refresh();
        }

        void Refresh()
        {
            bool rangeOk = range.IsChecked != true || Evaluate(rangeStart.Text) is not null && Evaluate(rangeLength.Text) is not null;
            rangeStart.Visibility = rangeLength.Visibility = range.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            DialogParts.MarkInvalid(rangeStart, !rangeOk);
            IReadOnlyList<string> bad = TransferOptions.Validate(current, values, Evaluate);
            foreach ((string key, Control control) in state.Fields)
            {
                DialogParts.MarkInvalid(control, bad.Contains(key));
            }

            string? error = null;
            ExportSource source = SourceOf(doc);
            IReadOnlyList<(long Offset, long Length)> ranges = Ranges();
            ExportOptions options = Options();
            if (bad.Count == 0 && rangeOk)
            {
                try
                {
                    preview.Text = current is FormatIds.Ips or FormatIds.Ips32 or FormatIds.Binary ? string.Empty
                        : Exporter.Preview(source, ranges, options);
                    long estimate = Exporter.EstimateSize(source, ranges, options);
                    summary.Text = Loc.Format("Export_EstimatedSize", Size(estimate));
                }
                catch (FormatLimitException ex)
                {
                    error = Loc.Format("Encoded_TooLarge", ex.Format, $"0x{ex.Limit:X}");
                }
            }

            if (error is not null)
            {
                summary.Text = error;
                preview.Text = string.Empty;
            }

            dialog.IsPrimaryButtonEnabled = bad.Count == 0 && rangeOk && error is null && pathBox.Text.Trim().Length > 0;
        }

        format.SelectionChanged += (_, _) => Rebuild();
        state.Refreshed = () =>
        {
            Refresh();
            return Task.CompletedTask;
        };
        foreach (RadioButton r in new[] { whole, selection, range })
        {
            r.Checked += (_, _) => Refresh();
        }

        rangeStart.TextChanged += (_, _) => Refresh();
        rangeLength.TextChanged += (_, _) => Refresh();
        pathBox.TextChanged += (_, _) => Refresh();
        browse.Click += async (_, _) =>
        {
            string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + FormatIds.Extension(current);
            if (TestHooks.TrySavePicker(suggested, out string? chosen))
            {
                pathBox.Text = chosen ?? pathBox.Text;
                return;
            }

            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.Export" };
            picker.FileTypeChoices.Add(FormatName(current), [FormatIds.Extension(current)]);
            if ((await picker.PickSaveFileAsync())?.Path is { } p)
            {
                pathBox.Text = p;
            }
        };
        if (doc.FilePath is { } own)
        {
            pathBox.Text = Path.ChangeExtension(own, FormatIds.Extension(formats[Math.Max(0, format.SelectedIndex)]));
        }

        Rebuild();
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowQueuedAsync();
        }
        finally
        {
            if (TransferForTest == state)
            {
                TransferForTest = null;
            }
        }

        if (answer != ContentDialogResult.Primary)
        {
            return;
        }

        TransferOptions.Save("export.options." + current, values);
        AppState.SetString("export.format", current);
        await RunExportAsync(doc, Ranges(), Options(), Path.GetFullPath(pathBox.Text));
    }

    /// <summary>
    /// 書き出す (TOOL-04 の「巨大ファイル」): 一時ファイルに書いてから置き換え、キャンセル・失敗したら一時ファイルを消す。テキスト形式で 1 GB を超える
    /// 見込み、HTML・RTF・ソースコードで 100 MB を超える見込みの場合は先に確かめる。
    /// </summary>
    private async Task<bool> RunExportAsync(DocumentViewModel doc, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions options, string path)
    {
        ExportSource source = SourceOf(doc);
        long estimate = Exporter.EstimateSize(source, ranges, options);
        string? warning = options.Format switch
        {
            FormatIds.Html or FormatIds.Rtf when estimate > Exporter.DocumentConfirmBytes => "Export_LargeDocument",
            _ when FormatIds.SourceArrays.Contains(options.Format) && estimate > Exporter.SourceConfirmBytes => "Export_LargeSource",
            _ when FormatIds.IsText(options.Format) && estimate > Exporter.ConfirmBytes => "Export_LargeText",
            _ => null,
        };
        if (warning is not null)
        {
            ContentDialog confirm = DialogParts.Dialog(Root, "ExportLargeDialog", Loc.Get("Export_LargeTitle"),
                new TextBlock { Text = Loc.Format(warning, Size(estimate)), TextWrapping = TextWrapping.Wrap }, Loc.Get("Export_Run"));
            if (await confirm.ShowQueuedAsync() != ContentDialogResult.Primary)
            {
                return false;
            }
        }

        long total = ranges.Sum(r => r.Length);
        DocumentSnapshot snapshot = doc.Document.Current;
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Export_Operation", Path.GetFileName(path)), OperationKind.WritesExternal, null, total, op =>
            {
                Exporter.WriteFile(path, stream =>
                {
                    if (options.Format is FormatIds.Ips or FormatIds.Ips32)
                    {
                        WritePatch(snapshot, options, stream, op);
                    }
                    else
                    {
                        Exporter.Write(source, ranges, options, stream, op.CancellationToken, op.Report);
                    }
                });
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IpsLimitException ex)
        {
            ShowNotice(Loc.Format("Export_IpsLimit", $"0x{ex.Offset:X}", ex.Ips32 ? "IPS32" : "IPS"), InfoBarSeverity.Error, doc);
            return false;
        }
        catch (FormatLimitException ex)
        {
            ShowNotice(Loc.Format("Encoded_TooLarge", ex.Format, $"0x{ex.Limit:X}"), InfoBarSeverity.Error, doc);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Export_Failed", ex.Message), InfoBarSeverity.Error, doc);
            return false;
        }

        ShowStatusMessage(Loc.Format("Export_Done", Path.GetFileName(path)));
        return true;
    }

    /// <summary>IPS パッチを書く: 差分の元は保存されている内容 (TOOL-12 の仕様 5、EDIT-42 の既定)。</summary>
    private static void WritePatch(DocumentSnapshot snapshot, ExportOptions options, Stream stream, LongRunningOperation op)
    {
        Core.Sources.IByteSource original = snapshot.OriginalSource;
        Core.Clipboard.ByteReader readOriginal = (o, d) => original.Read(o, d);
        List<(long, long)> changes = IpsPatch.ChangedRanges(snapshot, readOriginal, original.Length, op.CancellationToken);
        IpsFormat format = options.Format == FormatIds.Ips32 && options.Ips == IpsFormat.Auto ? IpsFormat.Ips32 : options.Ips;
        IpsPatch.Write(stream, changes, (o, d) => snapshot.Read(o, d), snapshot.Length, original.Length, format, options.IpsRle);
    }

    // ---- 範囲の切り出しと保存 (TOOL-16) ----

    /// <summary>
    /// 選択範囲をそのままのバイト列で別のファイルに保存する (エクスポートの形式「バイナリ」と同じ処理。仕様 4)。保存先のファイルがあれば上書きの確認は
    /// 保存ダイアログが出す (仕様 2)。一時ファイルに書いてから置き換える (仕様 3)。
    /// </summary>
    private async Task SaveSelectionAsync()
    {
        if (Vm.Selected is not { } doc || !doc.Editor.HasSelection)
        {
            return;
        }

        string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName)
            + $"_{doc.Editor.SelectionStart:X}-{doc.Editor.SelectionStart + doc.Editor.SelectionLength - 1:X}.bin";
        string? path;
        if (!TestHooks.TrySavePicker(suggested, out path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.SaveSelection" };
            picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [".bin"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        await RunExportAsync(doc, [(doc.Editor.SelectionStart, doc.Editor.SelectionLength)], new ExportOptions { Format = FormatIds.Binary },
            Path.GetFullPath(path));
    }
}

/// <summary>コードで作る部品に AutomationId を付ける補助。</summary>
internal static class ElementIds
{
    public static T WithId<T>(this T element, string id)
        where T : FrameworkElement
    {
        AutomationProperties.SetAutomationId(element, id);
        return element;
    }
}
