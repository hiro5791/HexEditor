using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// データ > 圧縮… / 展開… / 圧縮データの範囲を選択 (EDIT-41)。圧縮・展開は Core の <see cref="CompressionEngine"/> で、1 MiB 単位のストリーム処理。
/// 結果は追加データ (一時ファイル) として作り、完了後に 1 回で反映する (選択範囲を置き換える) か、派生ドキュメント (EDIT-46) の新しいタブで開く。
/// </summary>
public sealed partial class MainWindow
{
    private const string CompressStateKey = "data.compress.dialog";
    private const string DecompressStateKey = "data.decompress.dialog";

    /// <summary>長時間処理にする大きさ (EDIT-41 の「巨大ファイル」)。</summary>
    private const long CodecLongRunning = 4L * 1024 * 1024;

    /// <summary>テスト用: 開いている暗号化・圧縮のダイアログの状態と、欄に値を入れる処理。</summary>
    private Func<JsonObject>? _codecDialogState;

    private Action<string, string>? _codecDialogSet;

    internal JsonObject CodecDialogTestModel() => _codecDialogState?.Invoke() ?? new JsonObject { ["open"] = false };

    internal void CodecDialogTestSet(string field, string value) => _codecDialogSet?.Invoke(field, value);

    private void RegisterDataCodecCommands()
    {
        Commands.Register("data.encrypt", Guard(() => CipherAsync(encrypt: true)), () => NeedsDocument());
        Commands.Register("data.decrypt", Guard(() => CipherAsync(encrypt: false)), () => NeedsDocument());
        Commands.Register("data.compress", Guard(CompressAsync), () => NeedsDocument());
        Commands.Register("data.decompress", Guard(() => DecompressAsync(null)), () => NeedsDocument());
        Commands.Register("data.selectCompressed", Guard(SelectCompressedAsync), () => NeedsDocument());
        Commands.Register("data.derived.writeBack", Guard(WriteBackAsync),
            () => NeedsDocument(d => d.Derived is null ? Loc.Get("Derived_NotDerived") : d.Derived.IsDetached ? Loc.Get("Derived_ParentClosed") : null));
    }

    /// <summary>想定外の失敗 (ライブラリの誤りなど) を InfoBar で示し、アプリを止めない。</summary>
    private Func<Task> Guard(Func<Task> body) => async () =>
    {
        try
        {
            await body();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException
            or ArgumentException or IOException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            AppLog.Warning($"Data codec command failed: {ex.GetType().Name}");
            ShowNotice(Loc.Format("Codec_Error_Failed", ex.Message), InfoBarSeverity.Error, Vm.Selected);
        }
    };

    internal static string CompressionFormatName(CompressionFormat format) => Loc.Get("Compression_Format_" + format);

    /// <summary>この形式を使えるか (XZ・LZMA は liblzma を読み込めること)。</summary>
    private static bool FormatAvailable(CompressionFormat format) =>
        format is not (CompressionFormat.Xz or CompressionFormat.Lzma) || s_lzmaAvailable.Value;

    private static readonly Lazy<bool> s_lzmaAvailable = new(() => CompressionFormats.LzmaAvailable());

    /// <summary>
    /// 対象の範囲: 選択範囲 (なければドキュメント全体)。マルチ選択・矩形選択は使えない (null。InfoBar で示す)。
    /// </summary>
    private (long Start, long Length)? CodecTarget(DocumentViewModel doc)
    {
        if (doc.Editor.HasMultipleRanges)
        {
            ShowNotice(Loc.Get("Codec_Error_MultipleRanges"), InfoBarSeverity.Error, doc);
            return null;
        }

        return doc.Editor.HasSelection ? (doc.Editor.SelectionStart, doc.Editor.SelectionLength) : (0, doc.Document.Length);
    }

    /// <summary>「選択範囲を置き換える」を使えるか (読み取り専用・処理中・長さを変えられない場合は「新しいタブで開く」だけ)。</summary>
    private static bool CanReplace(DocumentViewModel doc, bool changesLength) =>
        !doc.Document.IsReadOnly && !doc.Document.IsEditLocked && (!changesLength || doc.Document.CanResize);

