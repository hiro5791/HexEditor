using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Expressions;
using HexEditor.Core.Formats;
using HexEditor.Core.Notifications;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// 特別な開き方: 詳細を指定して開く (ENG-11 の仕様 2)、範囲を指定して開く (ENG-13)、Intel HEX・S-record・Base64 をデコードして開く (ENG-38)、
/// 元の形式で保存する (TOOL-11)、選択範囲・ブックマークを新しいタブで開く (ENG-39)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>デコードを UI スレッドで済ませる大きさの上限 (これより大きいファイルは長時間処理として読む)。</summary>
    private const long SyncDecodeLimit = 16L * 1024 * 1024;

    /// <summary>デコードしたドキュメントの「名前を付けて保存」で選んだ、バイナリの保存先。</summary>
    private string? _binarySaveAsPath;

    private void RegisterFormatCommands()
    {
        Commands.Register("file.openAdvanced", () => ShowOpenAdvancedAsync(rangeFirst: false));
        Commands.Register("file.openRange", () => ShowOpenAdvancedAsync(rangeFirst: true));
        Commands.Register("file.openAsIntelHex", () => PickAndOpenEncodedAsync(FormatIds.IntelHex));
        Commands.Register("file.openAsSRecord", () => PickAndOpenEncodedAsync(FormatIds.SRecord));
        Commands.Register("file.openAsBase64", () => PickAndOpenEncodedAsync(FormatIds.Base64));
        Commands.Register("file.formatSettings", ShowFormatSettingsAsync,
            () => NeedsDocument(d => d.Encoded is null ? Loc.Get("Formats_NotEncoded") : null));
        Commands.Register("file.openSelectionInNewTab", () => OpenSelectionInNewTab(copy: false), NeedsSelection);
        Commands.Register("file.openSelectionAsCopy", () => OpenSelectionInNewTab(copy: true), NeedsSelection);
        RegisterTransferCommands();
        RegisterWorkspaceCommands();
    }

    private CommandState NeedsSelection() => NeedsDocument(d => d.Editor.HasSelection ? null : Loc.Get("Command_NoSelection"));

    /// <summary>「開く」のダイアログで 1 つのファイルを選ぶ (テストでは差し替える)。</summary>
    private async Task<string?> PickOneFileAsync(string settingsIdentifier)
    {
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    // ---- 詳細を指定して開く (ENG-11 の仕様 2) ----

    /// <summary>入力式の文脈: <c>end</c> はファイルの長さ (ENG-13 の仕様 1)。</summary>
    private sealed class FileLengthContext(long length) : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => length;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }

    /// <summary>「詳細を指定して開く」のダイアログの今の状態 (テスト用の命令が読む)。</summary>
    internal OpenAdvancedState? OpenAdvancedForTest { get; private set; }

    internal sealed class OpenAdvancedState
    {
        public required ContentDialog Dialog { get; init; }

        public required TextBox Path { get; init; }

        public required CheckBox Range { get; init; }

        public required TextBox Start { get; init; }

        public required TextBox Length { get; init; }

        public required TextBlock StartResult { get; init; }

        public required TextBlock LengthResult { get; init; }

        public required ComboBox Format { get; init; }

        /// <summary>入力の確認と解釈結果の表示を今すぐ行う (テスト用。TextChanged は後から届くため)。</summary>
        public Action Validate { get; set; } = () => { };
    }

    private static readonly string[] OpenFormats = ["auto", FormatIds.Binary, FormatIds.IntelHex, FormatIds.SRecord, FormatIds.Base64];

    /// <summary>
    /// 「詳細を指定して開く」(ENG-11 の仕様 2): パス、読み取り専用、範囲 (ENG-13)、形式 (ENG-38)、他のアプリの書き込みを禁止 (ENG-15)。
    /// 既定値のまま「開く」を押せば通常の「開く」と同じ。「ディスクイメージとして開く」(ENG-31) は、セクタサイズを選ぶダイアログに進む。
    /// </summary>
    private async Task ShowOpenAdvancedAsync(bool rangeFirst)
    {
        TextBox path = DialogParts.Field("OpenAdv_Path", Loc.Get("OpenAdv_Path"), string.Empty, monospace: false);
        var browse = new Button { Content = Loc.Get("OpenAdv_Browse"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(browse, "OpenAdv_Browse");
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(path);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);

        CheckBox readOnly = DialogParts.Check("OpenAdv_ReadOnly", Loc.Get("OpenAdv_ReadOnly"), false);
        CheckBox denyWrites = DialogParts.Check("OpenAdv_DenyWrites", Loc.Get("OpenAdv_DenyWrites"), false);
        CheckBox diskImage = DialogParts.Check("OpenAdv_DiskImage", Loc.Get("OpenAdv_DiskImage"), false);
        CheckBox range = DialogParts.Check("OpenAdv_Range", Loc.Get("OpenAdv_Range"), rangeFirst);
        TextBox start = DialogParts.Field("OpenAdv_Start", Loc.Get("OpenAdv_Start"), "0");
        TextBlock startResult = DialogParts.Caption("OpenAdv_StartResult", monospace: true);
        ComboBox lengthKind = DialogParts.Combo("OpenAdv_LengthKind", Loc.Get("OpenAdv_LengthKind"),
            [Loc.Get("OpenAdv_ByLength"), Loc.Get("OpenAdv_ByEnd")], 0);
        TextBox length = DialogParts.Field("OpenAdv_Length", Loc.Get("OpenAdv_Length"), "1M");
        TextBlock lengthResult = DialogParts.Caption("OpenAdv_LengthResult", monospace: true);
        CheckBox resizable = DialogParts.Check("OpenAdv_Resizable", Loc.Get("OpenAdv_Resizable"), false);
        var rangePanel = new StackPanel { Spacing = 4, Margin = new Thickness(24, 0, 0, 0) };
        foreach (UIElement e in new UIElement[] { start, startResult, lengthKind, length, lengthResult, resizable })
        {
            rangePanel.Children.Add(e);
        }

        ComboBox format = DialogParts.Combo("OpenAdv_Format", Loc.Get("OpenAdv_Format"),
            OpenFormats.Select(f => Loc.Get("OpenAdv_Format_" + f.Replace("-", string.Empty))), 0);
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (UIElement e in new UIElement[] { pathRow, readOnly, range, rangePanel, format, denyWrites, diskImage })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "OpenAdvancedDialog", Loc.Get("OpenAdv_Title"),
            new ScrollViewer { Content = body, MaxHeight = 520 }, Loc.Get("OpenAdv_Open"));
        OpenAdvancedForTest = new OpenAdvancedState
        {
            Dialog = dialog, Path = path, Range = range, Start = start, Length = length, StartResult = startResult,
            LengthResult = lengthResult, Format = format,
        };

        long startValue = 0, lengthValue = 0;
        void Validate()
        {
            // ディスクイメージとして開く場合は、範囲・形式は使わない (セクタサイズは次のダイアログで選ぶ)。
            bool image = diskImage.IsChecked == true;
            range.IsEnabled = format.IsEnabled = !image;
            if (image)
            {
                range.IsChecked = false;
            }

            rangePanel.Visibility = range.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            bool exists = File.Exists(path.Text);
            bool ok = exists;
            if (range.IsChecked == true && exists)
            {
                long fileLength = new FileInfo(path.Text).Length;
                var context = new FileLengthContext(fileLength);
                bool startOk = DialogParts.TryEvaluate(start.Text, context, out startValue, out ExpressionException? startError)
                    && startValue >= 0 && startValue < fileLength;
                startResult.Text = startOk ? DialogParts.Interpretation(startValue)
                    : startError is not null ? DialogParts.ExpressionError(startError) : Loc.Get("Range_StartOutside");
                bool lengthOk = DialogParts.TryEvaluate(length.Text, context, out long value, out ExpressionException? lengthError);
                lengthValue = lengthKind.SelectedIndex == 1 ? value - startValue + 1 : value;
                lengthOk &= lengthValue > 0;
                string lengthText = lengthOk ? DialogParts.Interpretation(value)
                    : lengthError is not null ? DialogParts.ExpressionError(lengthError) : Loc.Get("Range_LengthInvalid");
                if (lengthOk && startOk && startValue + lengthValue > fileLength)
                {
                    // 終了がファイルの長さを超える分は、末尾までに切り詰める (ENG-13 の「エラー」)。
                    lengthText += Environment.NewLine + Loc.Format("Range_Truncated", Size(fileLength - startValue));
                }

                lengthResult.Text = lengthText;
                DialogParts.MarkInvalid(start, !startOk);
                DialogParts.MarkInvalid(length, !lengthOk);
                ok &= startOk && lengthOk;
            }

            DialogParts.MarkInvalid(path, path.Text.Length > 0 && !exists);
            dialog.IsPrimaryButtonEnabled = ok;
        }

        foreach (TextBox box in new[] { path, start, length })
        {
            box.TextChanged += (_, _) => Validate();
        }

        OpenAdvancedForTest.Validate = Validate;

        range.Checked += (_, _) => Validate();
        range.Unchecked += (_, _) => Validate();
        diskImage.Checked += (_, _) => Validate();
        diskImage.Unchecked += (_, _) => Validate();
        lengthKind.SelectionChanged += (_, _) => Validate();
        browse.Click += async (_, _) =>
        {
            if (await PickOneFileAsync("HexEditor.OpenAdvanced") is { } chosen)
            {
                path.Text = chosen;
            }
        };
        Validate();
        try
        {
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        finally
        {
            if (OpenAdvancedForTest?.Dialog == dialog)
            {
                OpenAdvancedForTest = null;
            }
        }

        string file = Path.GetFullPath(path.Text);
        string chosenFormat = OpenFormats[Math.Max(0, format.SelectedIndex)];
        DocumentViewModel? opened;
        if (diskImage.IsChecked == true)
        {
            await OpenDiskImageAsync(file);
            return;
        }

        if (range.IsChecked == true)
        {
            opened = await OpenRangeAsync(file, startValue, lengthValue, resizable.IsChecked == true, readOnly.IsChecked == true);
        }
        else if (chosenFormat is FormatIds.IntelHex or FormatIds.SRecord or FormatIds.Base64)
        {
            opened = await OpenEncodedAsync(file, chosenFormat, null);
        }
        else
        {
            opened = TryOpen(file, readOnly: readOnly.IsChecked == true, decode: chosenFormat == "auto");
        }

        if (opened is not null && denyWrites.IsChecked == true)
        {
            opened.Document.LockPolicy = Core.Engine.FileLockPolicy.Always;
        }

        UpdateTitle();
    }

    // ---- 範囲を指定して開く (ENG-13) ----

    /// <summary>
    /// 範囲を開く。同じファイルの重なる範囲・全体が開いていれば、「同じデータを 2 つのタブで編集すると、後から保存した方が優先されます」と確かめる
    /// (仕様 6)。
    /// </summary>
    private async Task<DocumentViewModel?> OpenRangeAsync(string path, long start, long length, bool resizable, bool readOnly)
    {
        long end = Math.Min(start + length, new FileInfo(path).Length);
        bool overlaps = Vm.TabsOfFile(path).Any(d => d.RangeSource is not { } r || r.RangeStart < end && start < r.RangeStart + r.Length);
        if (overlaps)
        {
            switch (await ConfirmOverlapAsync())
            {
                case OverlapChoice.Cancel:
                    return null;
                case OverlapChoice.ReadOnly:
                    readOnly = true;
                    break;
            }
        }

        try
        {
            DocumentViewModel vm = Vm.OpenRange(path, start, length, resizable, readOnly);
            ApplyLinkedOrRangeView(vm);
            AppLog.Debug($"Opened range {start:X}+{length:X} of {path}");
            return vm;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            ShowNotice(Loc.Format("Error_Open", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
            return null;
        }
    }

    private enum OverlapChoice
    {
        Cancel,
        ReadOnly,
        Open,
    }

    private async Task<OverlapChoice> ConfirmOverlapAsync()
    {
        ContentDialog dialog = DialogParts.Dialog(Root, "RangeOverlapDialog", Loc.Get("Range_OverlapTitle"),
            new TextBlock { Text = Loc.Get("Range_OverlapBody"), TextWrapping = TextWrapping.Wrap }, Loc.Get("Range_OpenReadOnly"),
            Loc.Get("Range_OpenAnyway"));
        return await dialog.ShowQueuedAsync() switch
        {
            ContentDialogResult.Primary => OverlapChoice.ReadOnly,
            ContentDialogResult.Secondary => OverlapChoice.Open,
            _ => OverlapChoice.Cancel,
        };
    }

    /// <summary>範囲のタブ・連動ビューの表示の設定を結び付ける (ベースアドレスは開始位置。ENG-13 の仕様 2、ENG-39 の仕様 1)。</summary>
    private void ApplyLinkedOrRangeView(DocumentViewModel vm)
    {
        ViewOptions.Attach(App.Settings, vm);
        if (vm.LinkParent is { } parent)
        {
            ulong parentBase = parent.Editor.View.BaseAddress;
            vm.Editor.ApplyView(vm.Editor.View with { BaseAddress = parentBase + (ulong)vm.Document.LinkStart });
        }
    }

    // ---- エンコード形式をデコードして開く (ENG-38) ----

    /// <summary>拡張子からデコードする形式 (ENG-38 の仕様 1)。</summary>
    private static string? AutoDecodeFormat(string path) => FormatIds.ForOpenExtension(path);

    private async Task PickAndOpenEncodedAsync(string format)
    {
        if (await PickOneFileAsync("HexEditor.OpenEncoded") is { } path)
        {
            await OpenEncodedAsync(path, format, null);
            UpdateTitle();
        }
    }

    /// <summary>
    /// デコードして開く (ENG-38 の仕様 2)。小さいファイルはすぐに、大きいファイルは長時間処理としてデコードする。形式として全く読めなければ
    /// 「Intel HEX として読めませんでした (最初の誤り: 行 N)」と「バイナリのまま開く」を示す。
    /// </summary>
    private async Task<DocumentViewModel?> OpenEncodedAsync(string path, string format, int? insertAt)
    {
        string full = Path.GetFullPath(path);
        if (Vm.Documents.FirstOrDefault(d => d.Encoded is not null && string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            Vm.Selected = open;
            return open;
        }

        ImportResult result;
        DocumentViewModel? preview = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? previewTimer = null;
        try
        {
            long size = new FileInfo(full).Length;
            ImportOptions options = EncodedFile.OpenOptions(format);
            string temp = Vm.DocumentOptions.TempDirectory;
            if (size <= SyncDecodeLimit)
            {
                result = Importer.DecodeFile(full, options, temp);
            }
            else
            {
                // デコード中も、書き終えた先頭から読み取り専用のタブで表示する (ENG-38 の仕様 2・受け入れ基準 3)。表示は 0.5 秒ごとに伸ばす。
                ImportProgress? live = null;
                string previewName = Loc.Format("Encoded_DecodingTab", Path.GetFileName(full));
                previewTimer = DispatcherQueue.CreateTimer();
                previewTimer.Interval = TimeSpan.FromMilliseconds(500);
                previewTimer.Tick += (_, _) =>
                {
                    if (live?.Preview(previewName) is not { } image)
                    {
                        return;
                    }

                    if (preview is null)
                    {
                        preview = Vm.AddDecodingPreview(image, previewName);
                        DecodingPreviewForTest = preview;
                        if (insertAt is int at)
                        {
                            Vm.MoveDocument(preview, Math.Min(at, Vm.Documents.Count - 1));
                        }
                    }
                    else if (!preview.Document.IsDisposed && Vm.Documents.Contains(preview) && image.Length > preview.Document.Length)
                    {
                        long cursor = preview.Editor.Cursor;
                        long top = preview.Editor.TopRow;
                        preview.Document.ReplaceSource(image);
                        preview.RestorePosition(cursor, 0, 0, top);
                    }
                    else
                    {
                        image.Dispose();
                    }
                };
                previewTimer.Start();
                result = await Vm.Operations.RunAsync(Loc.Format("Encoded_Decoding", Path.GetFileName(full), FormatName(format)),
                    Core.Operations.OperationKind.ReadOnly, null, size,
                    op =>
                    {
                        live = new ImportProgress(op.CancellationToken, op.Report);
                        return Task.FromResult(Importer.DecodeFile(full, options, temp, live));
                    });
            }
        }
        catch (OperationCanceledException)
        {
            CloseDecodingPreview(previewTimer, preview);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CloseDecodingPreview(previewTimer, preview);
            ShowNotice(Loc.Format("Error_Open", Path.GetFileName(full), ex.Message), InfoBarSeverity.Error);
            return null;
        }

        // デコード中の表示のタブは、同じ位置・同じカーソルのデコードの結果のタブに置き換える (完了後は編集できる)。
        previewTimer?.Stop();
        (long Cursor, long Top)? previewPosition = null;
        if (preview is not null && Vm.Documents.IndexOf(preview) is var previewIndex and >= 0)
        {
            previewPosition = (preview.Editor.Cursor, preview.Editor.TopRow);
            insertAt = previewIndex;
            Vm.Close(preview);
        }

        if (result.DataBytes == 0 && result.HasErrors)
        {
            int line = result.Issues.Items.FirstOrDefault(i => !i.IsWarning)?.Line ?? 1;
            result.Dispose();
            ShowNotice(Loc.Format("Encoded_Unreadable", FormatName(format), line), InfoBarSeverity.Error, actions:
            [
                new NotificationAction(Loc.Get("Encoded_OpenBinary"), () => { TryOpen(full, insertAt, decode: false); UpdateTitle(); }),
            ]);
            return null;
        }

        bool hasErrors = result.HasErrors;
        int errorLines = result.Issues.Items.Where(i => !i.IsWarning).Select(i => i.Line).Distinct().Count();
        DocumentViewModel vm = Vm.AddDecoded(result, full, format);
        ViewOptions.Attach(App.Settings, vm);
        if (insertAt is int at)
        {
            Vm.MoveDocument(vm, Math.Min(at, Vm.Documents.Count - 1));
        }

        if (previewPosition is { } position)
        {
            vm.RestorePosition(position.Cursor, 0, 0, position.Top);
        }

        Vm.StartWatching(vm);
        ShowNotice(Loc.Format("Encoded_Decoded", FormatName(format)), InfoBarSeverity.Informational, vm, actions:
        [
            new NotificationAction(Loc.Get("Encoded_OpenAsText"), () => ReopenAsBinary(vm)),
        ]);

        // 誤りのある行は結果一覧に出し、読み取り専用で開く (ENG-38 の仕様 6)。
        if (vm.FormatIssues.Count > 0)
        {
            ShowFormatIssues(vm);
        }

        if (hasErrors)
        {
            vm.Document.SetReadOnly(Core.Engine.ReadOnlyReason.User);
            ShowNotice(Loc.Format("Encoded_ErrorLines", errorLines), InfoBarSeverity.Warning, vm, actions:
            [
                new NotificationAction(Loc.Get("Encoded_AllowEdit"), () =>
                {
                    vm.Document.SetReadOnly(Core.Engine.ReadOnlyReason.None);
                    UpdateCommandStates();
                }),
            ]);
        }

        AppLog.Info($"Decoded {Path.GetFileName(full)} as {format}: {vm.Document.Length} bytes, {vm.FormatIssues.Count} issue(s)");
        return vm;
    }

    /// <summary>デコード中の表示のタブ (テスト用。TC-ENG-38-03)。</summary>
    internal DocumentViewModel? DecodingPreviewForTest { get; private set; }

    private void CloseDecodingPreview(Microsoft.UI.Dispatching.DispatcherQueueTimer? timer, DocumentViewModel? preview)
    {
        timer?.Stop();
        if (preview is not null && Vm.Documents.Contains(preview))
        {
            Vm.Close(preview);
        }
    }

    /// <summary>「テキストのまま開く」: デコードしたタブを閉じ、同じファイルをバイナリのまま開く。</summary>
    private void ReopenAsBinary(DocumentViewModel vm)
    {
        string? path = vm.FilePath;
        int index = Vm.Documents.IndexOf(vm);
        if (vm.Document.IsModified || path is null)
        {
            return;
        }

        Vm.Close(vm);
        TryOpen(path, index, decode: false);
        UpdateTitle();
    }

    /// <summary>形式の表示名。</summary>
    private static string FormatName(string format) => Loc.Get("Format_" + format.Replace("-", string.Empty));

    /// <summary>拡張子が違っても内容から判定できた場合は、「Intel HEX として開き直す」を提案する (ENG-38 の仕様 1)。バックグラウンドで判定する。</summary>
    private async Task SuggestDecodingAsync(DocumentViewModel vm)
    {
        if (vm.FilePath is not { } path || vm.Document.Length == 0)
        {
            return;
        }

        string format;
        try
        {
            format = await Task.Run(() => Importer.DetectFile(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (!EncodedFile.IsOpenable(format) || vm.Document.IsDisposed || !Vm.Documents.Contains(vm))
        {
            return;
        }

        ShowNotice(Loc.Format("Encoded_Suggest", FormatName(format)), InfoBarSeverity.Informational, vm, actions:
        [
            new NotificationAction(Loc.Format("Encoded_Reopen", FormatName(format)), () =>
            {
                if (!vm.Document.IsModified && Vm.Documents.Contains(vm))
                {
                    int index = Vm.Documents.IndexOf(vm);
                    Vm.Close(vm);
                    _ = OpenEncodedAsync(path, format, index);
                }
            }),
        ]);
    }

    // ---- 元の形式で保存する (TOOL-11) ----

    /// <summary>
    /// デコードして開いたドキュメントを元の形式で保存する (仕様 2)。データとして読めない行があったファイルは、最初の保存のときに失われる旨を確かめる
    /// (仕様 4)。保存しなかった (キャンセル・失敗) 場合は false。
    /// </summary>
    private async Task<bool> SaveEncodedAsync(DocumentViewModel doc, string path, EncodedFileSettings settings)
    {
        bool samePath = string.Equals(Path.GetFullPath(path), doc.FilePath, StringComparison.OrdinalIgnoreCase);
        if (samePath && doc.Encoded?.HasForeignLines == true && !doc.ForeignLinesConfirmed)
        {
            ContentDialog confirm = DialogParts.Dialog(Root, "EncodedLossDialog", Loc.Get("Encoded_LossTitle"),
                new TextBlock { Text = Loc.Format("Encoded_LossBody", doc.DisplayName), TextWrapping = TextWrapping.Wrap },
                Loc.Get("Encoded_LossSave"), Loc.Get("Menu_File_SaveAs/Text").TrimEnd('.', '…'));
            switch (await confirm.ShowQueuedAsync())
            {
                case ContentDialogResult.Primary:
                    doc.ForeignLinesConfirmed = true;
                    break;
                case ContentDialogResult.Secondary:
                    return await SaveAsync(doc, saveAs: true);
                default:
                    return false;
            }
        }

        Core.Engine.DocumentSnapshot snapshot = doc.Document.Current;
        try
        {
            EncodedFile.Validate(snapshot, doc.EncodedBaseAddress, settings);
        }
        catch (FormatLimitException ex)
        {
            // 元の形式で表せない: 書き込みを始める前に理由を示し、名前を付けて保存で他の形式を選ぶよう案内する。
            ShowNotice(Loc.Format("Encoded_TooLarge", ex.Format, $"0x{ex.Limit:X}"), InfoBarSeverity.Error, doc, actions:
            [
                new NotificationAction(Loc.Get("Menu_File_SaveAs/Text").TrimEnd('.', '…'), () => _ = SaveAsync(doc, saveAs: true)),
            ]);
            return false;
        }

        if (doc.Watch is { } watch)
        {
            Vm.ExternalChanges?.Suspend(watch);
        }

        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Operation_Save", Path.GetFileName(path)), Core.Operations.OperationKind.WritesExternal,
                doc.Document, snapshot.Length, op =>
                {
                    EncodedFile.Save(snapshot, doc.EncodedBaseAddress, settings, path, op);
                    return Task.CompletedTask;
                }, locked => doc.Document.SetEditLock(locked));
        }
        catch (OperationCanceledException)
        {
            Vm.RebaseWatch(doc);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Vm.RebaseWatch(doc);
            ShowNotice(Loc.Format("Error_SaveIo", ex.Message), InfoBarSeverity.Error, doc);
            return false;
        }

        doc.Encoded = settings;
        if (!samePath)
        {
            doc.SetSavedPath(Path.GetFullPath(path));
        }

        doc.Document.MarkSaved();
        doc.OnSaved();
        Vm.RebaseWatch(doc);
        UpdateTitle();
        return true;
    }

    /// <summary>名前を付けて保存の形式の選択肢 (TOOL-11 の仕様 3: 元の形式・バイナリ・TOOL-04 の形式)。</summary>
    private static IReadOnlyList<(string Label, string Extension, string Format)> SaveAsChoices(EncodedFileSettings settings)
    {
        var list = new List<(string, string, string)>
        {
            (FormatName(settings.Format), FormatIds.Extension(settings.Format), settings.Format),
            (FormatName(FormatIds.Binary), ".bin", FormatIds.Binary),
        };
        foreach (string f in new[] { FormatIds.IntelHex, FormatIds.SRecord, FormatIds.Base64 })
        {
            if (f != settings.Format)
            {
                list.Add((FormatName(f), FormatIds.Extension(f), f));
            }
        }

        return list;
    }

    /// <summary>
    /// デコードしたドキュメントの「名前を付けて保存」(TOOL-11 の仕様 3)。保存先の拡張子で形式を決める: Intel HEX・S-record・Base64 ならその形式、
    /// それ以外はバイナリ (通常の保存。以後はバイナリのドキュメント)。
    /// </summary>
    private async Task<bool?> SaveEncodedAsAsync(DocumentViewModel doc, EncodedFileSettings settings)
    {
        IReadOnlyList<(string Label, string Extension, string Format)> choices = SaveAsChoices(settings);
        AppLog.Info("Save as choices: " + string.Join(", ", choices.Select(c => c.Label)));
        string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + choices[0].Extension;
        string? path;
        if (!TestHooks.TrySavePicker(suggested, out path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.SaveAs" };
            foreach ((string label, string ext, _) in choices)
            {
                picker.FileTypeChoices.Add(label, [ext]);
            }

            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return false;
        }

        string? target = FormatIds.ForOpenExtension(path);
        if (target is null)
        {
            // バイナリとして普通に保存する (選んだ保存先を通常の保存に渡す)。
            _binarySaveAsPath = path;
            return null;
        }

        EncodedFileSettings use = target == settings.Format ? settings : new EncodedFileSettings { Format = target, NewLine = settings.NewLine };
        return await SaveEncodedAsync(doc, path, use);
    }

    /// <summary>
    /// 「形式の設定...」(TOOL-11 の仕様 2): 元の形式で保存するときの設定 (1 レコードのデータ長、アドレスの形式、実行開始アドレス、S0 の文字列、
    /// レコード数の有無、大文字・小文字、改行。Base64 は 1 行の文字数・URL セーフ・パディング・改行) を変える。
    /// </summary>
    private async Task ShowFormatSettingsAsync()
    {
        if (Vm.Selected is not { Encoded: { } s } doc)
        {
            return;
        }

        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        ComboBox newLine = DialogParts.Combo("FormatSettings_NewLine", Loc.Get("Export_NewLine"), ["CRLF", "LF"], s.NewLine == "\n" ? 1 : 0);
        TextBox recordLength = DialogParts.Field("FormatSettings_RecordLength", Loc.Get("Export_RecordLength"),
            s.RecordLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ComboBox mode = DialogParts.Combo("FormatSettings_AddressMode", Loc.Get("Export_AddressMode"),
            s.Format == FormatIds.IntelHex ? ["I8HEX", "I16HEX", "I32HEX"] : ["S1", "S2", "S3"],
            s.Format == FormatIds.IntelHex ? Math.Max(0, (int)s.IntelMode - 1) : Math.Max(0, (int)s.SRecordMode - 1));
        TextBox exec = DialogParts.Field("FormatSettings_Exec", Loc.Get("Export_ExecAddress"),
            s.StartAddress is { } a ? $"0x{a:X}" : string.Empty);
        TextBox header = DialogParts.Field("FormatSettings_Header", Loc.Get("Export_Header"), s.Header ?? string.Empty, monospace: false);
        CheckBox count = DialogParts.Check("FormatSettings_Count", Loc.Get("Export_WriteCount"), s.WriteCount);
        CheckBox upper = DialogParts.Check("FormatSettings_Upper", Loc.Get("Export_UpperCase"), s.UpperCase);
        TextBox lineLength = DialogParts.Field("FormatSettings_LineLength", Loc.Get("Export_LineLength"),
            s.LineLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        CheckBox urlSafe = DialogParts.Check("FormatSettings_UrlSafe", Loc.Get("Export_UrlSafe"), s.UrlSafe);
        CheckBox padding = DialogParts.Check("FormatSettings_Padding", Loc.Get("Export_Padding"), s.Padding);
        UIElement[] fields = s.Format == FormatIds.Base64
            ? [lineLength, urlSafe, padding, newLine]
            : s.Format == FormatIds.IntelHex ? [recordLength, mode, exec, upper, newLine] : [recordLength, mode, exec, header, count, upper, newLine];
        foreach (UIElement e in fields)
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "FormatSettingsDialog", Loc.Format("FormatSettings_Title", FormatName(s.Format)), body,
            Loc.Get("Common_Ok"));
        void Validate()
        {
            bool recordOk = int.TryParse(recordLength.Text, out int r) && r is >= 1 and <= 255;
            bool lineOk = int.TryParse(lineLength.Text, out int l) && l >= 0;
            bool execOk = exec.Text.Trim().Length == 0 || DialogParts.TryEvaluate(exec.Text, new FileLengthContext(0), out long e, out _) && e >= 0;
            DialogParts.MarkInvalid(recordLength, !recordOk);
            DialogParts.MarkInvalid(lineLength, !lineOk);
            DialogParts.MarkInvalid(exec, !execOk);
            dialog.IsPrimaryButtonEnabled = s.Format == FormatIds.Base64 ? lineOk : recordOk && execOk;
        }

        foreach (TextBox box in new[] { recordLength, lineLength, exec })
        {
            box.TextChanged += (_, _) => Validate();
        }

        Validate();
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        long? execValue = exec.Text.Trim().Length == 0 ? null
            : DialogParts.TryEvaluate(exec.Text, new FileLengthContext(0), out long v, out _) ? v : s.StartAddress;
        doc.Encoded = s with
        {
            NewLine = newLine.SelectedIndex == 1 ? "\n" : "\r\n",
            RecordLength = int.TryParse(recordLength.Text, out int rl) ? rl : s.RecordLength,
            IntelMode = s.Format == FormatIds.IntelHex ? (IntelHexAddressMode)(mode.SelectedIndex + 1) : s.IntelMode,
            SRecordMode = s.Format == FormatIds.SRecord ? (SRecordAddressMode)(mode.SelectedIndex + 1) : s.SRecordMode,
            StartAddress = execValue,
            Header = header.Text,
            WriteCount = count.IsChecked == true,
            UpperCase = upper.IsChecked == true,
            LineLength = int.TryParse(lineLength.Text, out int ll) ? ll : s.LineLength,
            UrlSafe = urlSafe.IsChecked == true,
            Padding = padding.IsChecked == true,
        };
    }

    // ---- 選択範囲・ブックマークを新しいタブで開く (ENG-39) ----

    /// <summary>選択範囲を連動ビュー (既定) またはコピーとして新しいタブで開く。範囲の大きさに関係なく即座に開く。</summary>
    private void OpenSelectionInNewTab(bool copy)
    {
        if (Vm.Selected is not { } doc || !doc.Editor.HasSelection)
        {
            return;
        }

        OpenRangeInNewTab(doc, doc.Editor.SelectionStart, doc.Editor.SelectionLength, null, copy);
    }

    /// <summary>範囲を新しいタブで開く (ブックマークから開く場合はブックマークの名前をタブの名前にする。仕様 3)。</summary>
    internal DocumentViewModel? OpenRangeInNewTab(DocumentViewModel doc, long offset, long length, string? name, bool copy)
    {
        if (length <= 0)
        {
            return null;
        }

        LinkedTabOpenedAt = Environment.TickCount64;
        DocumentViewModel vm = copy ? Vm.OpenAsCopy(doc, offset, length, name) : Vm.OpenLinkedView(doc, offset, length, name);
        ApplyLinkedOrRangeView(vm);
        if (!copy)
        {
            vm.Document.LinkChanged += (_, _) => DispatcherQueue.TryEnqueue(() => OnLinkChanged(vm));
        }

        UpdateTitle();
        return vm;
    }

    /// <summary>「新しいタブで開く」を始めた時刻 (Environment.TickCount64。テストが開くまでの時間を計る)。</summary>
    internal long LinkedTabOpenedAt { get; private set; }

    /// <summary>連動ビューの範囲が動いた・切断された: 見出しとベースアドレスを合わせ、切断なら知らせる (仕様 1)。</summary>
    private void OnLinkChanged(DocumentViewModel vm)
    {
        if (vm.Document.IsDisposed || vm.LinkParent is not { } parent)
        {
            return;
        }

        vm.RangeLabel = DocumentViewModel.FormatRange(vm.Document.LinkStart, vm.Document.Length);
        vm.Editor.ApplyView(vm.Editor.View with { BaseAddress = parent.Editor.View.BaseAddress + (ulong)vm.Document.LinkStart });
        if (vm.Document.IsLinkDisconnected)
        {
            ShowNotice(Loc.Get("Linked_Disconnected"), InfoBarSeverity.Warning, vm);
        }
    }
}
