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
        Commands.Register("file.saveSelection", SaveSelectionAsync, NeedsDocument);
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

        /// <summary>欄の値を変える処理 (候補を示す入力欄など、コントロールに値を入れるだけでは値が変わらない欄のため)。</summary>
        public Dictionary<string, Action<string>> Setters { get; } = [];

        /// <summary>インポートの誤り・警告の一覧 (プレビューの下。TOOL-04 の「エラー」)。</summary>
        public ListView? Issues { get; init; }

        /// <summary>UUEncode のファイルの選択 (複数あるとき。TOOL-07 の仕様 1)。</summary>
        public ComboBox? UuFiles { get; init; }

        public Func<Task>? Refreshed { get; set; }

        public Task Pending { get; set; } = Task.CompletedTask;

        public RadioButton[] Targets { get; init; } = [];
    }

    // ---- 設定の欄 ----

    /// <summary>形式ごとの設定の欄を作り、値の変更を <paramref name="changed"/> で知らせる。</summary>
    private static StackPanel BuildFields(IReadOnlyList<TransferField> fields, Dictionary<string, string> values, Dictionary<string, Control> controls,
        Dictionary<string, Action<string>> setters, string idPrefix, Action changed)
    {
        var panel = new StackPanel { Spacing = 6 };
        controls.Clear();
        setters.Clear();
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

                case TransferFieldKind.Suggest:
                {
                    // 候補を示す入力欄 (候補以外の値も入力できる。例: 1 レコードのデータ長は 16 が既定で 32 も示す)。
                    var combo = new ComboBox { Header = label, MinWidth = 220, IsEditable = true, FontFamily = DialogParts.Mono };
                    foreach (string choice in field.Choices!)
                    {
                        combo.Items.Add(choice);
                    }

                    combo.Text = values[field.Key];
                    AutomationProperties.SetAutomationId(combo, id);
                    AutomationProperties.SetName(combo, label);
                    void Set(string text)
                    {
                        if (values[field.Key] != text)
                        {
                            values[field.Key] = text;
                            changed();
                        }
                    }

                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string chosen)
                        {
                            Set(chosen);
                        }
                    };
                    combo.TextSubmitted += (_, e) => Set(e.Text.Trim());
                    setters[field.Key] = text =>
                    {
                        combo.Text = text;
                        Set(text);
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

    /// <summary>
    /// インポートのダイアログ。<paramref name="path"/> を渡すと、そのファイルを選んだ状態で開く。ファイルはダイアログへのドラッグ &amp; ドロップでも選べる
    /// (仕様 2 の 1)。プレビューの下に誤り・警告の一覧 (行・列・内容) を出す (「エラー」)。
    /// </summary>
    private async Task ShowImportAsync(string? path)
    {
        DocumentViewModel? target = Vm.Selected;
        IReadOnlyList<string> formats = FormatIds.Importable;
        TextBox pathBox = DialogParts.Field("Import_Path", Loc.Get("Import_File"), path ?? string.Empty, monospace: false);
        var browse = new Button { Content = Loc.Get("OpenAdv_Browse") }.WithId("Import_Browse");
        ComboBox format = DialogParts.Combo("Import_Format", Loc.Get("Transfer_Format"), formats.Select(FormatName), 0);
        var optionsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };

        // UUEncode で複数のファイルがある場合は、名前で選ぶ (TOOL-07 の仕様 1)。
        ComboBox uuFiles = DialogParts.Combo("Import_UuFile", Loc.Get("Import_UuFile"), [], -1);
        uuFiles.Visibility = Visibility.Collapsed;
        RadioButton toNew = DialogParts.Radio("Import_ToNew", Loc.Get("Import_ToNew"), "ImportTarget", true);
        RadioButton toInsert = DialogParts.Radio("Import_ToInsert", Loc.Get("Import_ToInsert"), "ImportTarget", false);
        RadioButton toOverwrite = DialogParts.Radio("Import_ToOverwrite", Loc.Get("Import_ToOverwrite"), "ImportTarget", false);
        bool editable = target is not null && !target.Editor.ReadOnly;
        toInsert.IsEnabled = editable && target!.Document.CanResize;
        toOverwrite.IsEnabled = editable;
        TextBox preview = PreviewBox("Import_Preview");
        TextBlock summary = DialogParts.Caption("Import_Summary");

        // 誤り・警告の一覧 (警告だけの場合も一覧にする。最大 1,000 件)。
        var issuesHeader = new TextBlock { Text = Loc.Get("Import_IssuesHeader"), Visibility = Visibility.Collapsed };
        ListView issues = IssueList("Import_Issues", 160);
        issues.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(issues, Loc.Get("Import_IssuesHeader"));
        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement e in new UIElement[]
        {
            PathRow(pathBox, browse), format, optionsHost, uuFiles,
            new TextBlock { Text = Loc.Get("Import_Target") }, toNew, toInsert, toOverwrite, preview, summary, issuesHeader, issues,
        })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ImportDialog", Loc.Get("Import_Title"),
            new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Import_Run"));

        // ファイルのドラッグ & ドロップ (仕様 2 の 1)。最初のファイルを選ぶ。
        dialog.AllowDrop = true;
        dialog.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = Loc.Get("Import_DropCaption");
            }
        };
        dialog.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                return;
            }

            DragOperationDeferral deferral = e.GetDeferral();
            try
            {
                IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                if (items.OfType<Windows.Storage.StorageFile>().FirstOrDefault() is { } file)
                {
                    pathBox.Text = file.Path;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };
        var state = new TransferDialogState
        {
            Dialog = dialog, Format = format, Path = pathBox, Preview = preview, Summary = summary, Formats = formats,
            Targets = [toNew, toInsert, toOverwrite], Issues = issues, UuFiles = uuFiles,
        };
        TransferForTest = state;

        Dictionary<string, string> values = [];
        IReadOnlyList<TransferField> fields = [];
        string current = formats[0];
        ImportResult? previewResult = null;
        int version = 0;
        bool fillingUuFiles = false;
        void Rebuild()
        {
            current = formats[Math.Max(0, format.SelectedIndex)];
            fields = TransferOptions.ImportFields(current);
            values = TransferOptions.Load(AppState.GetString("import.options." + current, "{}"), fields);

            // UUEncode の「何番目のファイル」は名前の一覧で選ぶため、番号の欄は出さない。
            optionsHost.Content = BuildFields([.. fields.Where(f => f.Key != "uuIndex")], values, state.Fields, state.Setters, "Import_Opt_",
                () => state.Pending = RefreshPreviewAsync());
            uuFiles.Visibility = Visibility.Collapsed;
            bool ips = current is FormatIds.Ips or FormatIds.Ips32;
            toNew.IsEnabled = !ips;
            if (ips)
            {
                toOverwrite.IsChecked = editable;
            }
        }

        void ShowIssues(ImportResult? result)
        {
            issues.Items.Clear();
            if (result is not null)
            {
                foreach (ImportIssue issue in result.Issues.Items)
                {
                    issues.Items.Add(IssueRow(issue));
                }
            }

            Visibility shown = issues.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            issues.Visibility = issuesHeader.Visibility = shown;
        }

        // UUEncode のファイルの名前の一覧 (2 つ以上あるときだけ出す)。
        void ShowUuFiles(ImportResult? result)
        {
            IReadOnlyList<string> names = result?.UuFiles ?? [];
            if (current != FormatIds.UUEncode || names.Count < 2)
            {
                uuFiles.Visibility = Visibility.Collapsed;
                return;
            }

            fillingUuFiles = true;
            int selected = Math.Clamp(int.TryParse(values.GetValueOrDefault("uuIndex"), out int n) ? n - 1 : 0, 0, names.Count - 1);
            uuFiles.Items.Clear();
            foreach (string name in names)
            {
                uuFiles.Items.Add(name);
            }

            uuFiles.SelectedIndex = selected;
            uuFiles.Visibility = Visibility.Visible;
            fillingUuFiles = false;
        }

        uuFiles.SelectionChanged += (_, _) =>
        {
            if (!fillingUuFiles && uuFiles.SelectedIndex >= 0)
            {
                values["uuIndex"] = (uuFiles.SelectedIndex + 1).ToString(CultureInfo.InvariantCulture);
                state.Pending = RefreshPreviewAsync();
            }
        };

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
                ShowIssues(null);
                ShowUuFiles(null);
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
                ShowIssues(null);
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
                summary.Text = ImportSummary(result, size > PreviewDecodeLimit, options.ValueSize);
                ShowIssues(result);
                ShowUuFiles(result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                summary.Text = ex.Message;
                ShowIssues(null);
            }
        }

        // 形式を判定したファイル (同じファイルでは、利用者の選んだ形式を判定で変えない)。
        string? detectedFor = null;
        async Task Detect()
        {
            if (detectedFor == pathBox.Text)
            {
                await RefreshPreviewAsync();
                return;
            }

            detectedFor = pathBox.Text;
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
        pathBox.TextChanged += (_, _) =>
        {
            // 別のファイルを選んだら、UUEncode の選択は最初のファイルに戻す。
            if (values.ContainsKey("uuIndex"))
            {
                values["uuIndex"] = "1";
            }

            state.Pending = Detect();
        };
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

        AppState.SetString("import.options." + current, TransferOptions.Serialize(values, fields));
        AppState.SetString("import.format", current);
        string chosenFile = Path.GetFullPath(pathBox.Text);
        ImportTarget destination = toInsert.IsChecked == true ? ImportTarget.Insert : toOverwrite.IsChecked == true ? ImportTarget.Overwrite : ImportTarget.New;
        await RunImportAsync(chosenFile, TransferOptions.ToImportOptions(current, values), destination, target);
    }

    /// <summary>誤り・警告の一覧の部品 (等幅、行・列・理由・内容)。</summary>
    private static ListView IssueList(string automationId, double maxHeight)
    {
        var list = new ListView { MaxHeight = maxHeight, SelectionMode = ListViewSelectionMode.None };
        AutomationProperties.SetAutomationId(list, automationId);
        return list;
    }

    /// <summary>誤り・警告 1 件の行 (行・列・理由・内容)。警告には「警告」を付ける。</summary>
    private static TextBlock IssueRow(ImportIssue issue) => new()
    {
        Text = (issue.IsWarning ? Loc.Get("ImportIssue_Warning") + " " : string.Empty) + IssueText(issue) + "  " + issue.Content,
        FontFamily = DialogParts.Mono,
        TextWrapping = TextWrapping.NoWrap,
        FlowDirection = FlowDirection.LeftToRight,
    };

    private enum ImportTarget
    {
        New,
        Insert,
        Overwrite,
    }

    /// <summary>プレビューの要約: 変換後のサイズ・アドレスの範囲・ギャップ・警告と誤りの件数 (TOOL-04 の仕様 2 の 5、TOOL-05 の画面)。</summary>
    private static string ImportSummary(ImportResult result, bool partial, int requestedValueSize = 0)
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

        // 配列の要素のサイズ: 型名から推定した値を示す (「要素のサイズ」で変えられる。TOOL-09 の仕様 4)。
        if (result.InferredValueSize is { } inferred)
        {
            parts.Add(Loc.Format(requestedValueSize == 0 ? "Import_InferredSize" : "Import_ChosenSize", inferred));
        }

        // 誤り・警告の件数 (内容はプレビューの下の一覧に出す)。
        parts.Add(Loc.Format("Import_IssueCounts", result.Issues.WarningCount, result.Issues.ErrorCount));
        if (result.Issues.Count > ImportIssueList.MaxListed)
        {
            parts.Add(Loc.Format("ImportErrors_More", result.Issues.Count));
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

                    // 開始アドレス・S0 の文字列などは付随データとして持ち、エクスポートの既定値にする (TOOL-05・TOOL-06 の仕様 2)。
                    vm.ImportedSettings = result.Settings;
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
        ListView list = IssueList("ImportErrors_List", 280);
        foreach (ImportIssue issue in result.Issues.Items)
        {
            list.Items.Add(IssueRow(issue));
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

        DocumentAnnotations annotations = AnnotationsFor(doc);
        foreach (Core.Bookmarks.Bookmark b in annotations.Bookmarks.All.Take(100_000))
        {
            if (b.Length > 0)
            {
                // ブックマークの色 (色の一覧の色は代表の色) を付ける。
                highlights.Add(new DumpHighlight(b.Start, b.Length, DumpHighlightKind.Bookmark, b.Name, annotations.Bookmarks.EffectiveColor(b).HexText));
            }
        }

        highlights.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // 色付けルール (INSP-33) は行ごとに、書き出す内容のスナップショットで評価する (全体を先に求めない)。
        Core.Coloring.ColoringRuleSet rules = annotations.Coloring.Rules;
        return new DumpOptions
        {
            Encoding = doc.Editor.TextEncoding,
            BaseAddress = (long)doc.Editor.View.BaseAddress,
            Highlights = highlights,
            Title = doc.DisplayName,
            RowHighlights = rules.IsEmpty ? null : (offset, count) => RuleHighlights(snapshot, rules, offset, count),
            RuleColors = [.. rules.Rules.Select(r => r.Rule.Background ?? r.Rule.Foreground).OfType<uint>().Distinct().Select(c => $"#{c:X6}")],
        };
    }

    /// <summary>
    /// 色付けルールの強調 (Hex の列の結果。背景色、なければ文字色を色にする)。同じルールが続くバイトを 1 つにまとめる (TOOL-10 の「色付けルール」)。
    /// </summary>
    internal static IReadOnlyList<DumpHighlight> RuleHighlights(DocumentSnapshot snapshot, Core.Coloring.ColoringRuleSet rules, long offset, int count)
    {
        var hex = new Core.Coloring.ColoringCell[count];
        var text = new Core.Coloring.ColoringCell[count];
        Core.Coloring.ColoringEngine.EvaluateNow(snapshot, rules, offset, hex, text);
        var list = new List<DumpHighlight>();
        int i = 0;
        while (i < count)
        {
            Core.Coloring.ColoringCell cell = hex[i];
            int index = cell.Background >= 0 ? cell.Background : cell.Foreground >= 0 ? cell.Foreground : cell.Border;
            if (index < 0)
            {
                i++;
                continue;
            }

            int j = i + 1;
            while (j < count && hex[j] == cell)
            {
                j++;
            }

            Core.Coloring.ColoringRule rule = rules.Rules[index].Rule;
            uint? color = rule.Background ?? rule.Foreground;
            list.Add(new DumpHighlight(offset + i, j - i, DumpHighlightKind.Rule, rule.Name, color is { } c ? $"#{c:X6}" : null));
            i = j;
        }

        return list;
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
        int initial = Math.Max(0, formats.ToList().IndexOf(last));
        ComboBox format = DialogParts.Combo("Export_Format", Loc.Get("Transfer_Format"), formats.Select(FormatName), initial);
        RadioButton whole = DialogParts.Radio("Export_Whole", Loc.Get("Export_Whole"), "ExportTarget", !editor.HasSelection);
        RadioButton selection = DialogParts.Radio("Export_Selection", Loc.Get("Export_Selection"), "ExportTarget", editor.HasSelection);
        RadioButton range = DialogParts.Radio("Export_Range", Loc.Get("Export_Range"), "ExportTarget", false);
        selection.IsEnabled = editor.HasSelection;

        // マルチ選択は「範囲ごとに別ファイル」/「つなげて 1 つのファイル」を選ぶ (仕様 1)。
        bool multi = editor.HasMultipleRanges;
        (ComboBox multiMode, TextBox multiPattern) = MultiRangeControls("Export", PerRangePattern(formats[initial]));
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
            new TextBlock { Text = Loc.Get("Export_Target") }, whole, selection, multiMode, multiPattern, range, rangeStart, rangeLength, format,
            optionsHost,
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
        string current = formats[initial];
        DumpOptions dumpDefaults = DumpDefaults(doc);
        IReadOnlyList<(long Offset, long Length)> Ranges()
        {
            if (selection.IsChecked == true && editor.HasSelection)
            {
                return SelectionRangeList(editor);
            }

            if (range.IsChecked == true && Evaluate(rangeStart.Text) is { } s && Evaluate(rangeLength.Text) is { } l)
            {
                long start = Math.Min(s, doc.Document.Length);
                return [(start, Math.Max(0, Math.Min(l, doc.Document.Length - start)))];
            }

            return [(0, doc.Document.Length)];
        }

        ExportOptions Options() => TransferOptions.ToExportOptions(current, values, Evaluate, dumpDefaults);

        // 欄の既定値: インポート時の値 (実行開始アドレス・S0 の文字列) とファイル名、画面の文字コード (TOOL-05・06・10)。
        ExportDefaults defaults = ExportDefaults.From(doc.DisplayName, doc.ImportedValues, editor.TextEncoding.Id);
        IReadOnlyList<TransferField> fields = [];
        void Rebuild()
        {
            string previous = current;
            current = formats[Math.Max(0, format.SelectedIndex)];
            fields = TransferOptions.ExportFields(current, editor.View, defaults);
            values = TransferOptions.Load(AppState.GetString("export.options." + current, "{}"), fields);
            optionsHost.Content = BuildFields(fields, values, state.Fields, state.Setters, "Export_Opt_", Refresh);
            if (pathBox.Text.Length > 0)
            {
                pathBox.Text = Path.ChangeExtension(pathBox.Text, FormatIds.Extension(current));
            }

            // 範囲ごとのファイル名の形式は、変えていなければ形式の拡張子に合わせる。
            if (multiPattern.Text == PerRangePattern(previous))
            {
                multiPattern.Text = PerRangePattern(current);
            }

            Refresh();
        }

        void Refresh()
        {
            bool rangeOk = range.IsChecked != true || Evaluate(rangeStart.Text) is not null && Evaluate(rangeLength.Text) is not null;
            rangeStart.Visibility = rangeLength.Visibility = range.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            bool multiShown = multi && selection.IsChecked == true;
            bool perRange = multiShown && multiMode.SelectedIndex == 0;
            multiMode.Visibility = multiShown ? Visibility.Visible : Visibility.Collapsed;
            multiPattern.Visibility = perRange ? Visibility.Visible : Visibility.Collapsed;
            bool patternOk = !perRange || RangeFileNames.ExpandAll(multiPattern.Text, doc.DisplayName, Ranges()) is not null;
            DialogParts.MarkInvalid(multiPattern, !patternOk);
            rangeOk &= patternOk;
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
        multiMode.SelectionChanged += (_, _) => Refresh();
        multiPattern.TextChanged += (_, _) => Refresh();
        string? picked = null;
        browse.Click += async (_, _) =>
        {
            string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + FormatIds.Extension(current);
            if (TestHooks.TrySavePicker(suggested, out string? chosen))
            {
                pathBox.Text = chosen ?? pathBox.Text;
                picked = chosen;
                return;
            }

            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.Export" };
            picker.FileTypeChoices.Add(FormatName(current), [FormatIds.Extension(current)]);
            if ((await picker.PickSaveFileAsync())?.Path is { } p)
            {
                pathBox.Text = p;
                picked = p; // 保存ダイアログが上書きを確かめた
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

        AppState.SetString("export.options." + current, TransferOptions.Serialize(values, fields));
        AppState.SetString("export.format", current);
        if (multi && selection.IsChecked == true && multiMode.SelectedIndex == 0)
        {
            await RunPerRangeExportAsync(doc, Ranges(), Options(), Path.GetFullPath(pathBox.Text), multiPattern.Text);
            return;
        }

        // 保存先は入力欄で選ぶため、ファイルがあれば上書きを確かめる (保存ダイアログを通らない)。
        string output = Path.GetFullPath(pathBox.Text);
        if (File.Exists(output) && !string.Equals(output, picked is null ? null : Path.GetFullPath(picked), StringComparison.OrdinalIgnoreCase)
            && !await ConfirmOverwriteAsync([Path.GetFileName(output)]))
        {
            return;
        }

        await RunExportAsync(doc, Ranges(), Options(), output);
    }

    /// <summary>
    /// 上書きの確認 (TOOL-16 の仕様 2・TOOL-13 の仕様 5 と同じ: 「上書きする」「キャンセル」)。<paramref name="names"/> はすでにあるファイルの名前。
    /// </summary>
    private async Task<bool> ConfirmOverwriteAsync(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return true;
        }

        string list = string.Join(Environment.NewLine, names.Take(5)) + (names.Count > 5 ? Environment.NewLine + "…" : string.Empty);
        ConfirmChoice choice = await ConfirmAsync(new ConfirmRequest("ExportOverwriteDialog", Loc.Get("Export_OverwriteTitle"),
            Loc.Format("Export_OverwriteBody", names.Count, list), Loc.Get("Export_Overwrite"), null, Loc.Get("Common_Cancel")));
        return choice == ConfirmChoice.Primary;
    }

    /// <summary>選択範囲の一覧 (マルチ選択・矩形選択なら各要素。オフセット順)。</summary>
    private static IReadOnlyList<(long Offset, long Length)> SelectionRangeList(EditorState editor) => editor.HasMultipleRanges
        ? [.. editor.SelectedRanges.Where(r => r.Length > 0).Select(r => (r.Start, r.Length))]
        : [(editor.SelectionStart, editor.SelectionLength)];

    /// <summary>マルチ選択の書き出し方 (範囲ごとに別ファイル / つなげて 1 つのファイル) と、別ファイルのときのファイル名の形式。</summary>
    private static (ComboBox Mode, TextBox Pattern) MultiRangeControls(string prefix, string pattern = RangeFileNames.DefaultPattern)
    {
        ComboBox mode = DialogParts.Combo(prefix + "_MultiMode", Loc.Get("Export_MultiMode"),
            [Loc.Get("Export_MultiPerRange"), Loc.Get("Export_MultiConcatenate")], 1);
        TextBox box = DialogParts.Field(prefix + "_MultiPattern", Loc.Get("Export_MultiPattern"), pattern, monospace: false);
        ToolTipService.SetToolTip(box, Loc.Get("Export_MultiPatternHelp"));
        return (mode, box);
    }

    /// <summary>
    /// 範囲ごとのファイル名の既定の形式。<c>{base}</c> は元のドキュメントの名前 (TOOL-13 と同じ記号)。バイナリは元の拡張子 (<c>{ext}</c>)、
    /// それ以外は形式の拡張子。
    /// </summary>
    private static string PerRangePattern(string format) =>
        format == FormatIds.Binary ? RangeFileNames.DefaultPattern : "{base}_{start}" + FormatIds.Extension(format);

    /// <summary>
    /// マルチ選択を範囲ごとに別ファイルに書き出す (TOOL-04・TOOL-16 の仕様 1)。<paramref name="path"/> のフォルダに、形式で作った名前で書く。
    /// 1 つが失敗・キャンセルされたら、残りは書かない。
    /// </summary>
    private async Task<bool> RunPerRangeExportAsync(DocumentViewModel doc, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions options,
        string path, string pattern)
    {
        // {name}・{base}・{ext} は元のドキュメントの名前から作る (TOOL-13 と同じ記号)。保存先として選んだファイルは、書き出すフォルダを決める。
        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        if (RangeFileNames.ExpandAll(pattern, doc.DisplayName, ranges) is not { } names)
        {
            ShowNotice(Loc.Get("Export_MultiPatternHelp"), InfoBarSeverity.Warning, doc);
            return false;
        }

        // 保存先にあるファイルは、書き始める前にまとめて上書きを確かめる (TOOL-16 の仕様 2)。
        if (!await ConfirmOverwriteAsync(RangeFileNames.Existing(folder, names)))
        {
            return false;
        }

        for (int i = 0; i < ranges.Count; i++)
        {
            if (!await RunExportAsync(doc, [ranges[i]], options, Path.Combine(folder, names[i])))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>テスト用: 開いている「選択範囲をファイルに保存」の小さなダイアログ。</summary>
    internal SaveRangeDialogState? SaveRangeForTest { get; private set; }

    /// <summary>「選択範囲をファイルに保存」の小さなダイアログの部品 (テスト用の命令が読み書きする)。</summary>
    internal sealed class SaveRangeDialogState
    {
        public required ContentDialog Dialog { get; init; }

        public required RadioButton Selection { get; init; }

        public required RadioButton Range { get; init; }

        public required TextBox Start { get; init; }

        public required TextBox Length { get; init; }

        public required ComboBox Mode { get; init; }

        public required TextBox Pattern { get; init; }
    }

    /// <summary>
    /// 選択範囲 (既定) またはオフセットの範囲 (入力式) を、そのままのバイト列で別のファイルに保存する (エクスポートの形式「バイナリ」と同じ処理。仕様 1・4)。
    /// 選択範囲が 1 つなら、すぐに保存ダイアログを出す (上書きの確認は保存ダイアログが出す。仕様 2)。選択がない・マルチ選択の場合は、対象と書き出し方を
    /// 選ぶ小さなダイアログを先に出す。マルチ選択の「範囲ごとに別ファイル」は、保存先にあるファイルの上書きをまとめて確かめる。一時ファイルに書いてから
    /// 置き換える (仕様 3)。
    /// </summary>
    private async Task SaveSelectionAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        IReadOnlyList<(long Offset, long Length)> ranges = editor.HasSelection ? SelectionRangeList(editor) : [];
        string? perRangePattern = null;
        if (ranges.Count != 1)
        {
            if (await ChooseSaveRangeAsync(doc, ranges) is not { } choice)
            {
                return;
            }

            (ranges, perRangePattern) = choice;
        }

        (long first, long firstLength) = ranges[0];
        string suggested = perRangePattern is not null && RangeFileNames.ExpandAll(perRangePattern, doc.DisplayName, ranges) is { } names
            ? names[0]
            : Path.GetFileNameWithoutExtension(doc.DisplayName) + $"_{first:X}-{first + firstLength - 1:X}.bin";
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

        var binary = new ExportOptions { Format = FormatIds.Binary };
        if (perRangePattern is not null)
        {
            await RunPerRangeExportAsync(doc, ranges, binary, Path.GetFullPath(path), perRangePattern);
            return;
        }

        await RunExportAsync(doc, ranges, binary, Path.GetFullPath(path));
    }

    /// <summary>
    /// 対象 (選択範囲 / オフセットの範囲 (入力式)) と、マルチ選択の書き出し方 (範囲ごとに別ファイル / つなげて 1 つのファイル) を選ぶ小さなダイアログ
    /// (TOOL-16 の仕様 1・画面)。キャンセルなら null。範囲ごとに別ファイルなら、ファイル名の形式を返す。
    /// </summary>
    private async Task<(IReadOnlyList<(long Offset, long Length)> Ranges, string? Pattern)?> ChooseSaveRangeAsync(DocumentViewModel doc,
        IReadOnlyList<(long Offset, long Length)> selected)
    {
        EditorState editor = doc.Editor;
        bool hasSelection = selected.Count > 0;
        RadioButton selection = DialogParts.Radio("SaveRange_Selection", Loc.Get("Export_Selection"), "SaveRangeTarget", hasSelection);
        RadioButton range = DialogParts.Radio("SaveRange_Range", Loc.Get("Export_Range"), "SaveRangeTarget", !hasSelection);
        selection.IsEnabled = hasSelection;
        TextBox start = DialogParts.Field("SaveRange_Start", Loc.Get("OpenAdv_Start"), hasSelection ? $"0x{selected[0].Offset:X}" : "cur");
        TextBox length = DialogParts.Field("SaveRange_Length", Loc.Get("OpenAdv_Length"), "end");
        (ComboBox mode, TextBox pattern) = MultiRangeControls("SaveRange");
        var body = new StackPanel { Spacing = 8, MinWidth = 380 };
        foreach (UIElement e in new UIElement[] { selection, mode, pattern, range, start, length })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "SaveRangeDialog", Loc.Get("SaveSelection_MultiTitle"), body, Loc.Get("SaveSelection_Continue"));
        var context = new EditorExpressionContext(editor);
        long? Evaluate(string text) => DialogParts.TryEvaluate(text, context, out long v, out _) && v >= 0 ? v : null;
        (long Offset, long Length)? OffsetRange()
        {
            if (Evaluate(start.Text) is not { } s || Evaluate(length.Text) is not { } l)
            {
                return null;
            }

            long from = Math.Min(s, doc.Document.Length);
            long count = Math.Min(l, doc.Document.Length - from);
            return count > 0 ? (from, count) : null;
        }

        void Refresh()
        {
            bool byRange = range.IsChecked == true;
            bool multi = !byRange && selected.Count > 1;
            mode.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            pattern.Visibility = multi && mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            start.Visibility = length.Visibility = byRange ? Visibility.Visible : Visibility.Collapsed;
            bool rangeOk = !byRange || OffsetRange() is not null;
            bool patternOk = !multi || mode.SelectedIndex != 0 || RangeFileNames.ExpandAll(pattern.Text, doc.DisplayName, selected) is not null;
            DialogParts.MarkInvalid(start, !rangeOk);
            DialogParts.MarkInvalid(pattern, !patternOk);
            dialog.IsPrimaryButtonEnabled = rangeOk && patternOk;
        }

        selection.Checked += (_, _) => Refresh();
        range.Checked += (_, _) => Refresh();
        mode.SelectionChanged += (_, _) => Refresh();
        pattern.TextChanged += (_, _) => Refresh();
        start.TextChanged += (_, _) => Refresh();
        length.TextChanged += (_, _) => Refresh();
        Refresh();
        var state = new SaveRangeDialogState { Dialog = dialog, Selection = selection, Range = range, Start = start, Length = length, Mode = mode, Pattern = pattern };
        SaveRangeForTest = state;
        ContentDialogResult answer;
        try
        {
            answer = await dialog.ShowQueuedAsync();
        }
        finally
        {
            if (SaveRangeForTest == state)
            {
                SaveRangeForTest = null;
            }
        }

        if (answer != ContentDialogResult.Primary)
        {
            return null;
        }

        if (range.IsChecked == true)
        {
            return OffsetRange() is { } r ? ([r], null) : null;
        }

        return (selected, selected.Count > 1 && mode.SelectedIndex == 0 ? pattern.Text : null);
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