    // ---- 圧縮 ----

    private sealed record CompressRequest(CompressionOptions Options, bool NewTab);

    private async Task CompressAsync()
    {
        if (Vm.Selected is not { } doc || CodecTarget(doc) is not var (start, length))
        {
            return;
        }

        if (await ShowCompressDialogAsync(doc, length) is not { } request)
        {
            return;
        }

        if (request.Options.Format == CompressionFormat.Lz4Block && length > CompressionFormatInfo.Lz4BlockCompressLimit)
        {
            ShowNotice(Loc.Format("Compress_Error_Lz4BlockLimit", GigaBytes(CompressionFormatInfo.Lz4BlockCompressLimit)), InfoBarSeverity.Error, doc);
            return;
        }

        string name = Loc.Get("Operation_Compress");
        DocumentSnapshot snapshot = doc.Document.Current;
        Document? target = request.NewTab ? Vm.CreateDerivedDocument(doc.DisplayName) : null;
        Document sinkOwner = target ?? doc.Document;
        EditContent? content = await RunTransformAsync(doc, name, length, length > CodecLongRunning,
            op => CompressionEngine.Compress(snapshot, start, length, request.Options, ContentSink.For(sinkOwner, CheckTempSpace(sinkOwner)), op));
        if (content is null)
        {
            target?.Dispose();
            return;
        }

        if (target is not null)
        {
            // 圧縮の結果を新しいタブで開く (派生ドキュメントではない通常の新規ドキュメント)。
            target.InsertContent(0, content, name);
            target.ClearHistory();
            DocumentViewModel vm = Vm.AddDerived(target, doc.DisplayName, doc);
            vm.RangeLabel = DerivedRangeLabel(start, length, Loc.Get("Derived_Label_Compressed"));
            Vm.Selected = vm;
            return;
        }

        ApplyCodecResult(doc, start, length, content, "圧縮");
    }

    /// <summary>結果で範囲を置き換え (1 つの編集グループ)、結果を選択する。</summary>
    private void ApplyCodecResult(DocumentViewModel doc, long start, long length, EditContent content, string description)
    {
        long resultLength = content.Length;
        if (!EnsureEditable(doc))
        {
            content.Dispose();
            return;
        }

        if (!ApplyEdit(doc, () => TransformApplier.Apply(doc.Document, [new RangeReplacement(new TargetRange(start, length), content)], description)))
        {
            content.Dispose();
            return;
        }

        if (resultLength > 0)
        {
            doc.Editor.Select(start, resultLength);
        }

        FocusEditor();
    }

    private async Task<CompressRequest?> ShowCompressDialogAsync(DocumentViewModel doc, long length)
    {
        var formats = CompressionFormats.All.Select(f => f.Format).ToList();
        ComboBox format = ItemCombo("Compress_Format", Loc.Get("Compress_Format"), formats.Select(CompressionFormatName), 0);
        for (int i = 0; i < formats.Count; i++)
        {
            if (!FormatAvailable(formats[i]) && format.Items[i] is ComboBoxItem item)
            {
                item.IsEnabled = false;
            }
        }

        var level = new NumberBox
        {
            Header = Loc.Get("Compress_Level"),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = 1,
            MinWidth = 160,
        };
        AutomationProperties.SetAutomationId(level, "Compress_Level");
        AutomationProperties.SetName(level, Loc.Get("Compress_Level"));
        TextBlock levelRange = DialogParts.Caption("Compress_LevelRange");
        CheckBox gzipHeader = DialogParts.Check("Compress_GzipHeader", Loc.Get("Compress_GzipHeader"), false);
        RadioButton replace = DialogParts.Radio("Codec_DestReplace", Loc.Get("Codec_DestReplace"), "CodecDest", true);
        RadioButton newTab = DialogParts.Radio("Codec_DestNewTab", Loc.Get("Codec_DestNewTab"), "CodecDest", false);
        TextBlock note = DialogParts.Caption("Codec_DestNote");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (UIElement e in new UIElement[] { format, level, levelRange, gzipHeader, new TextBlock { Text = Loc.Get("Codec_Destination") }, replace, newTab, note })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "CompressDialog", Loc.Get("Compress_Title"), body, Loc.Get("Compress_Run"));

