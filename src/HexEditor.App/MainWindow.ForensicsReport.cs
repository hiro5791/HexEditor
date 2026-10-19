using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.Reports;
using HexEditor.Core.Forensics.Time;
using HexEditor.Core.Operations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// レポートの作成 (12 の FOR-33): 項目・形式・用紙・保存先を選ぶダイアログ、長時間処理としての作成、完了の通知 (レポートの SHA-256、「開く」「フォルダを開く」)。
/// レポートの内容は Core の <see cref="ReportBuilder"/> が作る。文言は UI の言語 (リソースの <c>Report_*</c>)。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterReportCommands() => Commands.Register("forensics.report.create", () => ShowReportDialogAsync(), NeedsCase);

    private static string ReportString(string key) => Loc.TryGet("Report_" + key) ?? ReportText.English(key);

    private async Task ShowReportDialogAsync()
    {
        if (ForensicsService.Cases.CurrentCase is not { } current)
        {
            return;
        }

        var sectionChecks = ReportSections.All.ToDictionary(id => id, id => DialogParts.Check("Report_Section_" + id, ReportString("Section_" + id), true));
        RadioButton html = DialogParts.Radio("Report_Html", Loc.Get("Report_Format_Html"), "ReportFormat", true);
        RadioButton pdf = DialogParts.Radio("Report_Pdf", Loc.Get("Report_Format_Pdf"), "ReportFormat", false);
        RadioButton a4 = DialogParts.Radio("Report_A4", Loc.Get("Report_Paper_A4"), "ReportPaper", true);
        RadioButton letter = DialogParts.Radio("Report_Letter", Loc.Get("Report_Paper_Letter"), "ReportPaper", false);
        CheckBox auditAll = DialogParts.Check("Report_AuditAll", Loc.Get("Report_AuditAll"), true);
        TextBox auditFrom = DialogParts.Field("Report_AuditFrom", Loc.Get("Report_AuditFrom"), string.Empty);
        TextBox auditTo = DialogParts.Field("Report_AuditTo", Loc.Get("Report_AuditTo"), string.Empty);
        TextBox dump = DialogParts.Field("Report_Dump", Loc.Get("Report_Dump"), "64");
        TextBox folder = DialogParts.Field("Report_Folder", Loc.Get("Report_Folder"), current.ReportsFolder);
        var browse = new Button { Content = Loc.Get("Case_Browse") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(browse, "Report_Browse");
        browse.Click += async (_, _) =>
        {
            if (await PickFolderToolAsync("HexEditor.ReportFolder") is { } picked)
            {
                folder.Text = picked;
            }
        };
        var error = DialogParts.Caption("Report_Error");
        error.TextWrapping = TextWrapping.Wrap;
        error.Foreground = Controls.Tools.ToolParts.ThemeBrush("SystemFillColorCriticalBrush");

        var body = new StackPanel { Spacing = 6, MinWidth = 440 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Report_Sections"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        foreach (CheckBox c in sectionChecks.Values)
        {
            body.Children.Add(c);
        }

        body.Children.Add(new TextBlock { Text = Loc.Get("Report_Format"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        foreach (UIElement e in new UIElement[] { html, pdf, a4, letter, auditAll, auditFrom, auditTo, dump, folder, browse, error })
        {
            body.Children.Add(e);
        }

        a4.IsEnabled = letter.IsEnabled = false;
        pdf.Checked += (_, _) => a4.IsEnabled = letter.IsEnabled = true;
        html.Checked += (_, _) => a4.IsEnabled = letter.IsEnabled = false;
        auditAll.Checked += (_, _) => auditFrom.IsEnabled = auditTo.IsEnabled = false;
        auditAll.Unchecked += (_, _) => auditFrom.IsEnabled = auditTo.IsEnabled = true;
        auditFrom.IsEnabled = auditTo.IsEnabled = false;

        ContentDialog dialog = DialogParts.Dialog(Root, "ReportDialog", Loc.Get("Report_DialogTitle"), new ScrollViewer { Content = body, MaxHeight = 640 }, Loc.Get("Report_Create"));
        void Update() => dialog.IsPrimaryButtonEnabled = sectionChecks.Values.Any(c => c.IsChecked == true) && folder.Text.Trim().Length > 0;
        foreach (CheckBox c in sectionChecks.Values)
        {
            c.Checked += (_, _) => Update();
            c.Unchecked += (_, _) => Update();
        }

        folder.TextChanged += (_, _) => Update();
        Update();

        ReportOptions? options = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            // 期間 (ISO 8601) と Hex ダンプの長さの検査。誤りがあればダイアログを閉じない。
            DateTime? from = null, to = null;
            if (auditAll.IsChecked != true)
            {
                if (auditFrom.Text.Trim().Length > 0 && !DateTime.TryParse(auditFrom.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime f)
                    || auditTo.Text.Trim().Length > 0 && !DateTime.TryParse(auditTo.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
                {
                    args.Cancel = true;
                    error.Text = Loc.Get("Report_Error_Period");
                    return;
                }

                from = auditFrom.Text.Trim().Length > 0 ? DateTime.Parse(auditFrom.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : null;
                to = auditTo.Text.Trim().Length > 0 ? DateTime.Parse(auditTo.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : null;
            }

            if (!int.TryParse(dump.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int dumpBytes) || dumpBytes is < 0 or > 4096)
            {
                args.Cancel = true;
                error.Text = Loc.Get("Report_Error_Dump");
                return;
            }

            options = ReportOptionsFor(new HashSet<string>(sectionChecks.Where(c => c.Value.IsChecked == true).Select(c => c.Key)), pdf.IsChecked == true, letter.IsChecked == true,
                folder.Text.Trim(), from, to, dumpBytes);
        };
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || options is null)
        {
            return;
        }

        await CreateReportAsync(options);
    }

    internal ReportOptions ReportOptionsFor(ISet<string> sections, bool pdf, bool letter, string folder, DateTime? from, DateTime? to, int dumpBytes) => new()
    {
        Sections = sections,
        Format = pdf ? ReportFormat.Pdf : ReportFormat.Html,
        Paper = letter ? ReportPaper.Letter : ReportPaper.A4,
        OutputFolder = folder,
        AuditFromUtc = from,
        AuditToUtc = to,
        DumpBytes = dumpBytes,
        Zone = ForensicsService.Cases.CurrentCase?.TimeZone ?? TimeZoneInfo.Local,
        Text = ReportString,
        Language = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride is { Length: > 0 } language ? language : CultureInfo.CurrentUICulture.Name,
        LogoPath = App.Settings.GetString("forensics.report.logo", string.Empty) is { Length: > 0 } logo ? logo : null,
        HeaderText = App.Settings.GetString("forensics.report.header", string.Empty) is { Length: > 0 } header ? header : null,
        Audit = ForensicsService.Audit,
    };

    /// <summary>レポートのブックマークの元 (UI のスレッドで写したもの): 表示名、データの写し、セクタサイズ、ブックマーク。</summary>
    private sealed record BookmarkSource(string Document, Core.Engine.DocumentSnapshot Data, int SectorSize, IReadOnlyList<Bookmark> Bookmarks);

    /// <summary>開いているすべてのドキュメントのブックマークを写す (UI のスレッドで呼ぶ。データは読まない)。</summary>
    private static List<BookmarkSource> CaptureBookmarks()
    {
        var list = new List<BookmarkSource>();
        foreach (DocumentViewModel doc in WindowManager.Windows.SelectMany(w => w.Vm.Documents).Distinct())
        {
            if (BookmarkCollection.For(doc.Document) is { } bookmarks)
            {
                list.Add(new BookmarkSource(doc.DisplayName, doc.Document.DataSnapshot, SectorSizeOf(doc), [.. bookmarks.All]));
            }
        }

        return list;
    }

    /// <summary>
    /// ブックマーク (名前、オフセット、LBA、コメント、その位置の Hex ダンプ)。バックグラウンドで作る。Hex ダンプは先頭の
    /// <see cref="ReportTable.DefaultMaxRows"/> 件だけに付ける (ブックマークが多くてもメモリと時間を件数に比例させすぎない。FOR-33 の実装の注記)。
    /// </summary>
    private static List<ReportBookmark> CollectBookmarks(IReadOnlyList<BookmarkSource> sources, int dumpBytes, CancellationToken cancellationToken)
    {
        var list = new List<ReportBookmark>();
        foreach (BookmarkSource source in sources)
        {
            foreach (Bookmark b in source.Bookmarks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long available = Math.Max(0, Math.Min(4096, source.Data.Length - b.Start));
                byte[] bytes = list.Count < ReportTable.DefaultMaxRows ? new byte[Math.Clamp((long)dumpBytes, 0, available)] : [];
                if (bytes.Length > 0)
                {
                    source.Data.Read(b.Start, bytes);
                }

                list.Add(new ReportBookmark(source.Document, b.Name, b.Start, b.Start / source.SectorSize, b.Comment, bytes));
            }
        }

        return list;
    }

    /// <summary>レポートを作る (長時間処理)。完了したら SHA-256 を示す。作ったレポートの結果を返す (キャンセル・失敗は null)。</summary>
    internal async Task<ReportResult?> CreateReportAsync(ReportOptions options)
    {
        if (ForensicsService.Cases.CurrentCase is not { } current)
        {
            return null;
        }

        string? manifest = ForensicsService.LastCarving?.ManifestPath;
        manifest ??= Path.Combine(current.CarvedFolder, Core.Forensics.Carving.CarvingEngine.ManifestFileName);
        List<BookmarkSource> bookmarkSources = options.Sections.Contains(ReportSections.Bookmarks) ? CaptureBookmarks() : [];
        CaseInfo info = current.Info;
        List<EvidenceItem> evidence = [.. current.Evidence];
        ReportInput InputFor(CancellationToken token) => new()
        {
            Case = info,
            Evidence = evidence,
            Bookmarks = CollectBookmarks(bookmarkSources, options.DumpBytes, token),
            CarvingManifests = File.Exists(manifest) ? [manifest] : [],
            AuditLogPath = current.AuditPath,
            AppName = "HexEditor",
            AppVersion = Program.Environment.AppVersion,
            NowUtc = ForensicsService.Clock.GetUtcNow().UtcDateTime,
            Registry = ReportRegistry.Shared,
        };

        // 節の寄稿者 (パーティション・ファイルシステム) が読むドキュメントの一覧は、UI のスレッドで写してからバックグラウンドで作る。
        ForensicsService.CaptureReportDocuments();
        try
        {
            ReportResult result = await Vm.Operations.RunAsync(Loc.Get("Report_Operation"), OperationKind.WritesExternal, null, null,
                op => Task.Run(() => ReportBuilder.Create(InputFor(op.CancellationToken), options, op.CancellationToken)));
            ForensicsService.LastReport = result;
            ShowReportDone(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Report_Failed", ex.Message), InfoBarSeverity.Error);
            return null;
        }
        finally
        {
            ForensicsService.ReleaseReportDocuments();
        }
    }

    private void ShowReportDone(ReportResult result)
    {
        var actions = new List<Core.Notifications.NotificationAction>
        {
            new(Loc.Get("Report_Open"), () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(result.Path))),
            new(Loc.Get("Report_OpenFolder"), () => _ = Windows.System.Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(result.Path)!)),
        };
        if (result.PdfError is { } pdfError)
        {
            ShowNotice(Loc.Format("Report_PdfFailed", pdfError, Path.GetFileName(result.Path)), InfoBarSeverity.Warning, actions: actions);
        }
        else
        {
            ShowNotice(Loc.Format("Report_Done", Path.GetFileName(result.Path), result.Sha256), InfoBarSeverity.Success, actions: actions);
            if (result.PdfTruncated)
            {
                ShowNotice(Loc.Get("Report_PdfTruncated"), InfoBarSeverity.Informational);
            }
        }
    }
}
