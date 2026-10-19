using System.Globalization;
using System.Reflection;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Ewf;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// ディスクイメージ (12 の FOR-20〜FOR-26): コマンドの登録、イメージをディスクとして開く (FOR-21)、イメージの情報、Hex ビューの強調
/// (取得時に読めなかったセクタ・取得していない範囲)、チャンクの検査値の不一致の通知。作成・検証は MainWindow.ImagesAcquire.cs、
/// クローン・セクタ範囲は MainWindow.ImagesClone.cs、RAID は MainWindow.ImagesRaid.cs。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>イメージのドキュメントの強調の範囲 (取得時に読めなかったセクタ、取得していない範囲)。</summary>
    private sealed record ImageRegions(IReadOnlyList<(long Offset, long Length)> AcquisitionErrors, IReadOnlyList<(long Offset, long Length)> NotAcquired);

    private readonly Dictionary<DocumentViewModel, ImageRegions> _imageRegions = [];

    /// <summary>書き込み保護モードのときの理由 (FOR-01 の仕様 2: 「書き込み保護モードが有効です」)。</summary>
    private static CommandState ImgNotWhenWriteBlocked() =>
        ImageForensics.Hooks.IsWriteBlocked ? CommandState.Unavailable(Loc.Get("WriteBlock_Enabled")) : CommandState.Available;

    private void RegisterImageCommands()
    {
        Commands.Register("forensics.image.create", () => CreateImageAsync(AcquisitionScope.Full));
        Commands.Register("forensics.image.skeleton", () => CreateImageAsync(AcquisitionScope.Skeleton));
        Commands.Register("forensics.image.open", OpenImageCommandAsync);
        Commands.Register("forensics.image.verify", VerifyImageCommandAsync);
        Commands.Register("forensics.image.info", ShowImageInfoAsync,
            () => NeedsDocument(d => d.Document.Source is IImageSource ? null : Loc.Get("Img_NotImage")));
        Commands.Register("forensics.disk.clone", CloneDiskAsync, NotWhenWriteBlocked);
        Commands.Register("forensics.sectors.save", SaveSectorsAsync, () => NeedsDocument(d => d.Document.Source.LogicalSectorSize > 1 ? null : Loc.Get("Img_NeedsDisk")));
        Commands.Register("forensics.sectors.restore", RestoreSectorsAsync, () =>
            ImageForensics.Hooks.IsWriteBlocked ? CommandState.Unavailable(Loc.Get("WriteBlock_Enabled"))
            : NeedsDocument(d => d.Device is not null ? null : Loc.Get("Img_NeedsDisk")));
        Commands.Register("forensics.raid.reconstruct", ReconstructRaidAsync);
        WriteBlockMode.Changed += OnWriteBlockChangedForImages;
        Closed += (_, _) => WriteBlockMode.Changed -= OnWriteBlockChangedForImages;
        Vm.Documents.CollectionChanged += (_, _) =>
        {
            foreach (DocumentViewModel gone in _imageRegions.Keys.Where(d => !Vm.Documents.Contains(d)).ToList())
            {
                ForgetImageDocument(gone);
            }
        };
    }

    private void OnWriteBlockChangedForImages(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateCommandStates);

    /// <summary>アプリのバージョン (取得ログ・E01 のヘッダーに書く)。</summary>
    internal static string ImageAppVersion =>
        "HexEditor " + (typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    // ---- イメージをディスクとして開く (FOR-21) ----

    private async Task OpenImageCommandAsync()
    {
        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.OpenForensicImage");
        string? path = paths?.FirstOrDefault();
        if (path is null)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.OpenForensicImage" };
            picker.FileTypeFilter.Add("*");
            path = (await picker.PickSingleFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        if (ImageOpener.Detect(path) == ImageFormat.Raw)
        {
            // 1 つのファイルの raw は ENG-31 の「ディスクイメージとして開く」(セクタサイズを選ぶ)。
            await OpenDiskImageAsync(path);
            return;
        }

        await OpenForensicImageAsync(path);
    }

    /// <summary>
    /// 分割 raw・E01・VHD・VHDX・VMDK をイメージとして開く (FOR-21 の仕様 1〜3)。セグメントが足りなければ足りない名前を示し、
    /// 「ある分だけ開く」「キャンセル」を選ばせる。形式が壊れていればどの部分で失敗したかを示す。
    /// </summary>
    internal async Task<DocumentViewModel?> OpenForensicImageAsync(string path, int sectorSize = 512)
    {
        var options = new ImageOpenOptions { SectorSize = sectorSize };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                IImageSource source = await Task.Run(() => ImageOpener.Open(path, options));
                DocumentViewModel doc = Vm.OpenImageSource(source, path);
                AttachImageDocument(doc, source);
                if (source.Info.MissingSegments.Count > 0)
                {
                    ShowNotice(Loc.Format("Img_OpenedPartial", string.Join(", ", source.Info.MissingSegments.Select(Path.GetFileName))),
                        InfoBarSeverity.Warning, doc);
                }

                UpdateTitle();
                return doc;
            }
            catch (MissingSegmentsException ex) when (attempt == 0)
            {
                if (!await ConfirmMissingSegmentsAsync(ex.Missing))
                {
                    return null;
                }

                options = options with { AllowMissingSegments = true };
            }
            catch (ImageFormatException ex)
            {
                ShowNotice(Loc.Format("Img_FormatError", ImgFormatName(ex.Format), ex.Part, ex.Message), InfoBarSeverity.Error);
                return null;
            }
            catch (ParentImageNotFoundException ex)
            {
                ShowNotice(Loc.Format("Img_ParentNotFound", ex.ParentHint), InfoBarSeverity.Error);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowNotice(Loc.Format("Error_Open", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
                return null;
            }
        }

        return null;
    }

    /// <summary>最後に出した「セグメントが足りない」の本文 (テスト用)。</summary>
    internal string? LastMissingSegmentsText { get; private set; }

    private async Task<bool> ConfirmMissingSegmentsAsync(IReadOnlyList<string> missing)
    {
        string body = Loc.Format("Img_MissingSegments", string.Join(", ", missing.Select(Path.GetFileName)));
        LastMissingSegmentsText = body;
        var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        AutomationProperties.SetAutomationId(text, "ImgMissing_Body");
        return await ConfirmAsync(Loc.Get("Img_MissingSegmentsTitle"), text, Loc.Get("Img_OpenAvailable"), "ImgMissingSegmentsDialog");
    }

    internal static string ImgFormatName(ImageFormat format) => format switch
    {
        ImageFormat.E01 => "E01",
        ImageFormat.SplitRaw => Loc.Get("Img_Format_SplitRaw"),
        ImageFormat.Vhd => "VHD",
        ImageFormat.Vhdx => "VHDX",
        ImageFormat.Vmdk => "VMDK",
        _ => "raw",
    };

    /// <summary>
    /// 開いたイメージのドキュメントの準備: error2 の範囲 (FOR-21 の仕様 5) と、<c>.ranges.csv</c> の含めなかった範囲 (FOR-23 の仕様 5) の強調、
    /// チャンクの検査値の不一致の通知 (FOR-21 の仕様 6)。
    /// </summary>
    private void AttachImageDocument(DocumentViewModel doc, IImageSource source)
    {
        int sector = source.LogicalSectorSize;
        var errors = source.Info.AcquisitionErrors.Select(r => (r.StartSector * sector, r.SectorCount * sector)).ToList();
        IReadOnlyList<(long, long)> notAcquired = [];
        string first = source.Info.Files.Count > 0 ? source.Info.Files[0] : string.Empty;
        try
        {
            if (first.Length > 0 && RangesCsv.FindFor(first, source.Info.Format) is { } csv)
            {
                notAcquired = RangesCsv.NotIncluded(RangesCsv.Parse(File.ReadAllText(csv)), sector, source.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"ranges.csv not read: {ex.Message}");
        }

        _imageRegions[doc] = new ImageRegions(errors, notAcquired);
        source.CorruptionDetected += (_, range) => DispatcherQueue.TryEnqueue(() =>
        {
            if (Vm.Documents.Contains(doc))
            {
                string text = Loc.Format("Img_ChunkCorrupt", "0x" + range.Offset.ToString("X", CultureInfo.InvariantCulture),
                    range.Length.ToString("N0", CultureInfo.CurrentCulture));
                ShowStatusMessage(text);
                ShowNotice(text, InfoBarSeverity.Warning, doc);
            }
        });
        foreach (HexView view in _views)
        {
            view.RefreshHighlights();
        }
    }

    /// <summary>Hex ビューにイメージの強調を付ける (AttachAnnotations から呼ぶ)。</summary>
    private void AttachImageHighlights(HexView view) => view.SetHighlightSource("image", (start, end) => ImageHighlights(view, start, end));

    private IEnumerable<HexHighlight> ImageHighlights(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } doc || !_imageRegions.TryGetValue(doc, out ImageRegions? regions))
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        foreach ((long offset, long length) in regions.AcquisitionErrors)
        {
            if (offset < end && start < offset + length)
            {
                yield return new HexHighlight(offset, length, CellLayer.Annotation, CompareBrushes.Background(Core.Compare.DiffKind.Unreadable, view, hc, null),
                    null, null, "image:acquisitionError", Mark: HexMark.Hatch, MarkBrush: CompareBrushes.Mark(Core.Compare.DiffKind.Unreadable, view, hc));
            }
        }

        foreach ((long offset, long length) in regions.NotAcquired)
        {
            if (offset < end && start < offset + length)
            {
                yield return new HexHighlight(offset, length, CellLayer.Annotation, CompareBrushes.Padding(view, hc), null, null, "image:notAcquired",
                    Mark: HexMark.Hatch, MarkBrush: CompareBrushes.PaddingMark(view, hc));
            }
        }
    }

    /// <summary>閉じたドキュメントの強調の記録を外す。</summary>
    private void ForgetImageDocument(DocumentViewModel doc) => _imageRegions.Remove(doc);

    // ---- イメージの情報 (FOR-21 の仕様 5) ----

    /// <summary>「イメージの情報」の項目 (名前と値)。</summary>
    internal static List<(string Label, string Value)> ImageInfoRows(IImageSource source)
    {
        ImageInfo i = source.Info;
        var rows = new List<(string, string)>
        {
            (Loc.Get("Img_Info_Format"), ImgFormatName(i.Format) + (i.Subtype is { } sub ? " (" + sub + ")" : string.Empty)),
            (Loc.Get("Img_Info_Files"), string.Join(Environment.NewLine, i.Files)),
            (Loc.Get("Img_Info_Size"), Loc.Format("Img_Info_SizeValue", i.MediaSize.ToString("N0", CultureInfo.CurrentCulture),
                (i.MediaSize / Math.Max(1, i.BytesPerSector)).ToString("N0", CultureInfo.CurrentCulture))),
            (Loc.Get("Img_Info_SectorSize"), i.BytesPerSector.ToString(CultureInfo.InvariantCulture)),
        };
        if (i.Case is { } c)
        {
            rows.Add((Loc.Get("Img_Case_Number"), c.CaseNumber));
            rows.Add((Loc.Get("Img_Case_Evidence"), c.EvidenceNumber));
            rows.Add((Loc.Get("Img_Case_Examiner"), c.Examiner));
            rows.Add((Loc.Get("Img_Case_Description"), c.Description));
            rows.Add((Loc.Get("Img_Case_Notes"), c.Notes));
        }

        if (i.AcquiredAt is { } at)
        {
            rows.Add((Loc.Get("Img_Info_Acquired"), AcquisitionLog.FormatTime(at, ImageForensics.Hooks.Case?.TimeZone)));
        }

        if (i.AcquisitionTool is { } tool)
        {
            rows.Add((Loc.Get("Img_Info_Tool"), tool + (i.AcquisitionOs is { } os ? " / " + os : string.Empty)));
        }

        if (i.Model is not null || i.SerialNumber is not null)
        {
            rows.Add((Loc.Get("Img_Info_Device"), $"{i.Model} {i.SerialNumber}".Trim()));
        }

        if (i.ChunkCount is { } chunks)
        {
            rows.Add((Loc.Get("Img_Info_Chunks"), Loc.Format("Img_Info_ChunksValue", chunks.ToString("N0", CultureInfo.CurrentCulture),
                (i.SectorsPerChunk ?? 0).ToString(CultureInfo.InvariantCulture))));
        }

        if (i.Compressed is { } compressed)
        {
            rows.Add((Loc.Get("Img_Info_Compression"), compressed ? Loc.Get("Img_Info_Compressed") + (i.CompressionLevel is { } l ? " (" + l + ")" : string.Empty)
                : Loc.Get("Img_Info_NotCompressed")));
        }

        foreach ((string algorithm, byte[] value) in i.StoredHashes)
        {
            rows.Add((Loc.Format("Img_Info_StoredHash", algorithm), Convert.ToHexStringLower(value)));
        }

        if (i.AcquisitionErrors.Count > 0)
        {
            rows.Add((Loc.Get("Img_Info_Errors"), string.Join(", ", i.AcquisitionErrors.Take(20).Select(r =>
                Loc.Format("Img_SectorRange", r.StartSector.ToString(CultureInfo.InvariantCulture), r.SectorCount.ToString(CultureInfo.InvariantCulture))))));
        }

        if (i.ParentPath is { } parent)
        {
            rows.Add((Loc.Get("Img_Info_Parent"), parent));
        }

        if (i.MissingSegments.Count > 0)
        {
            rows.Add((Loc.Get("Img_Info_Missing"), string.Join(", ", i.MissingSegments.Select(Path.GetFileName))));
        }

        return rows;
    }

    private async Task ShowImageInfoAsync()
    {
        if (Vm.Selected?.Document.Source is not IImageSource source)
        {
            return;
        }

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 4, MaxWidth = 560 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach ((string label, string value) in ImageInfoRows(source))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = DialogParts.Mono };
            AutomationProperties.SetName(text, label);
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            row++;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("Img_InfoTitle"),
            Content = new ScrollViewer { Content = grid, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ImgInfoDialog");
        await dialog.ShowQueuedAsync();
    }

    // ---- 共通の部品 ----

    /// <summary>大きさの入力 ("100M"、"2G"、"512K"、"1048576")。K・M・G・T は 1024 の累乗。読めなければ null。</summary>
    internal static long? ImgParseSize(string text)
    {
        string t = text.Trim().ToUpperInvariant().Replace("IB", string.Empty, StringComparison.Ordinal).Replace("B", string.Empty, StringComparison.Ordinal);
        if (t.Length == 0)
        {
            return null;
        }

        long multiplier = t[^1] switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, 'T' => 1L << 40, _ => 1 };
        string number = multiplier == 1 ? t : t[..^1];
        return long.TryParse(number.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n > 0 && n <= long.MaxValue / multiplier
            ? n * multiplier
            : null;
    }

    /// <summary>取得ログの見出し (表示言語の文言)。</summary>
    internal static AcquisitionLogText ImgLogText() => new()
    {
        Title = Loc.Get("ImgLog_Title"),
        CloneTitle = Loc.Get("ImgLog_CloneTitle"),
        Status = Loc.Get("ImgLog_Status"),
        StatusCompleted = Loc.Get("ImgLog_Completed"),
        StatusCancelled = Loc.Get("ImgLog_Cancelled"),
        StatusSourceLost = Loc.Get("ImgLog_SourceLost"),
        StatusFailed = Loc.Get("ImgLog_Failed"),
        Source = Loc.Get("ImgLog_Source"),
        Path = Loc.Get("ImgLog_Path"),
        Model = Loc.Get("ImgLog_Model"),
        Serial = Loc.Get("ImgLog_Serial"),
        Capacity = Loc.Get("ImgLog_Capacity"),
        SectorSize = Loc.Get("ImgLog_SectorSize"),
        Range = Loc.Get("ImgLog_Range"),
        Output = Loc.Get("ImgLog_Output"),
        Format = Loc.Get("ImgLog_Format"),
        Files = Loc.Get("ImgLog_Files"),
        Target = Loc.Get("ImgLog_Target"),
        Started = Loc.Get("ImgLog_Started"),
        Ended = Loc.Get("ImgLog_Ended"),
        SectorsRead = Loc.Get("ImgLog_SectorsRead"),
        UnreadableSectors = Loc.Get("ImgLog_Unreadable"),
        None = Loc.Get("ImgLog_None"),
        Hashes = Loc.Get("ImgLog_Hashes"),
        IncludedHashes = Loc.Get("ImgLog_IncludedHashes"),
        Verification = Loc.Get("ImgLog_Verification"),
        VerificationMatch = Loc.Get("Img_Verdict_Match"),
        VerificationMismatch = Loc.Get("Img_Verdict_Mismatch"),
        VerificationNoReference = Loc.Get("Img_Verdict_NoReference"),
        VerificationReadError = Loc.Get("Img_Verdict_ReadError"),
        VerificationSkipped = Loc.Get("ImgLog_NotVerified"),
        AppVersion = Loc.Get("ImgLog_AppVersion"),
        Case = Loc.Get("ImgLog_Case"),
        CaseNumber = Loc.Get("Img_Case_Number"),
        EvidenceNumber = Loc.Get("Img_Case_Evidence"),
        Examiner = Loc.Get("Img_Case_Examiner"),
        Description = Loc.Get("Img_Case_Description"),
        Notes = Loc.Get("Img_Case_Notes"),
        Scope = Loc.Get("ImgLog_Scope"),
        ScopeFull = Loc.Get("ImgLog_ScopeFull"),
        ScopeSparse = Loc.Get("ImgLog_ScopeSparse"),
        ScopeSkeleton = Loc.Get("ImgLog_ScopeSkeleton"),
        ScopeNote = Loc.Get("ImgLog_ScopeNote"),
        RangesFile = Loc.Get("ImgLog_RangesFile"),
        Message = Loc.Get("ImgLog_Message"),
    };

    internal static string ImgVerdictText(VerificationVerdict verdict) => Loc.Get("Img_Verdict_" + verdict);

    /// <summary>入力元の説明 (取得ログ・監査ログ)。ディスクならモデル名・シリアル番号・物理ディスク番号。</summary>
    internal static AcquisitionSourceInfo ImgSourceInfoOf(DocumentViewModel doc)
    {
        Core.Sources.IByteSource source = doc.Document.Source;
        if (doc.Device is { } device)
        {
            return new AcquisitionSourceInfo
            {
                Path = device.Path,
                DisplayName = device.DisplayName,
                Identity = device.Identity,
                Model = device.Info.Disk?.Model,
                SerialNumber = device.Info.SerialNumber ?? device.Info.Disk?.SerialNumber,
                Capacity = device.Length,
                SectorSize = device.LogicalSectorSize,
                Physical = device.Info.Disk is not null,
                DiskNumber = device.Info.Disk?.Number ?? device.Info.Volume?.DiskNumber,
            };
        }

        string path = doc.FilePath ?? (source is ImageSourceBase image ? image.PrimaryPath : doc.DisplayName);
        return new AcquisitionSourceInfo
        {
            Path = path,
            DisplayName = doc.DisplayName,
            Identity = source.Identity,
            Model = source is IImageSource img ? img.Info.Model : null,
            SerialNumber = source is IImageSource img2 ? img2.Info.SerialNumber : null,
            Capacity = source.Length,
            SectorSize = Math.Max(1, source.LogicalSectorSize) is var s && s > 1 ? s : 512,
        };
    }

    private static Border ImgSection(string header, UIElement content)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = header, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(content);
        return new Border { Child = panel, Padding = new Thickness(0, 4, 0, 4) };
    }

    private static TextBlock ImgNote(string automationId, string text, bool warning = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        if (warning)
        {
            block.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        }

        AutomationProperties.SetAutomationId(block, automationId);
        return block;
    }
}