        // 前回の選択 (形式・レベル・gzip のヘッダ・書き込み先)。
        JsonObject state = LoadDialogState(CompressStateKey);
        if (state["format"]?.GetValue<string>() is { } saved && Enum.TryParse(saved, out CompressionFormat f) && FormatAvailable(f))
        {
            format.SelectedIndex = formats.IndexOf(f);
        }

        bool canReplace = CanReplace(doc, changesLength: true);
        replace.IsEnabled = canReplace;
        bool wantNewTab = state["newTab"]?.GetValue<bool>() ?? false;
        replace.IsChecked = canReplace && !wantNewTab;
        newTab.IsChecked = !canReplace || wantNewTab;
        gzipHeader.IsChecked = state["gzipHeader"]?.GetValue<bool>() ?? false;
        note.Text = canReplace ? string.Empty : Loc.Get(doc.Document.IsReadOnly ? "Codec_DestReadOnly" : "Codec_DestFixedLength");
        int? savedLevel = state["level"]?.GetValue<int>();
        CompressionFormat? levelFor = null;
        void Update()
        {
            CompressionFormat current = formats[Math.Max(0, format.SelectedIndex)];
            CompressionFormatInfo info = CompressionFormats.Info(current);
            if (levelFor != current)
            {
                level.Minimum = info.MinLevel;
                level.Maximum = info.MaxLevel;
                level.Value = levelFor is null && savedLevel is int l && l >= info.MinLevel && l <= info.MaxLevel ? l : info.DefaultLevel;
                levelFor = current;
            }

            levelRange.Text = Loc.Format("Compress_LevelRangeText", info.MinLevel, info.MaxLevel, info.DefaultLevel);
            gzipHeader.Visibility = current == CompressionFormat.Gzip ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = !double.IsNaN(level.Value) && FormatAvailable(current);
        }

        format.SelectionChanged += (_, _) => Update();
        level.ValueChanged += (_, _) => Update();
        Update();
        _codecDialogState = () => new JsonObject
        {
            ["open"] = true,
            ["kind"] = "compress",
            ["format"] = formats[Math.Max(0, format.SelectedIndex)].ToString(),
            ["level"] = double.IsNaN(level.Value) ? null : (int)level.Value,
            ["minLevel"] = (int)level.Minimum,
            ["maxLevel"] = (int)level.Maximum,
            ["newTab"] = newTab.IsChecked == true,
            ["replaceEnabled"] = replace.IsEnabled,
        };
        _codecDialogSet = (field, value) =>
        {
            switch (field)
            {
                case "format":
                    format.SelectedIndex = formats.IndexOf(Enum.Parse<CompressionFormat>(value));
                    break;
                case "level":
                    level.Value = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "newTab":
                    (bool.Parse(value) ? newTab : replace).IsChecked = true;
                    break;
                case "gzipHeader":
                    gzipHeader.IsChecked = bool.Parse(value);
                    break;
            }
        };
        ContentDialogResult result;
        try
        {
            result = await ShowEditDialogAsync(dialog);
        }
        finally
        {
            _codecDialogState = null;
            _codecDialogSet = null;
        }

        if (result != ContentDialogResult.Primary || double.IsNaN(level.Value))
        {
            return null;
        }

