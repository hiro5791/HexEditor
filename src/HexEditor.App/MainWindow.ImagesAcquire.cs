using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Scope;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>イメージの作成 (12 の FOR-20)、スケルトン・スパース (FOR-23)、イメージの検証 (FOR-22)。</summary>
public sealed partial class MainWindow
{
    /// <summary>イメージ作成のダイアログの入力 (テストの命令からも作る)。</summary>
    internal sealed record CreateImageRequest
    {
        public required DocumentViewModel Source { get; init; }

        public bool SelectionOnly { get; init; }

        public AcquisitionScope Scope { get; init; }

        /// <summary>スケルトンにブックマーク・マルチ選択の範囲を加える。</summary>
        public bool IncludeMarked { get; init; }

        public required AcquisitionOptions Options { get; init; }
    }

    /// <summary>最後に終えた取得の結果 (テスト用)。</summary>
    internal AcquisitionResult? LastAcquisition { get; private set; }

    /// <summary>最後の完了の通知の文 (テスト用)。</summary>
    internal string? LastAcquisitionNotice { get; private set; }

    /// <param name="selectionOnly">パーティションパネルの右クリックメニューから呼んだ: 選択した範囲 (パーティション) を入力元の既定にする。</param>
    internal async Task CreateImageAsync(AcquisitionScope scope, bool selectionOnly = false)
    {
        if (Vm.Selected is not { } doc)
        {
            ShowNotice(Loc.Get("Img_NeedsSource"), InfoBarSeverity.Informational);
            return;
        }

        CreateImageRequest? request = await ShowCreateImageDialogAsync(doc, scope, selectionOnly);
        if (request is not null)
        {
            await RunAcquisitionAsync(request);
        }
    }

