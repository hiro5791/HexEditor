using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Coloring;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Printing;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 印刷 (TOOL-01)、印刷プレビュー (TOOL-02)、PDF への出力 (TOOL-03)。印刷ダイアログは左に設定、右にプレビューを置く。
/// 印刷は Windows の印刷 API (<see cref="GdiPrinter"/>)、PDF は自前の書き出し (<see cref="PdfExporter"/>) で、どちらも印刷の計画 (<see cref="PrintPlan"/>) の
/// ページを 1 つずつ作る。印刷の処理は長時間処理として処理センターに出し、印刷を始めた時点のスナップショットを使う (印刷中も編集できる)。
/// </summary>
public sealed partial class MainWindow
{
    private const string PrintDefaultsKey = "print.defaults";

    private enum PrintChoice
    {
        Cancel,
        Print,
        Pdf,
    }

    // 開いている印刷ダイアログ (テスト用の命令が操作する)。
    private PrintDialogContent? _printContent;
    private PrintPreview? _printPreview;
    private PrintPlan? _printPlan;
    private double _printRefreshMs;
    private string? _printProblem;

    private void RegisterPrintCommands()
    {
        Commands.Register("file.print", () => ShowPrintAsync(PrintDialogMode.Print, focusPreview: false), () => NeedsDocument());
        Commands.Register("file.printPreview", () => ShowPrintAsync(PrintDialogMode.Print, focusPreview: true), () => NeedsDocument());
        Commands.Register("file.exportPdf", () => ShowPrintAsync(PrintDialogMode.Pdf, focusPreview: false), () => NeedsDocument());
    }