        CompressionFormat chosen = formats[Math.Max(0, format.SelectedIndex)];
        int levelValue = (int)level.Value;
        bool header = chosen == CompressionFormat.Gzip && gzipHeader.IsChecked == true;
        SaveDialogState(CompressStateKey, new JsonObject
        {
            ["format"] = chosen.ToString(),
            ["level"] = levelValue,
            ["gzipHeader"] = gzipHeader.IsChecked == true,
            ["newTab"] = newTab.IsChecked == true,
        });
        var options = new CompressionOptions(chosen, levelValue)
        {
            GzipFileName = header ? doc.DisplayName : null,
            GzipModified = header && doc.FilePath is { } path && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : header ? DateTimeOffset.UtcNow : null,
        };
        return new CompressRequest(options, newTab.IsChecked == true);
    }

    // ---- 展開 ----

    private sealed record DecompressRequest(CompressionFormat? Format, bool NewTab);

    /// <summary>
    /// 「ここから展開...」(ANA-16 の仕様 5・ANA-34 の仕様 6): 範囲 [start, start + length) を選択し、形式を入れた状態で展開の画面を開く。
    /// </summary>
    internal Task DecompressFromAsync(long start, long length, CompressionFormat? format)
    {
        if (Vm.Selected is not { } doc)
        {
            return Task.CompletedTask;
        }

        doc.Editor.Select(start, Math.Max(1, Math.Min(length, doc.Document.Length - start)));
        _decompressPreset = format;
        return DecompressAsync(null);
    }

    /// <summary>次に開く展開の画面の形式 (「ここから展開...」)。</summary>
    private CompressionFormat? _decompressPreset;

    /// <param name="undetected">自動判別できなかったときに、形式の選択を求めて開き直す (仕様 3)。</param>
    private async Task DecompressAsync(DecompressRequest? undetected)
    {
        if (Vm.Selected is not { } doc || CodecTarget(doc) is not var (start, length))
        {
            return;
        }

        if (await ShowDecompressDialogAsync(doc, start, length, undetected) is not { } request)
        {
            return;
        }

        string name = Loc.Get("Operation_Decompress");
        DocumentSnapshot snapshot = doc.Document.Current;
        Document? target = request.NewTab ? Vm.CreateDerivedDocument(doc.DisplayName) : null;
        Document sinkOwner = target ?? doc.Document;
        long limit = (long)Math.Clamp(App.Settings?.GetInt(CodecSettings.DecompressConfirmKey, CodecSettings.DefaultDecompressConfirmMiB)
            ?? CodecSettings.DefaultDecompressConfirmMiB, 1, CodecSettings.MaxDecompressConfirmMiB) * 1024 * 1024;
        CodecOutput NewOutput() => new(ContentSink.For(sinkOwner, CheckTempSpace(sinkOwner)))
        {
            Limit = limit,
            LimitReached = written => AskDecompressLimit(limit, length),
        };

        DecompressionResult? result;
        bool detected = true;
        try
        {
            // 展開は入力が小さくても結果が大きくなりうるため、常に長時間処理として実行する (0.5 秒を超えたら処理センターに出る)。
            result = await RunTransformAsync(doc, name, length, longRunning: true, op =>
            {
                if (request.Format is { } f)
                {
                    return CompressionEngine.Decompress(snapshot, start, length, f, NewOutput(), op);
                }

                DecompressionResult? r = CompressionEngine.DecompressAuto(snapshot, start, length, NewOutput, op);
                detected = r is not null;
                return r ?? new DecompressionResult(CompressionFormat.Deflate, 0, 0, null, false);
            });
        }
        catch (CompressedDataException e)
        {
            target?.Dispose();
            await ShowCorruptDataAsync(doc, start, e);
            return;
        }

        if (result is null)
        {
            target?.Dispose();
            return;
        }

        if (!detected)
        {
            // 形式を自動判別できなかった: 同じダイアログで形式の選択を求める (選ぶまで展開しない)。
            target?.Dispose();
            await DecompressAsync(request);
            return;
        }

        if (result.ChecksumMismatch && !await ConfirmChecksumMismatchAsync(result.Format))
        {
            result.Content?.Dispose();
            target?.Dispose();
            return;
        }

        EditContent content = result.Content!;
        long remaining = length - result.ConsumedInput;
        if (target is not null)
        {
            var transform = new DerivedTransform
            {
                Kind = DerivedKind.Decompressed,
                Format = result.Format,
                Level = CompressionFormats.Info(result.Format).DefaultLevel,
            };
            OpenDerivedDocument(doc, target, content, start, result.ConsumedInput, transform);
        }
        else
        {
            ApplyCodecResult(doc, start, result.ConsumedInput, content, "展開");
        }

        if (remaining > 0)
        {
            // 圧縮データの終わりで止まった (仕様 4)。残りのデータは変えない。
            ShowNotice(Loc.Format("Decompress_Trailing", StatusFormat.Number(result.ConsumedInput, Culture), StatusFormat.Number(remaining, Culture)),
                InfoBarSeverity.Informational, Vm.Selected ?? doc);
        }
    }

    private async Task<DecompressRequest?> ShowDecompressDialogAsync(DocumentViewModel doc, long start, long length, DecompressRequest? undetected)
    {
        var formats = CompressionFormats.All.Select(f => f.Format).ToList();
        ComboBox format = ItemCombo("Decompress_Format", Loc.Get("Decompress_Format"),
            [Loc.Get("Decompress_Auto"), .. formats.Select(CompressionFormatName)], 0);
        for (int i = 0; i < formats.Count; i++)
        {
            if (!FormatAvailable(formats[i]) && format.Items[i + 1] is ComboBoxItem item)
            {
                item.IsEnabled = false;
            }
        }

        TextBlock detection = DialogParts.Caption("Decompress_Detection");
        detection.TextWrapping = TextWrapping.Wrap;
        RadioButton replace = DialogParts.Radio("Codec_DestReplace", Loc.Get("Codec_DestReplace"), "CodecDest", false);
        RadioButton newTab = DialogParts.Radio("Codec_DestNewTab", Loc.Get("Codec_DestNewTab"), "CodecDest", true);
        TextBlock note = DialogParts.Caption("Codec_DestNote");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (UIElement e in new UIElement[] { format, detection, new TextBlock { Text = Loc.Get("Codec_Destination") }, replace, newTab, note })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "DecompressDialog", Loc.Get("Decompress_Title"), body, Loc.Get("Decompress_Run"));
        bool canReplace = CanReplace(doc, changesLength: true);
        replace.IsEnabled = canReplace;
        JsonObject state = LoadDialogState(DecompressStateKey);
        bool wantReplace = undetected is { NewTab: false } || (undetected is null && state["newTab"]?.GetValue<bool>() == false);
        replace.IsChecked = canReplace && wantReplace;
        newTab.IsChecked = !(canReplace && wantReplace);
        note.Text = canReplace ? string.Empty : Loc.Get(doc.Document.IsReadOnly ? "Codec_DestReadOnly" : "Codec_DestFixedLength");

        // 自動判別の手がかり (仕様 3)。判別できなかった後は「自動判別」を選べない。
        IReadOnlyList<CompressionFormat> clues;
        try
        {
            clues = CompressionFormats.Detect(RangeReader.Head(doc.Document.Current, start, length, 16));
        }
        catch (DataReadException)
        {
            clues = [];
        }

        if (undetected is not null && format.Items[0] is ComboBoxItem auto)
        {
            auto.IsEnabled = false;
            format.SelectedIndex = -1;
        }

        void Update()
        {
            bool autoSelected = format.SelectedIndex == 0;
            detection.Text = undetected is not null ? Loc.Get("Decompress_Undetected")
                : autoSelected ? (clues.Count > 0 ? Loc.Format("Decompress_Clue", CompressionFormatName(clues[0])) : Loc.Get("Decompress_NoClue"))
                : string.Empty;
            dialog.IsPrimaryButtonEnabled = format.SelectedIndex >= 0 && (format.SelectedIndex == 0 || FormatAvailable(formats[format.SelectedIndex - 1]));
        }

        if (_decompressPreset is { } preset && formats.IndexOf(preset) is int presetIndex and >= 0)
        {
            format.SelectedIndex = presetIndex + 1;
        }

        _decompressPreset = null;
        format.SelectionChanged += (_, _) => Update();
        Update();
        _codecDialogState = () => new JsonObject
        {
            ["open"] = true,
            ["kind"] = "decompress",
            ["start"] = start,
            ["length"] = length,
            ["format"] = format.SelectedIndex switch { < 0 => null, 0 => "Auto", int i => formats[i - 1].ToString() },
            ["detection"] = detection.Text,
            ["autoEnabled"] = format.Items[0] is ComboBoxItem a && a.IsEnabled,
            ["newTab"] = newTab.IsChecked == true,
            ["replaceEnabled"] = replace.IsEnabled,
            ["runEnabled"] = dialog.IsPrimaryButtonEnabled,
        };
        _codecDialogSet = (field, value) =>
        {
            switch (field)
            {
                case "format":
                    format.SelectedIndex = value == "Auto" ? 0 : formats.IndexOf(Enum.Parse<CompressionFormat>(value)) + 1;
                    break;
                case "newTab":
                    (bool.Parse(value) ? newTab : replace).IsChecked = true;
                    break;
            }
        };
        ContentDialogResult result;
        try
        {
            result = await ShowEditDialogAsync(dialog);
        }
        finally
        {
            _codecDialogState = null;
            _codecDialogSet = null;
        }

        if (result != ContentDialogResult.Primary || format.SelectedIndex < 0)
        {
            return null;
        }

        SaveDialogState(DecompressStateKey, new JsonObject { ["newTab"] = newTab.IsChecked == true });
        return new DecompressRequest(format.SelectedIndex == 0 ? null : formats[format.SelectedIndex - 1], newTab.IsChecked == true);
    }

    /// <summary>
    /// 展開後の大きさが確認の大きさを超えた (仕様 7)。処理のスレッドから呼ばれ、UI スレッドで確認ダイアログを出して答えを待つ。
    /// </summary>
    private bool AskDecompressLimit(long limit, long inputLength)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                string size = limit % (1024L * 1024 * 1024) == 0
                    ? Loc.Format("Decompress_LimitGiB", limit / (1024L * 1024 * 1024))
                    : Loc.Format("Decompress_LimitMiB", limit / (1024 * 1024));
                ContentDialog dialog = DialogParts.Dialog(Root, "DecompressLimitDialog", Loc.Get("Decompress_LimitTitle"),
                    new TextBlock
                    {
                        Text = Loc.Format("Decompress_LimitBody", size, (inputLength / 1e6).ToString("N1", Culture)),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420,
                    }, Loc.Get("Decompress_LimitContinue"));
                dialog.CloseButtonText = Loc.Get("Decompress_LimitAbort");
                dialog.DefaultButton = ContentDialogButton.Close;
                answer.TrySetResult(await dialog.ShowQueuedAsync() == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Decompress limit dialog failed: {ex.Message}");
                answer.TrySetResult(false);
            }
        });
        return queued && answer.Task.GetAwaiter().GetResult();
    }

    private async Task<bool> ConfirmChecksumMismatchAsync(CompressionFormat format)
    {
        ContentDialog dialog = DialogParts.Dialog(Root, "DecompressChecksumDialog", Loc.Get("Decompress_Title"),
            new TextBlock { Text = Loc.Format("Decompress_ChecksumMismatch", CompressionFormatName(format)), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            Loc.Get("Decompress_UseResult"));
        dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// 展開時のデータ破損 (「エラー」): オフセットを示し、それまでに展開できた分を新しいタブで開くかを選ばせる。
    /// </summary>
    private async Task ShowCorruptDataAsync(DocumentViewModel doc, long start, CompressedDataException e)
    {
        string offset = StatusFormat.Hex(start + e.InputOffset);
        string message = e.Error switch
        {
            CompressedDataError.Truncated => Loc.Format("Decompress_Truncated", offset),
            CompressedDataError.Unsupported => Loc.Format("Decompress_Unsupported", e.Detail ?? string.Empty),
            CompressedDataError.MemoryLimit => Loc.Get("Decompress_MemoryLimit"),
            _ => Loc.Format("Decompress_Corrupt", offset),
        };
        if (e.Partial is not { } partial)
        {
            ShowNotice(message, InfoBarSeverity.Error, doc);
            return;
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "DecompressCorruptDialog", Loc.Get("Decompress_Title"),
            new TextBlock
            {
                Text = message + Environment.NewLine + Loc.Format("Decompress_PartialAvailable", StatusFormat.Number(e.PartialLength, Culture)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            }, Loc.Get("Decompress_OpenPartial"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            partial.Dispose();
            return;
        }

        Document target = Vm.CreateDerivedDocument(doc.DisplayName);
        target.InsertContent(0, partial, Loc.Get("Operation_Decompress"));
        target.ClearHistory();
        DocumentViewModel vm = Vm.AddDerived(target, doc.DisplayName, doc);
        vm.RangeLabel = DerivedRangeLabel(start, e.InputOffset, Loc.Get("Derived_Label_Partial"));
        Vm.Selected = vm;
    }

    // ---- 圧縮データの範囲を選択 ----

    /// <summary>
    /// カーソル位置から展開を試み、圧縮データとして読めた範囲を選択する (仕様 5)。データは変えない。手がかりのある形式を先に、
    /// なければ deflate (zip のエントリなど)・Brotli を試す。展開した内容は書き出さず、数えるだけ。
    /// </summary>
    private async Task SelectCompressedAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        long start = doc.Editor.Cursor;
        long length = doc.Document.Length - start;
        if (length <= 0)
        {
            ShowNotice(Loc.Get("SelectCompressed_NotFound"), InfoBarSeverity.Warning, doc);
            return;
        }

        DocumentSnapshot snapshot = doc.Document.Current;
        DecompressionResult? found = null;
        try
        {
            found = await Vm.Operations.RunAsync(Loc.Get("Operation_SelectCompressed"), OperationKind.ReadOnly, doc.Document, length, op =>
            {
                byte[] head = RangeReader.Head(snapshot, start, length, 16);
                IEnumerable<CompressionFormat> order = CompressionFormats.Detect(head)
                    .Concat([CompressionFormat.Deflate, CompressionFormat.Brotli]).Distinct().Where(FormatAvailable);
                foreach (CompressionFormat format in order)
                {
                    try
                    {
                        DecompressionResult r = CompressionEngine.Decompress(snapshot, start, length, format, new CodecOutput(null), op);
                        if (r.ConsumedInput > 0)
                        {
                            return Task.FromResult<DecompressionResult?>(r);
                        }
                    }
                    catch (CompressedDataException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                return Task.FromResult<DecompressionResult?>(null);
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (DataReadException ex)
        {
            ShowNotice(Loc.Format("DataOp_Error_Read", StatusFormat.Hex(ex.Offset)), InfoBarSeverity.Error, doc);
            return;
        }

        if (found is null)
        {
            ShowNotice(Loc.Get("SelectCompressed_NotFound"), InfoBarSeverity.Warning, doc);
            return;
        }

        doc.Editor.RecordJump();
        doc.Editor.Select(start, found.ConsumedInput);
        LastSelectCompressed = found;
        ShowNotice(Loc.Format("SelectCompressed_Found", CompressionFormatName(found.Format), StatusFormat.Number(found.ConsumedInput, Culture),
            StatusFormat.Number(found.OutputLength, Culture)), InfoBarSeverity.Success, doc);
        FocusEditor();
    }

    /// <summary>テスト用: 最後の「圧縮データの範囲を選択」の結果。</summary>
    internal DecompressionResult? LastSelectCompressed { get; private set; }

    // ---- 共通 ----

    /// <summary>項目を個別に無効にできるドロップダウン (使えない形式・アルゴリズムを選べないようにする)。</summary>
    private static ComboBox ItemCombo(string automationId, string header, IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox { Header = header, MinWidth = 220 };
        foreach (string item in items)
        {
            combo.Items.Add(new ComboBoxItem { Content = item });
        }

        combo.SelectedIndex = selected;
        AutomationProperties.SetAutomationId(combo, automationId);
        AutomationProperties.SetName(combo, header);
        return combo;
    }

    private static JsonObject LoadDialogState(string key)
    {
        try
        {
            return JsonNode.Parse(AppState.GetString(key, "{}")) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static void SaveDialogState(string key, JsonObject state) => AppState.SetString(key, state.ToJsonString());
}