    /// <summary>出力先の既定のフォルダ: ケースの <c>images/</c>、なければ入力元のファイルのフォルダ、なければドキュメント。</summary>
    private static string DefaultImageFolder(DocumentViewModel doc) =>
        ImageForensics.Hooks.Case?.Folder is { } folder ? Path.Combine(folder, "images")
        : doc.FilePath is { } path ? Path.GetDirectoryName(path)!
        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static string ImgSafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c)).Trim('_') is { Length: > 0 } n ? n : "image";

    /// <summary>
    /// イメージ作成のダイアログ (FOR-20 の「画面」、FOR-23 の取得する範囲): 入力元、取得する範囲、出力形式、出力先と名前、分割サイズ、ハッシュ、
    /// エラー時の動作、E01 の情報。書き込み保護モードが無効なら、有効にすることを勧める (仕様 1)。
    /// </summary>
    private async Task<CreateImageRequest?> ShowCreateImageDialogAsync(DocumentViewModel doc, AcquisitionScope initialScope, bool selectionOnlyDefault = false)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 480 };
        if (!ImageForensics.Hooks.IsWriteBlocked)
        {
            var suggest = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = Loc.Get("Img_SuggestWriteBlock") };
            var enable = new Button { Content = Loc.Get("Img_EnableWriteBlock") };
            AutomationProperties.SetAutomationId(enable, "ImgCreate_EnableWriteBlock");
            enable.Click += (_, _) =>
            {
                WriteBlockMode.Enable("image");
                suggest.IsOpen = false;
            };
            suggest.ActionButton = enable;
            AutomationProperties.SetAutomationId(suggest, "ImgCreate_WriteBlockSuggestion");
            panel.Children.Add(suggest);
        }

        // 入力元。
        TextBlock sourceText = ImgNote("ImgCreate_Source", Loc.Format("Img_SourceLabel", doc.DisplayName));
        panel.Children.Add(sourceText);
        bool hasSelection = doc.Editor.SelectionLength > 0;
        CheckBox selectionOnly = DialogParts.Check("ImgCreate_SelectionOnly", Loc.Get("Img_SelectionOnly"), selectionOnlyDefault && hasSelection);
        selectionOnly.IsEnabled = hasSelection;
        panel.Children.Add(selectionOnly);

        // 取得する範囲 (FOR-23)。
        RadioButton full = DialogParts.Radio("ImgCreate_ScopeFull", Loc.Get("Img_Scope_Full"), "imgScope", initialScope == AcquisitionScope.Full);
        RadioButton sparse = DialogParts.Radio("ImgCreate_ScopeSparse", Loc.Get("Img_Scope_Sparse"), "imgScope", initialScope == AcquisitionScope.Sparse);
        RadioButton skeleton = DialogParts.Radio("ImgCreate_ScopeSkeleton", Loc.Get("Img_Scope_Skeleton"), "imgScope", initialScope == AcquisitionScope.Skeleton);
        CheckBox includeMarked = DialogParts.Check("ImgCreate_IncludeMarked", Loc.Get("Img_IncludeMarked"), false);
        var scopePanel = new StackPanel { Spacing = 2 };
        scopePanel.Children.Add(full);
        scopePanel.Children.Add(sparse);
        scopePanel.Children.Add(skeleton);
        scopePanel.Children.Add(includeMarked);
        panel.Children.Add(ImgSection(Loc.Get("Img_ScopeHeader"), scopePanel));

        // 出力形式と出力先。
        ComboBox format = DialogParts.Combo("ImgCreate_Format", Loc.Get("Img_Format"), [Loc.Get("Img_Format_Raw"), Loc.Get("Img_Format_SplitRaw"), "E01"], 0);
        string baseName = ImgSafeName(Path.GetFileNameWithoutExtension(doc.DisplayName));
        TextBox output = DialogParts.Field("ImgCreate_Output", Loc.Get("Img_Output"), Path.Combine(DefaultImageFolder(doc), baseName + ".dd"), monospace: false);
        var browse = new Button { Content = Loc.Get("Img_Browse") };
        AutomationProperties.SetAutomationId(browse, "ImgCreate_Browse");
        TextBox split = DialogParts.Field("ImgCreate_SplitSize", Loc.Get("Img_SplitSize"), "2G");
        var outputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        output.MinWidth = 380;
        outputRow.Children.Add(output);
        outputRow.Children.Add(browse);
        var formatPanel = new StackPanel { Spacing = 6 };
        formatPanel.Children.Add(format);
        formatPanel.Children.Add(outputRow);
        formatPanel.Children.Add(split);
        panel.Children.Add(ImgSection(Loc.Get("Img_OutputHeader"), formatPanel));

        // ハッシュとエラー時の動作 (仕様 3)。
        CheckBox md5 = DialogParts.Check("ImgCreate_HashMd5", "MD5", true);
        CheckBox sha1 = DialogParts.Check("ImgCreate_HashSha1", "SHA-1", false);
        CheckBox sha256 = DialogParts.Check("ImgCreate_HashSha256", "SHA-256", true);
        var hashes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        hashes.Children.Add(md5);
        hashes.Children.Add(sha1);
        hashes.Children.Add(sha256);
        panel.Children.Add(ImgSection(Loc.Get("Img_Hashes"), hashes));
        TextBox retries = DialogParts.Field("ImgCreate_Retries", Loc.Get("Img_Retries"), "3");
        TextBox fill = DialogParts.Field("ImgCreate_Fill", Loc.Get("Img_Fill"), "00");
        ComboBox unit = DialogParts.Combo("ImgCreate_ReadUnit", Loc.Get("Img_ReadUnit"), ["64 KiB", "256 KiB", "1 MiB", "4 MiB", "16 MiB"], 2);
        CheckBox verify = DialogParts.Check("ImgCreate_Verify", Loc.Get("Img_VerifyAfter"), true);
        var errorsPanel = new StackPanel { Spacing = 6 };
        errorsPanel.Children.Add(retries);
        errorsPanel.Children.Add(fill);
        errorsPanel.Children.Add(unit);
        errorsPanel.Children.Add(verify);
        panel.Children.Add(ImgSection(Loc.Get("Img_ErrorsHeader"), errorsPanel));

        // E01 の情報 (仕様 4)。ケースがあればケースの値を初期値にする。
        ImageCaseInfo? caseInfo = ImageForensics.Hooks.Case;
        TextBox caseNumber = DialogParts.Field("ImgCreate_CaseNumber", Loc.Get("Img_Case_Number"), caseInfo?.CaseNumber ?? string.Empty, monospace: false);
        TextBox evidence = DialogParts.Field("ImgCreate_Evidence", Loc.Get("Img_Case_Evidence"), string.Empty, monospace: false);
        TextBox examiner = DialogParts.Field("ImgCreate_Examiner", Loc.Get("Img_Case_Examiner"), caseInfo?.Examiner ?? string.Empty, monospace: false);
        TextBox description = DialogParts.Field("ImgCreate_Description", Loc.Get("Img_Case_Description"), doc.DisplayName, monospace: false);
        TextBox notes = DialogParts.Field("ImgCreate_Notes", Loc.Get("Img_Case_Notes"), string.Empty, monospace: false);
        ComboBox compression = DialogParts.Combo("ImgCreate_Compression", Loc.Get("Img_Compression"),
            [Loc.Get("Img_Compression_None"), Loc.Get("Img_Compression_Fast"), Loc.Get("Img_Compression_Best")], 1);
        var e01Panel = new StackPanel { Spacing = 6 };
        foreach (FrameworkElement e in new FrameworkElement[] { caseNumber, evidence, examiner, description, notes, compression })
        {
            e01Panel.Children.Add(e);
        }

        Border e01Section = ImgSection(Loc.Get("Img_E01Header"), e01Panel);
        panel.Children.Add(e01Section);
        TextBlock error = DialogParts.Caption("ImgCreate_Error");
        panel.Children.Add(error);

        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 };
        ContentDialog dialog = DialogParts.Dialog(Root, "ImgCreateDialog", Loc.Get(initialScope == AcquisitionScope.Full ? "Img_CreateTitle" : "Img_SkeletonTitle"), scroll,
            Loc.Get("Img_Start"));

        ImageFormat Format() => format.SelectedIndex switch { 1 => ImageFormat.SplitRaw, 2 => ImageFormat.E01, _ => ImageFormat.Raw };
        void Validate()
        {
            ImageFormat f = Format();
            split.Visibility = f == ImageFormat.Raw ? Visibility.Collapsed : Visibility.Visible;
            e01Section.Visibility = f == ImageFormat.E01 ? Visibility.Visible : Visibility.Collapsed;
            includeMarked.IsEnabled = skeleton.IsChecked == true;
            string? problem = null;
            if (output.Text.Trim().Length == 0)
            {
                problem = Loc.Get("Img_NeedsOutput");
            }
            else if (f != ImageFormat.Raw && (ImgParseSize(split.Text) is not long s || s < 1024 * 1024))
            {
                problem = Loc.Get("Img_SplitSizeInvalid");
            }
            else if (!int.TryParse(retries.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int r) || r > 100)
            {
                problem = Loc.Get("Img_RetriesInvalid");
            }
            else if (!byte.TryParse(fill.Text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                problem = Loc.Get("Img_FillInvalid");
            }
            else if (md5.IsChecked != true && sha1.IsChecked != true && sha256.IsChecked != true)
            {
                problem = Loc.Get("Img_NeedsHash");
            }

            DialogParts.MarkInvalid(split, f != ImageFormat.Raw && ImgParseSize(split.Text) is null);
            error.Text = problem ?? string.Empty;
            dialog.IsPrimaryButtonEnabled = problem is null;
        }

        format.SelectionChanged += (_, _) =>
        {
            // 拡張子を形式に合わせる (raw は .dd、分割 raw は名前だけ、E01 は .E01)。
            string text = output.Text.Trim();
            string stem = Path.Combine(Path.GetDirectoryName(text) ?? string.Empty, Path.GetFileNameWithoutExtension(text));
            output.Text = Format() switch { ImageFormat.E01 => stem + ".E01", ImageFormat.SplitRaw => stem, _ => stem + ".dd" };
            Validate();
        };
        foreach (TextBox box in new[] { output, split, retries, fill })
        {
            box.TextChanged += (_, _) => Validate();
        }

        foreach (CheckBox box in new[] { md5, sha1, sha256 })
        {
            box.Click += (_, _) => Validate();
        }

        foreach (RadioButton radio in new[] { full, sparse, skeleton })
        {
            radio.Checked += (_, _) => Validate();
        }

        browse.Click += async (_, _) =>
        {
            if (await PickImageOutputAsync(output.Text, Format()) is { } chosen)
            {
                output.Text = chosen;
            }
        };
        Validate();
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var algorithms = new List<string>();
        if (md5.IsChecked == true)
        {
            algorithms.Add(ImageHashes.Md5);
        }

        if (sha1.IsChecked == true)
        {
            algorithms.Add(ImageHashes.Sha1);
        }

        if (sha256.IsChecked == true)
        {
            algorithms.Add(ImageHashes.Sha256);
        }

        AcquisitionScope scope = sparse.IsChecked == true ? AcquisitionScope.Sparse : skeleton.IsChecked == true ? AcquisitionScope.Skeleton : AcquisitionScope.Full;
        return new CreateImageRequest
        {
            Source = doc,
            SelectionOnly = selectionOnly.IsChecked == true,
            Scope = scope,
            IncludeMarked = includeMarked.IsChecked == true,
            Options = new AcquisitionOptions
            {
                Output = output.Text.Trim(),
                Format = Format(),
                SplitSize = ImgParseSize(split.Text) ?? 2L << 30,
                Hashes = algorithms,
                Retries = int.Parse(retries.Text, CultureInfo.InvariantCulture),
                Fill = byte.Parse(fill.Text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ReadUnit = unit.SelectedIndex switch { 0 => 64 * 1024, 1 => 256 * 1024, 3 => 4 << 20, 4 => 16 << 20, _ => 1 << 20 },
                Verify = verify.IsChecked == true,
                Compression = (EwfCompression)Math.Max(0, compression.SelectedIndex),
                Case = new EwfCaseInfo
                {
                    CaseNumber = caseNumber.Text,
                    EvidenceNumber = evidence.Text,
                    Examiner = examiner.Text,
                    Description = description.Text,
                    Notes = notes.Text,
                },
            },
        };
    }

    private async Task<string?> PickImageOutputAsync(string current, ImageFormat format)
    {
        string suggested = Path.GetFileName(current);
        if (TestHooks.TrySavePicker(suggested, out string? path))
        {
            return path;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = Path.GetFileNameWithoutExtension(current), SettingsIdentifier = "HexEditor.ImageOutput" };
        switch (format)
        {
            case ImageFormat.E01:
                picker.FileTypeChoices.Add("E01", [".E01"]);
                break;
            case ImageFormat.SplitRaw:
                picker.FileTypeChoices.Add(Loc.Get("Img_Format_SplitRaw"), [".001"]);
                break;
            default:
                picker.FileTypeChoices.Add(Loc.Get("Img_Format_Raw"), [".dd", ".raw", ".img"]);
                break;
        }

        string? chosen = (await picker.PickSaveFileAsync())?.Path;
        return chosen is not null && format == ImageFormat.SplitRaw && SplitRawNaming.TryParse(chosen, out string b, out _) ? b : chosen;
    }

    /// <summary>
    /// 取得を実行する (FOR-20・FOR-23): 出力先の検査 (共通の前提) → 範囲の計算 (スパース・スケルトン) → 取得 (処理センター) → 完了の通知
    /// (ハッシュ値と「取得ログを開く」、読めなかったセクタの警告)。
    /// </summary>
    internal async Task<AcquisitionResult?> RunAcquisitionAsync(CreateImageRequest request, Func<IOException, Task<bool>>? retryPrompt = null,
        Func<Task<bool>>? keepPrompt = null)
    {
        DocumentViewModel doc = request.Source;
        IByteSource source = doc.Document.Source;
        AcquisitionSourceInfo info = ImgSourceInfoOf(doc);
        int sector = info.SectorSize;
        long start = 0;
        long length = source.Length;
        if (request.SelectionOnly && doc.Editor.SelectionLength > 0)
        {
            // 選択範囲をセクタ境界に広げる (仕様 1)。
            start = doc.Editor.SelectionStart / sector * sector;
            long end = Math.Min(source.Length, (doc.Editor.SelectionStart + doc.Editor.SelectionLength + sector - 1) / sector * sector);
            length = end - start;
        }

        AcquisitionOptions options = request.Options with
        {
            RangeStart = start,
            RangeLength = length,
            Scope = request.Scope,
            AppVersion = ImageAppVersion,
            LogText = ImgLogText(),
            DetailText = n => Loc.Format("Img_DetailBadSectors", n.ToString("N0", CultureInfo.CurrentCulture)),
        };

        // 出力先の検査: 入力元・証拠のディスク上でないこと、空き容量、1 ファイルの上限 (共通の前提「出力先の検査」、仕様 6)。
        string folder = Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? ".";
        long required = options.Format == ImageFormat.Raw ? length + 1024 * 1024 : length;
        long largest = options.Format == ImageFormat.Raw ? length : Math.Min(length, options.SplitSize);
        OutputCheckResult check = ImageOutputCheck.Check(ImgExistingParent(folder), required, largest, info.DiskNumber, ImageForensics.Hooks.EvidenceDiskNumbers,
            new DefaultOutputVolumeProbe(DeviceService.EnumerateDevices));
        bool compressedE01 = options.Format == ImageFormat.E01 && options.Compression != EwfCompression.None;
        if (!check.Ok && !(check.Problem == OutputProblem.InsufficientSpace && compressedE01) && !(check.Problem == OutputProblem.InsufficientSpace && request.Scope != AcquisitionScope.Full))
        {
            await ShowMessageAsync(Loc.Get("Img_CreateTitle"), ImgOutputProblemText(check), "ImgOutputProblemDialog");
            return null;
        }

        if (check.Problem == OutputProblem.InsufficientSpace)
        {
            ShowNotice(ImgOutputProblemText(check), InfoBarSeverity.Warning, doc);
        }

        if (ImgOutputExists(options) && !await ConfirmAsync(Loc.Get("Img_CreateTitle"), Loc.Format("Img_OverwriteConfirm", options.Output), Loc.Get("Img_Overwrite"),
            "ImgOverwriteDialog"))
        {
            return null;
        }

        // スパースの raw は NTFS 以外では 0 を書く (容量は減らない) ことを開始前に示す (FOR-23 の仕様 2)。
        if (request.Scope != AcquisitionScope.Full && options.Format != ImageFormat.E01 && check.FileSystem is { } fs
            && !string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase)
            && !await ConfirmAsync(Loc.Get("Img_CreateTitle"), Loc.Format("Img_NotNtfsZeros", fs), Loc.Get("Img_Start"), "ImgNotNtfsDialog"))
        {
            return null;
        }

        var prompts = new ImgDialogPrompts(this, retryPrompt, keepPrompt);
        string name = Loc.Format("Img_OperationName", Path.GetFileName(options.Output));
        AcquisitionResult result;
        try
        {
            result = await Vm.Operations.RunAsync(name, OperationKind.ReadOnly, doc.Document, length, async op =>
            {
                AcquisitionOptions run = options;
                if (request.Scope != AcquisitionScope.Full)
                {
                    // 範囲の計算 (ビットマップの読み込み) も長時間処理に含める (FOR-23 の「巨大ファイル」)。
                    op.ReportDetail(Loc.Get("Img_PlanningRanges"));
                    IEnumerable<(long, long)>? extra = request.IncludeMarked ? ImgMarkedRanges(doc) : null;
                    ImageScopePlan plan = ImageScopePlanner.Plan(new SubRangeSource(source, start, length), sector, request.Scope, extra?.Select(r => (r.Item1 - start, r.Item2)),
                        op.CancellationToken);
                    run = run with { Included = [.. plan.Included.Select(r => (r.Offset + start, r.Length))] };
                    if (plan.WholePartitions.Count > 0)
                    {
                        DispatcherQueue.TryEnqueue(() => ShowNotice(Loc.Format("Img_WholePartitions", plan.WholePartitions.Count), InfoBarSeverity.Informational, doc));
                    }

                    op.ReportDetail(null);
                }

                return await ImageAcquisition.RunAsync(source.Read, source.Length, info, run, op, prompts, TestHooks.Time);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(Loc.Format("Img_CreateFailed", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }

        LastAcquisition = result;
        ReportAcquisition(doc, result);
        return result;
    }

    private static string ImgExistingParent(string folder)
    {
        string? at = folder;
        while (at is not null && !Directory.Exists(at))
        {
            at = Path.GetDirectoryName(at);
        }

        return at ?? folder;
    }

    private static bool ImgOutputExists(AcquisitionOptions options) => options.Format switch
    {
        ImageFormat.SplitRaw => File.Exists(SplitRawNaming.SegmentPath(options.Output, 1)),
        _ => File.Exists(options.Output),
    };

    /// <summary>ブックマーク・マルチ選択の範囲 (スケルトンに加える。FOR-23 の仕様 1)。</summary>
    private IEnumerable<(long, long)> ImgMarkedRanges(DocumentViewModel doc)
    {
        var ranges = new List<(long, long)>();
        if (_annotations.TryGetValue(doc, out DocumentAnnotations? a))
        {
            ranges.AddRange(a.Bookmarks.Overlapping(0, long.MaxValue).Select(b => (b.Start, Math.Max(1, b.Length))));
        }

        ranges.AddRange(doc.Editor.CaptureSelection().Ranges.Where(r => r.Length > 0).Select(r => (r.Start, r.Length)));
        return ranges;
    }

    internal static string ImgOutputProblemText(OutputCheckResult check) => check.Problem switch
    {
        OutputProblem.SameDiskAsSource => Loc.Get("Img_Output_SameDisk"),
        OutputProblem.OnEvidenceDisk => Loc.Get("Img_Output_EvidenceDisk"),
        OutputProblem.FileTooLarge => Loc.Format("Img_Output_FileTooLarge", check.FileSystem ?? string.Empty,
            (check.FileLimit ?? 0).ToString("N0", CultureInfo.CurrentCulture)),
        OutputProblem.InsufficientSpace => Loc.Format("Img_Output_Space", check.Required.ToString("N0", CultureInfo.CurrentCulture),
            (check.Free ?? 0).ToString("N0", CultureInfo.CurrentCulture)),
        _ => string.Empty,
    };

    /// <summary>完了・中断・失敗の通知 (FOR-20 の「画面」): ハッシュ値と「取得ログを開く」、読めなかったセクタ数の警告。</summary>
    private void ReportAcquisition(DocumentViewModel doc, AcquisitionResult result)
    {
        var actions = new List<Core.Notifications.NotificationAction>();
        if (File.Exists(result.LogPath))
        {
            actions.Add(new(Loc.Get("Img_OpenLog"), () => TryOpen(result.LogPath, readOnly: true)));
        }

        string text;
        InfoBarSeverity severity;
        switch (result.Status)
        {
            case AcquisitionStatus.Completed:
                text = Loc.Format("Img_Done", string.Join("  ", result.Hashes.Select(h => h.Key + ": " + Convert.ToHexStringLower(h.Value))));
                if (result.Verification is { } v)
                {
                    text += " " + Loc.Format("Img_DoneVerified", ImgVerdictText(v.Overall));
                }

                severity = result.Verification?.Overall is VerificationVerdict.Mismatch or VerificationVerdict.ReadError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
                if (result.BadSectorCount > 0)
                {
                    text += " " + Loc.Format("Img_BadSectorsWarning", result.BadSectorCount.ToString("N0", CultureInfo.CurrentCulture));
                    severity = InfoBarSeverity.Warning;
                }

                break;
            case AcquisitionStatus.Cancelled:
                text = Loc.Get(result.KeptPartialFiles ? "Img_CancelledKept" : "Img_CancelledDeleted");
                severity = InfoBarSeverity.Informational;
                break;
            case AcquisitionStatus.SourceLost:
                text = Loc.Get("Img_SourceLost");
                severity = InfoBarSeverity.Error;
                break;
            default:
                text = Loc.Format("Img_CreateFailed", result.Message ?? string.Empty);
                severity = InfoBarSeverity.Error;
                break;
        }

        LastAcquisitionNotice = text;
        ShowNotice(text, severity, Vm.Documents.Contains(doc) ? doc : null, actions: actions);
    }

    /// <summary>取得中の問い合わせ (書き込みエラーの再試行、キャンセルの後の残す・削除する)。UI スレッドでダイアログを出す。</summary>
    private sealed class ImgDialogPrompts(MainWindow window, Func<IOException, Task<bool>>? retry, Func<Task<bool>>? keep) : IAcquisitionPrompts
    {
        public Task<bool> RetryAfterWriteErrorAsync(IOException error) =>
            retry is not null ? retry(error) : window.ImgOnUiAsync(() => window.ImgChooseAsync(Loc.Get("Img_CreateTitle"), Loc.Format("Img_WriteError", error.Message),
                Loc.Get("Img_Retry"), Loc.Get("Img_Abort"), "ImgWriteErrorDialog"));

        public Task<bool> KeepPartialFilesAsync() =>
            keep is not null ? keep() : window.ImgOnUiAsync(() => window.ImgChooseAsync(Loc.Get("Img_CreateTitle"), Loc.Get("Img_KeepPartial"),
                Loc.Get("Img_Keep"), Loc.Get("Img_Delete"), "ImgKeepPartialDialog"));
    }

    /// <summary>UI スレッドで非同期の処理をして結果を返す (長時間処理のスレッドから)。</summary>
    private Task<T> ImgOnUiAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                done.SetResult(await work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }))
        {
            done.SetCanceled();
        }

        return done.Task;
    }

    /// <summary>2 択のダイアログ (主ボタン → true、2 つ目のボタン → false)。閉じるボタンはない。</summary>
    private async Task<bool> ImgChooseAsync(string title, string body, string primary, string secondary, string automationId)
    {
        var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        AutomationProperties.SetAutomationId(text, automationId + "_Body");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = title,
            Content = text,
            PrimaryButtonText = primary,
            SecondaryButtonText = secondary,
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    // ---- イメージの検証 (FOR-22) ----

    /// <summary>最後の検証の結果 (テスト用)。</summary>
    internal VerificationResult? LastVerification { get; private set; }

    private async Task VerifyImageCommandAsync()
    {
        // 対象: 開いているイメージのドキュメント、証拠一覧のディスク (開いているディスクのタブ)、またはファイルを選ぶ (仕様 1)。
        if (Vm.Selected is { Device: { } device } diskDoc && diskDoc.Document.Source is not IImageSource)
        {
            await VerifySourceAsync(diskDoc.Document.Source, null, device.Info.Path);
            return;
        }

        IImageSource? image = Vm.Selected?.Document.Source as IImageSource;
        string? path = image is ImageSourceBase b ? b.PrimaryPath : null;
        bool owned = false;
        if (image is null)
        {
            IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.VerifyImage");
            path = paths?.FirstOrDefault();
            if (path is null)
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.VerifyImage" };
                picker.FileTypeFilter.Add("*");
                path = (await picker.PickSingleFileAsync())?.Path;
            }

            if (path is null)
            {
                return;
            }

            try
            {
                image = ImageOpener.Open(path, new ImageOpenOptions { AllowMissingSegments = true });
                owned = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowNotice(Loc.Format("Error_Open", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
                return;
            }
        }

        try
        {
            await VerifyImageAsync(image, path!);
        }
        finally
        {
            if (owned)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>検証のダイアログ (対象、アルゴリズム、比べる値の入力欄、入力元のディスクも読み直す) と、結果のダイアログ (FOR-22 の「画面」)。</summary>
    internal Task<VerificationResult?> VerifyImageAsync(IImageSource image, string path, Func<ContentDialog, Task>? interact = null) =>
        VerifySourceAsync(image, image, path, interact);

    /// <summary>
    /// 証拠を検証する (証拠パネルの右クリックメニュー。FOR-22 の「呼び出し」): ディスクの証拠は開いて (読み取り専用) そのデータソースを、
    /// イメージ・ファイルの証拠はイメージとして開いて検証する。比べる値は証拠一覧の値 (と、イメージなら格納値・取得ログ)。
    /// </summary>
    internal async Task VerifyEvidenceAsync(Core.Forensics.Cases.EvidenceItem item)
    {
        if (item.IsDevice)
        {
            await OpenEvidenceAsync(item);
            if (Vm.Selected is { Device: { } device } d && string.Equals(device.Info.Path, item.Path, StringComparison.OrdinalIgnoreCase))
            {
                await VerifySourceAsync(d.Document.Source, null, item.Path);
            }

            return;
        }

        IImageSource image;
        try
        {
            image = ImageOpener.Open(item.Path, new ImageOpenOptions { AllowMissingSegments = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_Open", Path.GetFileName(item.Path), ex.Message), InfoBarSeverity.Error);
            return;
        }

        try
        {
            await VerifyImageAsync(image, item.Path);
        }
        finally
        {
            image.Dispose();
        }
    }

    /// <summary>
    /// 検証のダイアログと実行。<paramref name="image"/> はイメージのとき (格納値・取得ログを比べる値にし、入力元のディスクの読み直しができる)。
    /// ディスクのとき (<paramref name="image"/> が null) は、証拠一覧の値と入力した値と比べる。
    /// </summary>
    private async Task<VerificationResult?> VerifySourceAsync(IByteSource data, IImageSource? image, string path, Func<ContentDialog, Task>? interact = null)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        panel.Children.Add(ImgNote("ImgVerify_Target", Loc.Format("Img_VerifyTarget", path)));
        CheckBox md5 = DialogParts.Check("ImgVerify_HashMd5", "MD5", true);
        CheckBox sha1 = DialogParts.Check("ImgVerify_HashSha1", "SHA-1", false);
        CheckBox sha256 = DialogParts.Check("ImgVerify_HashSha256", "SHA-256", true);
        var hashes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        hashes.Children.Add(md5);
        hashes.Children.Add(sha1);
        hashes.Children.Add(sha256);
        panel.Children.Add(hashes);
        TextBox expected = DialogParts.Field("ImgVerify_Expected", Loc.Get("Img_VerifyExpected"));
        expected.PlaceholderText = Loc.Get("Img_VerifyExpectedHint");
        panel.Children.Add(expected);

        // 入力元のディスクがまだ開いていれば、読み直して 3 者を比べられる (仕様 4)。取得ログの入力元のパスで探す。
        DocumentViewModel? sourceDisk = image is null ? null : SourceDiskOf(image);
        CheckBox reread = DialogParts.Check("ImgVerify_Reread", Loc.Get("Img_VerifyReread"), false);
        reread.IsEnabled = sourceDisk is not null;
        panel.Children.Add(reread);
        TextBlock error = DialogParts.Caption("ImgVerify_Error");
        panel.Children.Add(error);
        ContentDialog dialog = DialogParts.Dialog(Root, "ImgVerifyDialog", Loc.Get("Img_VerifyTitle"), panel, Loc.Get("Img_VerifyStart"));
        void Validate()
        {
            bool badValue = expected.Text.Trim().Length > 0 && ImageVerifier.ParseUserHash(expected.Text) is null;
            DialogParts.MarkInvalid(expected, badValue);
            error.Text = badValue ? Loc.Get("Img_VerifyExpectedInvalid") : string.Empty;
            dialog.IsPrimaryButtonEnabled = !badValue && (md5.IsChecked == true || sha1.IsChecked == true || sha256.IsChecked == true);
        }

        expected.TextChanged += (_, _) => Validate();
        foreach (CheckBox box in new[] { md5, sha1, sha256 })
        {
            box.Click += (_, _) => Validate();
        }

        Validate();
        Task<ContentDialogResult> shown = dialog.ShowQueuedAsync();
        if (interact is not null)
        {
            await interact(dialog);
        }

        if (await shown != ContentDialogResult.Primary)
        {
            return null;
        }

        var algorithms = new List<string>();
        if (md5.IsChecked == true)
        {
            algorithms.Add(ImageHashes.Md5);
        }

        if (sha1.IsChecked == true)
        {
            algorithms.Add(ImageHashes.Sha1);
        }

        if (sha256.IsChecked == true)
        {
            algorithms.Add(ImageHashes.Sha256);
        }

        string identity = data.Identity;
        IReadOnlyDictionary<string, byte[]> evidenceHashes = MergeEvidence(ImageForensics.Hooks.EvidenceHashesOf(path), ImageForensics.Hooks.EvidenceHashesOf(identity));
        IReadOnlyList<ReferenceHash> refs = image is not null
            ? ImageVerifier.FindReferences(image, expected.Text, null, evidenceHashes)
            : DiskReferences(evidenceHashes, expected.Text);
        IByteSource? disk = reread.IsChecked == true ? sourceDisk?.Document.Source : null;
        VerificationResult result;
        try
        {
            result = await Vm.Operations.RunAsync(Loc.Format("Img_VerifyOperation", Path.GetFileName(path)), OperationKind.ReadOnly, null, data.Length,
                op => ImageVerification.RunAsync(data, path, algorithms, refs, op, disk, TestHooks.Time));
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        LastVerification = result;
        await ShowVerificationResultAsync(result);
        return result;
    }

    /// <summary>ディスクの比べる値: 証拠一覧の値と、利用者が入力した値 (FOR-22 の仕様 2。ディスクには格納値・取得ログがない)。</summary>
    private static List<ReferenceHash> DiskReferences(IReadOnlyDictionary<string, byte[]> evidence, string userHex)
    {
        var refs = new List<ReferenceHash>();
        foreach ((string algorithm, byte[] value) in evidence)
        {
            if (ImageHashes.TryNormalize(algorithm, out string a))
            {
                refs.Add(new ReferenceHash(a, value, ReferenceSource.Evidence));
            }
        }

        if (ImageVerifier.ParseUserHash(userHex) is { } user)
        {
            refs.Add(user);
        }

        return refs;
    }

    private static IReadOnlyDictionary<string, byte[]> MergeEvidence(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b)
    {
        var merged = new Dictionary<string, byte[]>(a, StringComparer.Ordinal);
        foreach ((string k, byte[] v) in b)
        {
            merged.TryAdd(k, v);
        }

        return merged;
    }

    /// <summary>取得ログの入力元のパスと同じディスクが開いていれば、そのドキュメント。</summary>
    private DocumentViewModel? SourceDiskOf(IImageSource image)
    {
        string first = image.Info.Files.Count > 0 ? image.Info.Files[0] : string.Empty;
        if (first.Length == 0 || AcquisitionLog.FindFor(first, image.Info.Format) is not { } log)
        {
            return null;
        }

        string? sourcePath = null;
        try
        {
            sourcePath = File.ReadLines(log).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(@"\\.\", StringComparison.Ordinal) || l.Contains(@": \\.\", StringComparison.Ordinal));
        }
        catch (IOException)
        {
        }

        if (sourcePath is null)
        {
            return null;
        }

        string devicePath = sourcePath[sourcePath.IndexOf(@"\\.\", StringComparison.Ordinal)..];
        return Vm.Documents.FirstOrDefault(d => d.Device is { } device && string.Equals(device.Path, devicePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>最後の検証の結果の本文 (テスト用)。</summary>
    internal string? LastVerificationText { get; private set; }

    private async Task ShowVerificationResultAsync(VerificationResult result)
    {
        var lines = new List<string>();
        if (result.ReadErrors.Count > 0)
        {
            lines.Add(Loc.Format("Img_VerifyReadErrors", string.Join(", ", result.ReadErrors.Take(10).Select(r =>
                "0x" + r.Offset.ToString("X", CultureInfo.InvariantCulture) + "+" + r.Length.ToString("N0", CultureInfo.CurrentCulture)))));
        }

        foreach (AlgorithmVerification row in result.Algorithms)
        {
            lines.Add($"{row.Algorithm}: {ImgVerdictText(row.Verdict)}");
            if (row.Computed is { } computed)
            {
                lines.Add("  " + Loc.Format("Img_VerifyComputed", Convert.ToHexStringLower(computed)));
            }

            foreach ((ReferenceHash reference, bool match) in row.Comparisons)
            {
                lines.Add("  " + Loc.Format("Img_VerifyReference", Loc.Get("Img_RefSource_" + reference.Source), Convert.ToHexStringLower(reference.Value),
                    ImgVerdictText(match ? VerificationVerdict.Match : VerificationVerdict.Mismatch)));
            }
        }

        LastVerificationText = string.Join(Environment.NewLine, lines);
        var text = new TextBlock { Text = LastVerificationText, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = DialogParts.Mono, MaxWidth = 560 };
        AutomationProperties.SetAutomationId(text, "ImgVerifyResult_Body");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Format("Img_VerifyResultTitle", ImgVerdictText(result.Overall)),
            Content = new ScrollViewer { Content = text, MaxHeight = 480 },
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ImgVerifyResultDialog");
        await dialog.ShowQueuedAsync();
    }
}