    private static PrintSettings LoadPrintDefaults(PrintDialogMode mode)
    {
        PrintSettings settings = new PrintSettings { Paper = PaperSizes.DefaultFor(CultureInfo.CurrentCulture) };
        try
        {
            if (JsonNode.Parse(AppState.GetString(PrintDefaultsKey, "{}")) is JsonObject json)
            {
                settings = PrintSettings.FromJson(json, settings);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (mode == PrintDialogMode.Pdf && !PaperSizes.PdfKinds.Contains(settings.Paper))
        {
            settings = settings with { Paper = PaperSizes.DefaultFor(CultureInfo.CurrentCulture) };
        }

        return settings;
    }

    private static PrintColor LightBookmarkColor(BookmarkColor color)
    {
        if (color.IsCustom)
        {
            // 任意の色は、淡くして文字が読めるようにする (白と混ぜる)。
            byte Mix(uint c) => (byte)(c + (255 - c) * 6 / 10);
            return new PrintColor(Mix((color.Rgb >> 16) & 0xFF), Mix((color.Rgb >> 8) & 0xFF), Mix(color.Rgb & 0xFF));
        }

        foreach (ResourceDictionary dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.ThemeDictionaries.TryGetValue("Light", out object? found) && found is ResourceDictionary themed
                && themed.TryGetValue($"BookmarkBackground{color.PaletteIndex}Brush", out object? value) && value is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
                return new PrintColor(brush.Color.R, brush.Color.G, brush.Color.B);
            }
        }

        return PrintColor.Shade;
    }

    /// <summary>
    /// 印刷のデータ: 今の内容のスナップショットと、ブックマーク・色付けルールの背景色 (印刷を始めた時点の値を先に集めておく。
    /// 印刷の処理はバックグラウンドで動くため、画面側の状態を後から読まない)。
    /// </summary>
    private IPrintData MakePrintData(DocumentViewModel doc)
    {
        DocumentSnapshot snapshot = doc.Document.Current;
        var marks = new List<(long Start, long Length, PrintColor Color)>();
        if (BookmarkCollection.For(doc.Document) is { } bookmarks)
        {
            foreach ((Bookmark bookmark, long start) in bookmarks.AllWithStart)
            {
                marks.Add((start, Math.Max(1, bookmark.Length), LightBookmarkColor(bookmark.Color)));
            }
        }

        ColoringRuleSet? rules = _annotations.TryGetValue(doc, out DocumentAnnotations? a) && !a.Coloring.Rules.IsEmpty ? a.Coloring.Rules : null;
        IEnumerable<(long, long, PrintColor)> Highlights(long offset, long length)
        {
            foreach ((long start, long len, PrintColor color) in marks)
            {
                if (start < offset + length && start + len > offset)
                {
                    yield return (start, len, color);
                }
            }

            if (rules is not null && length > 0 && length <= int.MaxValue)
            {
                var hex = new ColoringCell[length];
                var text = new ColoringCell[length];
                rules.EvaluateRange(snapshot, offset, hex, text);
                for (int i = 0; i < hex.Length; i++)
                {
                    int bg = hex[i].Background;
                    if (bg >= 0 && rules.Rules[bg].Rule.Background is { } rgb)
                    {
                        yield return (offset + i, 1, new PrintColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
                    }
                }
            }
        }

        return new SnapshotPrintData(snapshot, doc.DisplayName, doc.FilePath, doc.Document.IsModified, Highlights);
    }

    private PrintFields MakePrintFields(DocumentViewModel doc, IPrintData data) => new(
        doc.DisplayName,
        doc.FilePath,
        data.Length,
        data.IsModified,
        Loc.Get("Print_Modified"),
        (TestHooks.Time ?? TimeProvider.System).GetLocalNow(),
        CultureInfo.CurrentCulture);

    private static SystemFontMetrics? PrintFonts(string family) =>
        SystemFontCatalog.Instance.Primary(family) is { } face ? new SystemFontMetrics(face, SystemFontCatalog.Instance) : null;

    /// <summary>印刷ダイアログ (印刷の設定とプレビュー) を出し、印刷・PDF への出力まで進める。</summary>
    internal async Task ShowPrintAsync(PrintDialogMode mode, bool focusPreview)
    {
        if (Vm.Selected is not { } doc || doc.IsPending || doc.IsMissing)
        {
            return;
        }

        PrintSettings settings = LoadPrintDefaults(mode);
        bool firstTime = true;
        while (true)
        {
            (PrintChoice choice, PrintSettings? chosen) = await ShowPrintDialogOnceAsync(doc, mode, settings, focusPreview, firstTime);
            firstTime = false;
            if (choice == PrintChoice.Cancel || chosen is null)
            {
                return;
            }

            settings = chosen;

            // 印刷を始めた時点の内容 (スナップショット) を使う。以後の編集は印刷結果に影響しない (TOOL-01 の仕様 6)。
            IPrintData data = MakePrintData(doc);
            PrintFields fields = MakePrintFields(doc, data);
            if (PrintFonts(settings.FontFamily) is not { } fonts)
            {
                ShowNotice(Loc.Get("Print_Error_NoFont"), InfoBarSeverity.Error);
                return;
            }

            PrintPlan plan = PrintPlan.Create(settings, data.Length, fonts);
            if (choice == PrintChoice.Pdf)
            {
                await ExportPdfAsync(doc, plan, data, fields, fonts);
                return;
            }

            if (plan.NeedsConfirmation && !await ConfirmManyPagesAsync(plan.PrintedPages))
            {
                // 「範囲を変える」: 印刷を始めずに、印刷ダイアログに戻る。
                focusPreview = false;
                continue;
            }

            await PrintAsync(doc, plan, data, fields, fonts, settings);
            return;
        }
    }

    private async Task<bool> ConfirmManyPagesAsync(long pages)
    {
        var text = new TextBlock { Text = Loc.Format("Print_Confirm_Body", pages), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        AutomationProperties.SetAutomationId(text, "Print_ConfirmBody");
        ContentDialog dialog = DialogParts.Dialog(Root, "PrintConfirmDialog", Loc.Get("Print_Confirm_Title"), text, Loc.Get("Print_Confirm_Run"));
        dialog.CloseButtonText = Loc.Get("Print_Confirm_Change");
        return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task<(PrintChoice, PrintSettings?)> ShowPrintDialogOnceAsync(DocumentViewModel doc, PrintDialogMode mode, PrintSettings settings, bool focusPreview, bool firstTime)
    {
        IPrintData data = MakePrintData(doc);
        PrintFields fields = MakePrintFields(doc, data);
        var selection = doc.Editor.SelectedRanges.Select(r => (r.Start, r.Length)).ToList();
        IReadOnlyList<string> printers = mode == PrintDialogMode.Print ? GdiPrinter.Names() : [];
        var preview = new PrintPreview();
        var content = new PrintDialogContent(mode, settings with { Range = firstTime && selection.Count > 0 ? PrintRangeKind.Selection : settings.Range },
            doc.Editor.View, selection, data.Length, printers, new EditorExpressionContext(doc.Editor), preview);

        string primary = mode == PrintDialogMode.Print ? Loc.Get("Print_Run") : Loc.Get("Print_SavePdf");
        ContentDialog dialog = DialogParts.Dialog(Root, "PrintDialog", Loc.Get(mode == PrintDialogMode.Print ? "Print_Title" : "Print_PdfTitle"), content, primary,
            mode == PrintDialogMode.Print ? Loc.Get("Print_SavePdf") : null);
        dialog.Resources["ContentDialogMaxWidth"] = 960.0;
        dialog.Resources["ContentDialogMinWidth"] = 900.0;
        dialog.Resources["ContentDialogMaxHeight"] = 720.0;

        PrintSettings? current = null;

        void Refresh()
        {
            double started = Environment.TickCount64;
            PrintSettings? collected = content.Collect(out string? problem);
            _printProblem = problem;
            current = collected;
            bool ok = false;
            if (collected is null)
            {
                content.SetStatus(string.Empty, problem);
            }
            else if (PrintFonts(collected.FontFamily) is not { } fonts)
            {
                content.SetStatus(string.Empty, Loc.Get("Print_Error_NoFont"));
            }
            else
            {
                PrintPlan plan = PrintPlan.Create(collected, data.Length, fonts);
                _printPlan = plan;
                string? error = !plan.PageRangeValid ? Loc.Get("Print_Error_PageRange")
                    : plan.PrintedPages == 0 ? Loc.Get("Print_Error_Nothing")
                    : plan.ExceedsLimit ? Loc.Format("Print_Error_Limit", plan.PrintedPages.ToString("N0", CultureInfo.CurrentCulture), PrintSettings.MaxPages.ToString("N0", CultureInfo.CurrentCulture))
                    : null;
                content.SetStatus(Loc.Format("Print_Status", plan.PrintedPages, plan.BytesPerRow, plan.RowsPerPage), error);
                _printProblem = error;
                ok = error is null;
                if (plan.PrintedPages > 0 && plan.PageRangeValid)
                {
                    preview.SetContent(plan, data, fields, fonts);
                }
                else
                {
                    preview.SetContent(PrintPlan.Create(collected with { Range = PrintRangeKind.All }, Math.Min(data.Length, 1), fonts), data, fields, fonts);
                }

                if (mode == PrintDialogMode.Print && content.SelectedPrinter is null)
                {
                    dialog.IsPrimaryButtonEnabled = false;
                    dialog.IsSecondaryButtonEnabled = ok;
                    _printRefreshMs = Environment.TickCount64 - started;
                    return;
                }
            }

            dialog.IsPrimaryButtonEnabled = ok;
            dialog.IsSecondaryButtonEnabled = ok;
            _printRefreshMs = Environment.TickCount64 - started;
        }

        content.SettingsChanged += (_, _) => Refresh();
        content.SaveDefaultsRequested += (_, _) =>
        {
            if (current is not null)
            {
                AppState.SetString(PrintDefaultsKey, current.ToJson().ToJsonString());
                ShowNotice(Loc.Get("Print_DefaultsSaved"), InfoBarSeverity.Success);
            }
        };
        dialog.Opened += (_, _) =>
        {
            if (focusPreview)
            {
                preview.FocusPreview();
            }
        };

        _printContent = content;
        _printPreview = preview;
        Refresh();
        try
        {
            ContentDialogResult result = await ShowEditDialogAsync(dialog);
            PrintChoice choice = result switch
            {
                ContentDialogResult.Primary => mode == PrintDialogMode.Print ? PrintChoice.Print : PrintChoice.Pdf,
                ContentDialogResult.Secondary => PrintChoice.Pdf,
                _ => PrintChoice.Cancel,
            };
            return (choice, choice == PrintChoice.Cancel ? null : current);
        }
        finally
        {
            // 次のダイアログがすでに開いていれば、そちらを残す。
            if (ReferenceEquals(_printContent, content))
            {
                _printContent = null;
                _printPreview = null;
            }
        }
    }

    // ---- 印刷 ----

    private async Task PrintAsync(DocumentViewModel doc, PrintPlan plan, IPrintData data, PrintFields fields, SystemFontMetrics fonts, PrintSettings settings)
    {
        string printer = settings.Printer ?? string.Empty;
        string? testFile = TestHooks.PrintToFile;
        bool unreadable = false;
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Print_Operation", doc.DisplayName), OperationKind.ReadOnly, null, plan.PrintedPages, op =>
            {
                if (testFile is not null)
                {
                    // テスト用: 物理的なプリンタには送らず、同じページの内容を PDF に書く。
                    unreadable = PdfExporter.WriteFile(testFile, plan, data, fields, fonts, SystemFontCatalog.Instance, op, op.CancellationToken).HadUnreadable;
                }
                else
                {
                    unreadable = GdiPrinter.Print(printer, doc.DisplayName, settings.Copies, plan, data, fields, fonts, SystemFontCatalog.Instance, op.Report, op.CancellationToken);
                }

                return Task.CompletedTask;
            });
            ShowNotice(Loc.Format("Print_Done", plan.PrintedPages), InfoBarSeverity.Success);
            if (unreadable)
            {
                ShowNotice(Loc.Get("Print_Unreadable"), InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.Get("Print_Cancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Print_Error_Failed", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---- PDF ----

    private async Task ExportPdfAsync(DocumentViewModel doc, PrintPlan plan, IPrintData data, PrintFields fields, SystemFontMetrics fonts)
    {
        string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + ".pdf";
        string? path;
        if (!TestHooks.TrySavePicker(suggested, out path))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.ExportPdf" };
            picker.FileTypeChoices.Add(Loc.Get("Print_PdfFileType"), [".pdf"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        await WritePdfAsync(Path.GetFullPath(path), plan, data, fields, fonts);
    }

    /// <summary>PDF を書き出す(処理センターに進捗を出す。一時ファイルに書いてから置き換え、失敗したら書きかけを残さない)。</summary>
    internal async Task<bool> WritePdfAsync(string path, PrintPlan plan, IPrintData data, PrintFields fields, SystemFontMetrics fonts)
    {
        try
        {
            PdfExportResult? result = null;
            await Vm.Operations.RunAsync(Loc.Format("Print_Operation_Pdf", Path.GetFileName(path)), OperationKind.WritesExternal, null, plan.PrintedPages, op =>
            {
                result = PdfExporter.WriteFile(path, plan, data, fields, fonts, SystemFontCatalog.Instance, op, op.CancellationToken);
                return Task.CompletedTask;
            });
            ShowNotice(Loc.Format("Print_PdfDone", Path.GetFileName(path), result!.Pages), InfoBarSeverity.Success);
            if (result.HadUnreadable)
            {
                ShowNotice(Loc.Get("Print_Unreadable"), InfoBarSeverity.Warning);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.Get("Print_Cancelled"), InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Print_Error_PdfFailed", ex.Message), InfoBarSeverity.Error);
        }

        return false;
    }
}
