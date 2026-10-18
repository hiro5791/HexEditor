using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Tools.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ファイルツールのうち、分割 (TOOL-13)、結合 (TOOL-14)、インターリーブとデインターリーブ (TOOL-15) のダイアログとつなぎ込み。
/// 長時間処理は処理センターに進捗を出し、キャンセルできる。ダイアログは ContentDialog 1 つで、確認など追加の入力はダイアログの中に出す
/// (ContentDialog は同時に 1 つしか開けない)。
/// </summary>
public sealed partial class MainWindow
{
    private const string FileOpsKey = "fileops.";

    /// <summary>
    /// ファイルツールの入力: 開いているドキュメント (未保存の変更を含む) またはファイル。ドキュメントは変換ビュー (EDIT-45) が有効でも
    /// 元のデータ (保存と同じ <see cref="Core.Engine.Document.DataSnapshot"/>) を使う (11 の TOOL-13 の仕様 1)。
    /// </summary>
    internal sealed record ToolInput(string Name, string DefaultDirectory, long Length, DocumentViewModel? Document, string? Path)
    {
        public IContentReader Open() => Document is { } d ? new SnapshotContentReader(d.Document.DataSnapshot) : new FileContentReader(Path!);
    }

    private void RegisterToolsFileOpsCommands()
    {
        Commands.Register("tools.files.split", () => ShowSplitDialogAsync());
        Commands.Register("tools.files.join", () => ShowJoinDialogAsync());
        Commands.Register("tools.files.interleave", () => ShowInterleaveDialogAsync());
        Commands.Register("tools.files.deinterleave", () => ShowDeinterleaveDialogAsync());
        RegisterToolsShredCommands();
        RegisterAdsCommands();
        RegisterPrintCommands();
    }

    private static string DocumentsFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private ToolInput? DocumentInput() =>
        Vm.Selected is { IsPending: false, IsMissing: false } doc && !doc.Document.IsDisposed
            ? new ToolInput(doc.DisplayName, doc.FilePath is { } p && Path.GetDirectoryName(p) is { } dir ? dir : DocumentsFolder, doc.Document.Length, doc, null)
            : null;

    /// <summary>入力の選択 (アクティブなドキュメント / ファイル)。<paramref name="get"/> は今の入力 (選べていなければ null)。</summary>
    private (StackPanel Panel, Func<ToolInput?> Get) BuildInputSection(string prefix, Action changed)
    {
        ToolInput? doc = DocumentInput();
        RadioButton useDocument = DialogParts.Radio(prefix + "_InputDocument",
            doc is null ? Loc.Get("FileOps_InputDocument") : Loc.Format("FileOps_InputDocumentNamed", doc.Name), prefix + "Input", doc is not null);
        useDocument.IsEnabled = doc is not null;
        RadioButton useFile = DialogParts.Radio(prefix + "_InputFile", Loc.Get("FileOps_InputFile"), prefix + "Input", doc is null);
        TextBox path = DialogParts.Field(prefix + "_FilePath", Loc.Get("FileOps_FilePath"), string.Empty, monospace: false);
        Button browse = ToolParts.TextButton(prefix + "_Browse", "FileOps_Browse", () => { });
        browse.Click += async (_, _) =>
        {
            if ((await PickFilesAsync("HexEditor.FileOpsInput", multiple: false)).FirstOrDefault() is { } picked)
            {
                path.Text = picked;
                useFile.IsChecked = true;
            }
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(path);
        Grid.SetColumn(browse, 1);
        browse.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(browse);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("FileOps_Input"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(useDocument);
        panel.Children.Add(useFile);
        panel.Children.Add(row);
        useDocument.Checked += (_, _) => changed();
        useFile.Checked += (_, _) => changed();
        path.TextChanged += (_, _) =>
        {
            if (path.Text.Length > 0 && useFile.IsChecked != true)
            {
                useFile.IsChecked = true;
            }

            changed();
        };

        ToolInput? Get()
        {
            if (useDocument.IsChecked == true)
            {
                return DocumentInput();
            }

            string text = path.Text.Trim().Trim('"');
            if (text.Length > 0 && File.Exists(text))
            {
                string full = Path.GetFullPath(text);
                try
                {
                    return new ToolInput(Path.GetFileName(full), Path.GetDirectoryName(full)!, new FileInfo(full).Length, null, full);
                }
                catch (IOException)
                {
                    return null;
                }
            }

            return null;
        }

        return (panel, Get);
    }

    private static string Bytes(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private async Task<bool> ConfirmOverwriteAsync(IReadOnlyList<string> existing, string automationId)
    {
        var list = new StackPanel { Spacing = 4 };
        list.Children.Add(new TextBlock { Text = Loc.Format("FileOps_OverwriteBody", existing.Count), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        var names = new TextBlock
        {
            Text = string.Join(Environment.NewLine, existing.Take(10).Select(Path.GetFileName)),
            FontFamily = ToolParts.Mono,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetAutomationId(names, automationId + "_Names");
        list.Children.Add(names);
        return await ConfirmAsync(Loc.Get("FileOps_OverwriteTitle"), list, Loc.Get("FileOps_Overwrite"), automationId);
    }

    private async Task ShowSpaceShortageAsync(SpaceShortage shortage)
    {
        await ShowMessageAsync(Loc.Get("FileOps_NoSpaceTitle"),
            Loc.Format("FileOps_NoSpaceBody", shortage.Drive, Bytes(shortage.Required), Bytes(shortage.Available)), "FileOpsNoSpaceDialog");
    }

    /// <summary>キャンセル・失敗で作りかけが残ったとき、完成したファイルを残すか確認する。残すなら true。</summary>
    private async Task<bool> AskKeepCompletedAsync(int count, string message)
    {
        ContentDialog dialog = DialogParts.Dialog(Root, "FileOpsKeepDialog", Loc.Get("FileOps_KeepTitle"),
            new TextBlock { Text = Loc.Format("FileOps_KeepBody", count, message), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            Loc.Get("FileOps_Keep"), Loc.Get("FileOps_DeleteCompleted"));
        dialog.CloseButtonText = string.Empty;
        return await ShowEditDialogAsync(dialog) != ContentDialogResult.Secondary;
    }

    private async Task ShowFailedFilesAsync(FileToolException error)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap });
        if (error.CreatedFiles.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = Loc.Format("FileOps_CreatedList", error.CreatedFiles.Count), TextWrapping = TextWrapping.Wrap });
            var list = new TextBlock { Text = string.Join(Environment.NewLine, error.CreatedFiles), FontFamily = ToolParts.Mono, IsTextSelectionEnabled = true };
            AutomationProperties.SetAutomationId(list, "FileOps_CreatedFiles");
            body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "FileOpsErrorDialog", Loc.Get("FileOps_ErrorTitle"), body, Loc.Get("Common_Close"));
        dialog.CloseButtonText = string.Empty;
        await ShowEditDialogAsync(dialog);
    }

    private IVolumeInfoProvider Volumes => TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance;

    // ---- 分割 (TOOL-13) ----

    private async Task ShowSplitDialogAsync()
    {
        Action changedHook = () => { };
        (StackPanel inputPanel, Func<ToolInput?> getInput) = BuildInputSection("Split", () => changedHook());
        RadioButton bySize = DialogParts.Radio("Split_BySize", Loc.Get("Split_BySize"), "SplitMode", true);
        RadioButton byCount = DialogParts.Radio("Split_ByCount", Loc.Get("Split_ByCount"), "SplitMode", false);
        RadioButton byPositions = DialogParts.Radio("Split_ByPositions", Loc.Get("Split_ByPositions"), "SplitMode", false);
        TextBox size = DialogParts.Field("Split_Size", Loc.Get("Split_Size"), AppState.GetString(FileOpsKey + "split.size", "4G"));
        TextBox count = DialogParts.Field("Split_Count", Loc.Get("Split_Count"), AppState.GetString(FileOpsKey + "split.count", "2"));
        TextBox align = DialogParts.Field("Split_Align", Loc.Get("Split_Align"), "1");
        TextBox positions = DialogParts.Field("Split_Positions", Loc.Get("Split_Positions"));
        positions.AcceptsReturn = true;
        positions.TextWrapping = TextWrapping.NoWrap;
        positions.MinHeight = 64;
        CheckBox bookmarks = DialogParts.Check("Split_UseBookmarks", Loc.Get("Split_UseBookmarks"), false);
        TextBox folder = DialogParts.Field("Split_OutputFolder", Loc.Get("Split_OutputFolder"), string.Empty, monospace: false);
        Button browseFolder = ToolParts.TextButton("Split_BrowseFolder", "FileOps_Browse", () => { });
        browseFolder.Click += async (_, _) =>
        {
            if (await PickFolderToolAsync("HexEditor.SplitFolder") is { } picked)
            {
                folder.Text = picked;
            }
        };
        TextBox format = DialogParts.Field("Split_NameFormat", Loc.Get("Split_NameFormat"), FileNameTemplate.SplitDefault);
        CheckBox script = DialogParts.Check("Split_JoinScript", Loc.Get("Split_JoinScript"), false);
        CheckBox checksum = DialogParts.Check("Split_Checksum", Loc.Get("Split_Checksum"), false);
        TextBlock summary = DialogParts.Caption("Split_Summary");
        TextBlock error = ToolParts.ErrorText("Split_Error");

        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.Children.Add(folder);
        Grid.SetColumn(browseFolder, 1);
        browseFolder.VerticalAlignment = VerticalAlignment.Bottom;
        folderRow.Children.Add(browseFolder);

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement element in new UIElement[]
        {
            inputPanel, new TextBlock { Text = Loc.Get("Split_Method"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
            bySize, size, byCount, count, align, byPositions, positions, bookmarks,
            new TextBlock { Text = Loc.Get("Split_Output"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
            folderRow, format, script, checksum, summary, error,
        })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "SplitDialog", Loc.Get("Split_Title"), new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Split_Run"));

        SplitOptions? Options(ToolInput input, out string? problem)
        {
            problem = null;
            var context = new CompareDialog.LengthContext(input.Length);
            var options = new SplitOptions
            {
                NameFormat = format.Text.Trim().Length > 0 ? format.Text.Trim() : FileNameTemplate.SplitDefault,
                OutputDirectory = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : null,
                CreateJoinScript = script.IsChecked == true,
                CreateChecksumFile = checksum.IsChecked == true,
            };
            if (bySize.IsChecked == true)
            {
                if (!DialogParts.TryEvaluate(size.Text, context, out long partSize, out ExpressionException? e, DefaultRadix.Decimal))
                {
                    problem = e is null ? Loc.Get("Split_Error_Size") : DialogParts.ExpressionError(e);
                    return null;
                }

                return options with { Mode = SplitMode.Size, PartSize = partSize };
            }

            if (byCount.IsChecked == true)
            {
                if (!int.TryParse(count.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    problem = Loc.Get("Split_Error_Count");
                    return null;
                }

                if (!DialogParts.TryEvaluate(align.Text, context, out long alignment, out ExpressionException? ae, DefaultRadix.Decimal))
                {
                    problem = ae is null ? Loc.Get("Split_Error_Size") : DialogParts.ExpressionError(ae);
                    return null;
                }

                return options with { Mode = SplitMode.Count, Count = n, Alignment = alignment };
            }

            var offsets = new List<long>();
            foreach (string part in positions.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!DialogParts.TryEvaluate(part, context, out long offset, out ExpressionException? pe))
                {
                    problem = pe is null ? Loc.Get("Split_Error_Positions") : DialogParts.ExpressionError(pe);
                    return null;
                }

                offsets.Add(offset);
            }

            if (bookmarks.IsChecked == true && input.Document is { } doc && BookmarkCollection.For(doc.Document) is { } collection)
            {
                offsets.AddRange(FileSplitter.PositionsFromBookmarks(collection));
            }

            return options with { Mode = SplitMode.Positions, Positions = offsets };
        }

        void Update()
        {
            size.IsEnabled = bySize.IsChecked == true;
            count.IsEnabled = align.IsEnabled = byCount.IsChecked == true;
            positions.IsEnabled = bookmarks.IsEnabled = byPositions.IsChecked == true;
            bookmarks.IsEnabled = byPositions.IsChecked == true && getInput()?.Document is not null;
            summary.Text = string.Empty;
            error.Text = string.Empty;
            error.Visibility = Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = false;
            ToolInput? input = getInput();
            if (input is null)
            {
                ToolParts.ShowError(error, null, Loc.Get("FileOps_Error_NoInput"));
                return;
            }

            SplitOptions? options = Options(input, out string? problem);
            if (options is null)
            {
                ToolParts.ShowError(error, null, problem);
                return;
            }

            try
            {
                SplitPlan plan = FileSplitter.Plan(input.Length, input.Name, input.DefaultDirectory, options);
                summary.Text = Loc.Format("Split_Summary", plan.FileCount, plan.FirstName, plan.LastName, Bytes(plan.TotalBytes));
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (FileToolException ex)
            {
                ToolParts.ShowError(error, null, FileToolMessage(ex));
            }
        }

        foreach (RadioButton radio in new[] { bySize, byCount, byPositions })
        {
            radio.Checked += (_, _) => Update();
        }

        foreach (TextBox box in new[] { size, count, align, positions, folder, format })
        {
            box.TextChanged += (_, _) => Update();
        }

        foreach (CheckBox box in new[] { bookmarks, script, checksum })
        {
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
        }

        changedHook = Update;
        dialog.Opened += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || getInput() is not { } chosen
            || Options(chosen, out _) is not { } chosenOptions)
        {
            return;
        }

        AppState.SetString(FileOpsKey + "split.size", size.Text);
        AppState.SetString(FileOpsKey + "split.count", count.Text);
        await RunSplitAsync(chosen, chosenOptions);
    }

    private static string FileToolMessage(FileToolException ex) => ex.Error switch
    {
        FileToolError.NothingToDo => Loc.Get("FileOps_Error_NothingToDo"),
        FileToolError.InvalidArgument => Loc.Get("FileOps_Error_Argument"),
        FileToolError.InvalidNames => Loc.Get("FileOps_Error_Names"),
        FileToolError.LengthMismatch => Loc.Get("Interleave_Error_Length"),
        FileToolError.Unreadable => Loc.Get("FileOps_Error_Unreadable"),
        _ => ex.Message,
    };

    /// <summary>分割を実行する (上書きの確認、空き容量の確認、処理センターの進捗、キャンセル・失敗の後始末)。</summary>
    internal async Task<SplitResult?> RunSplitAsync(ToolInput input, SplitOptions options)
    {
        SplitPlan plan;
        try
        {
            plan = FileSplitter.Plan(input.Length, input.Name, input.DefaultDirectory, options);
        }
        catch (FileToolException ex)
        {
            ShowNotice(FileToolMessage(ex), InfoBarSeverity.Error);
            return null;
        }

        // 空き容量が足りなければ始めない(仕様 6)。
        if (FileSplitter.CheckSpace(plan, Volumes) is { } shortage)
        {
            await ShowSpaceShortageAsync(shortage);
            return null;
        }

        if (plan.ExistingFiles.Count > 0 && !await ConfirmOverwriteAsync(plan.ExistingFiles, "SplitOverwriteDialog"))
        {
            return null;
        }

        IContentReader reader = input.Open();
        SplitResult result;
        try
        {
            result = await Vm.Operations.RunAsync(Loc.Format("Split_Operation", input.Name), OperationKind.WritesExternal, null, plan.TotalBytes,
                op => Task.FromResult(FileSplitter.Execute(plan, reader, options, op, op.CancellationToken)));
        }
        finally
        {
            reader.Dispose();
        }

        return await FinishFileToolAsync(result, Loc.Format("Split_Done", result.Completed.Count, plan.OutputDirectory));
    }

    /// <summary>ファイルツールの結果の後始末: 成功の通知、キャンセル・失敗なら完成したファイルを残すかの確認と一覧。</summary>
    private async Task<SplitResult> FinishFileToolAsync(SplitResult result, string doneMessage)
    {
        if (result.Succeeded)
        {
            ShowNotice(doneMessage, InfoBarSeverity.Success);
        }
        else if (result.Cancelled)
        {
            if (result.Completed.Count > 0 && !await AskKeepCompletedAsync(result.Completed.Count, Loc.Get("FileOps_Cancelled")))
            {
                FileSplitter.DeleteCompleted(result.Completed);
                ShowNotice(Loc.Get("FileOps_CancelledDeleted"), InfoBarSeverity.Informational);
            }
            else
            {
                ShowNotice(Loc.Get("FileOps_Cancelled"), InfoBarSeverity.Informational);
            }
        }
        else if (result.Error is { } error)
        {
            await ShowFailedFilesAsync(error);
        }

        return result;
    }

    // ---- 結合 (TOOL-14) ----

    private async Task ShowJoinDialogAsync()
    {
        var list = new FileListEditor("Join", sortButtons: true, folderButton: false);
        TaskCompletionSource<bool>? pending = null;
        var sequencePanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        var sequenceText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
        AutomationProperties.SetAutomationId(sequenceText, "Join_SequenceText");
        Button sequenceAll = ToolParts.TextButton("Join_SequenceAll", "Join_SequenceAll", () => pending?.TrySetResult(true));
        Button sequenceOne = ToolParts.TextButton("Join_SequenceOne", "Join_SequenceOne", () => pending?.TrySetResult(false));
        sequencePanel.Children.Add(sequenceText);
        sequencePanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sequenceAll, sequenceOne } });

        RadioButton padNone = DialogParts.Radio("Join_PadNone", Loc.Get("Join_PadNone"), "JoinPad", true);
        RadioButton padAlign = DialogParts.Radio("Join_PadAlign", Loc.Get("Join_PadAlign"), "JoinPad", false);
        TextBox align = DialogParts.Field("Join_Align", Loc.Get("Join_Align"), "512");
        TextBox padValue = DialogParts.Field("Join_PadValue", Loc.Get("Join_PadValue"), "00");
        RadioButton outNew = DialogParts.Radio("Join_OutNew", Loc.Get("Join_OutNew"), "JoinOut", true);
        RadioButton outFile = DialogParts.Radio("Join_OutFile", Loc.Get("Join_OutFile"), "JoinOut", false);
        TextBox outPath = DialogParts.Field("Join_OutPath", Loc.Get("Join_OutPath"), string.Empty, monospace: false);
        TextBlock total = DialogParts.Caption("Join_Total");
        TextBlock error = ToolParts.ErrorText("Join_Error");

        ContentDialog? dialog = null;
        list.PickFiles = () => PickFilesAsync("HexEditor.JoinFiles", multiple: true);
        list.Accept = async path =>
        {
            if (!File.Exists(path))
            {
                ToolParts.ShowError(error, null, Loc.Format("Join_Error_Missing", path));
                return [];
            }

            IReadOnlyList<string> sequence = FileJoiner.FindSequence(path);
            if (sequence.Count <= 1)
            {
                return [path];
            }

            // 連番のファイル: 同じ名前の連番のファイルをまとめて追加するか確認する(ダイアログの中に出す)。
            sequenceText.Text = Loc.Format("Join_SequenceBody", Path.GetFileName(sequence[1]), sequence.Count - 1);
            sequencePanel.Visibility = Visibility.Visible;
            pending = new TaskCompletionSource<bool>();
            bool all = await pending.Task;
            sequencePanel.Visibility = Visibility.Collapsed;
            return all ? sequence : [path];
        };

        JoinOptions Options()
        {
            long alignTo = 0;
            if (padAlign.IsChecked == true && DialogParts.TryEvaluate(align.Text, new CompareDialog.LengthContext(0), out long a, out _, DefaultRadix.Decimal))
            {
                alignTo = a;
            }

            byte pad = 0;
            string hex = padValue.Text.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                hex = hex[2..];
            }

            if (hex.Length is 1 or 2 && byte.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte parsed))
            {
                pad = parsed;
            }

            return new JoinOptions { AlignTo = alignTo, PadValue = pad };
        }

        bool Valid(out string? problem)
        {
            problem = null;
            if (list.Items.Count == 0)
            {
                problem = Loc.Get("Join_Error_Empty");
                return false;
            }

            if (padAlign.IsChecked == true)
            {
                if (!DialogParts.TryEvaluate(align.Text, new CompareDialog.LengthContext(0), out long a, out _, DefaultRadix.Decimal) || a < 1)
                {
                    problem = Loc.Get("Join_Error_Align");
                    return false;
                }

                string hex = padValue.Text.Trim();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    hex = hex[2..];
                }

                if (hex.Length is not (1 or 2) || !byte.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                {
                    problem = Loc.Get("Join_Error_PadValue");
                    return false;
                }
            }

            if (outFile.IsChecked == true && outPath.Text.Trim().Length == 0)
            {
                problem = Loc.Get("Join_Error_OutPath");
                return false;
            }

            return true;
        }

        void Update()
        {
            align.IsEnabled = padValue.IsEnabled = padAlign.IsChecked == true;
            outPath.IsEnabled = outFile.IsChecked == true;
            long sum = 0;
            foreach (string path in list.Items)
            {
                try
                {
                    sum += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
            }

            total.Text = Loc.Format("Join_Total", list.Items.Count, Bytes(sum));
            bool ok = Valid(out string? problem);
            ToolParts.ShowError(error, null, ok || list.Items.Count == 0 ? null : problem);
            if (dialog is not null)
            {
                dialog.IsPrimaryButtonEnabled = ok;
            }
        }

        list.Changed += (_, _) => Update();
        foreach (RadioButton radio in new[] { padNone, padAlign, outNew, outFile })
        {
            radio.Checked += (_, _) => Update();
        }

        align.TextChanged += (_, _) => Update();
        padValue.TextChanged += (_, _) => Update();
        outPath.TextChanged += (_, _) => Update();

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement element in new UIElement[]
        {
            new TextBlock { Text = Loc.Get("Join_Files"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] }, list, sequencePanel, total,
            padNone, padAlign, align, padValue, outNew, outFile, outPath, error,
        })
        {
            body.Children.Add(element);
        }

        dialog = DialogParts.Dialog(Root, "JoinDialog", Loc.Get("Join_Title"), new ScrollViewer { Content = body, MaxHeight = 600 }, Loc.Get("Join_Run"));
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        await RunJoinAsync([.. list.Items], Options(), outFile.IsChecked == true ? outPath.Text.Trim() : null);
    }

    /// <summary>結合を実行する。<paramref name="outputPath"/> が null なら新しいドキュメントとして開く(データはコピーしない)。</summary>
    internal async Task<bool> RunJoinAsync(IReadOnlyList<string> files, JoinOptions options, string? outputPath)
    {
        try
        {
            JoinPlan plan = await Task.Run(() => FileJoiner.Plan(files, options));
            if (!plan.CanRun)
            {
                var message = new TextBlock
                {
                    Text = Loc.Get("Join_Error_Unreadable") + Environment.NewLine + string.Join(Environment.NewLine, plan.Unreadable.Select(u => $"{u.Path}: {u.Reason}")),
                    TextWrapping = TextWrapping.Wrap,
                };
                AutomationProperties.SetAutomationId(message, "Join_UnreadableList");
                ContentDialog dialog = DialogParts.Dialog(Root, "JoinUnreadableDialog", Loc.Get("FileOps_ErrorTitle"), message, Loc.Get("Common_Close"));
                dialog.CloseButtonText = string.Empty;
                await ShowEditDialogAsync(dialog);
                return false;
            }

            if (outputPath is null)
            {
                string name = Loc.Format("Join_DocumentName", Path.GetFileName(files[0]));
                Document doc = await Task.Run(() => FileJoiner.CreateDocument(plan, options, name, Vm.DocumentOptions));
                Vm.AddGenerated(doc, name);
                return true;
            }

            string full = Path.GetFullPath(outputPath);
            if (File.Exists(full) && !await ConfirmOverwriteAsync([full], "JoinOverwriteDialog"))
            {
                return false;
            }

            if (full.Equals(files.Select(Path.GetFullPath).FirstOrDefault(f => f.Equals(full, StringComparison.OrdinalIgnoreCase)), StringComparison.OrdinalIgnoreCase))
            {
                ShowNotice(Loc.Get("Join_Error_SameFile"), InfoBarSeverity.Error);
                return false;
            }

            FileJoiner.JoinWriteResult result = await Vm.Operations.RunAsync(Loc.Format("Join_Operation", Path.GetFileName(full)), OperationKind.WritesExternal, null,
                plan.TotalLength, op => Task.FromResult(FileJoiner.WriteToFile(plan, options, full, op, Volumes, op.CancellationToken)));
            ShowNotice(Loc.Format("Join_Done", Path.GetFileName(full)), InfoBarSeverity.Success);
            if (result.ChangedDuringJoin.Count > 0)
            {
                ShowNotice(Loc.Format("Join_ChangedDuringJoin", string.Join(", ", result.ChangedDuringJoin.Select(Path.GetFileName))), InfoBarSeverity.Warning);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.Get("FileOps_Cancelled"), InfoBarSeverity.Informational);
            return false;
        }
        catch (FileToolException ex) when (ex.Space is { } shortage)
        {
            await ShowSpaceShortageAsync(shortage);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Join_Error_Write", ex.Message), InfoBarSeverity.Error);
            return false;
        }
    }

    // ---- デインターリーブ (TOOL-15) ----

    private async Task ShowDeinterleaveDialogAsync()
    {
        Action changedHook = () => { };
        (StackPanel inputPanel, Func<ToolInput?> getInput) = BuildInputSection("Deint", () => changedHook());
        TextBox countBox = DialogParts.Field("Deint_Count", Loc.Get("Deint_Count"), AppState.GetString(FileOpsKey + "deint.count", "2"));
        ComboBox unitCombo = DialogParts.Combo("Deint_Unit", Loc.Get("Deint_Unit"),
            [Loc.Get("Deint_Unit1"), Loc.Get("Deint_Unit2"), Loc.Get("Deint_Unit4"), Loc.Get("Deint_Unit8"), Loc.Get("Deint_UnitCustom")], 0);
        TextBox unitBox = DialogParts.Field("Deint_UnitCustom", Loc.Get("Deint_UnitBytes"), "3");
        RadioButton pad = DialogParts.Radio("Deint_Pad", Loc.Get("Deint_Pad"), "DeintRest", true);
        RadioButton keep = DialogParts.Radio("Deint_Keep", Loc.Get("Deint_Keep"), "DeintRest", false);
        TextBox padValue = DialogParts.Field("Deint_PadValue", Loc.Get("Deint_PadValue"), "FF");
        TextBox folder = DialogParts.Field("Deint_OutputFolder", Loc.Get("Split_OutputFolder"), string.Empty, monospace: false);
        TextBox format = DialogParts.Field("Deint_NameFormat", Loc.Get("Split_NameFormat"), FileNameTemplate.DeinterleaveDefault);
        TextBlock summary = DialogParts.Caption("Deint_Summary");
        TextBlock error = ToolParts.ErrorText("Deint_Error");

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement element in new UIElement[] { inputPanel, countBox, unitCombo, unitBox, pad, keep, padValue, folder, format, summary, error })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "DeinterleaveDialog", Loc.Get("Deint_Title"), new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Deint_Run"));

        DeinterleaveOptions? Options(out string? problem)
        {
            problem = null;
            if (!int.TryParse(countBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 2 or > Interleaver.MaxCount)
            {
                problem = Loc.Get("Deint_Error_Count");
                return null;
            }

            int unit;
            if (unitCombo.SelectedIndex < 4)
            {
                unit = 1 << unitCombo.SelectedIndex;
            }
            else if (!int.TryParse(unitBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out unit) || unit < 1 || unit > Interleaver.MaxUnit)
            {
                problem = Loc.Get("Deint_Error_Unit");
                return null;
            }

            if (!TryParseByte(padValue.Text, out byte fill))
            {
                problem = Loc.Get("Join_Error_PadValue");
                return null;
            }

            return new DeinterleaveOptions
            {
                Count = n,
                Unit = unit,
                Remainder = pad.IsChecked == true ? RemainderMode.Pad : RemainderMode.KeepInLast,
                PadValue = fill,
                NameFormat = format.Text.Trim().Length > 0 ? format.Text.Trim() : FileNameTemplate.DeinterleaveDefault,
                OutputDirectory = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : null,
            };
        }

        void Update()
        {
            unitBox.IsEnabled = unitCombo.SelectedIndex == 4;
            padValue.IsEnabled = pad.IsChecked == true;
            summary.Text = string.Empty;
            ToolParts.ShowError(error, null, null);
            dialog.IsPrimaryButtonEnabled = false;
            ToolInput? input = getInput();
            if (input is null)
            {
                ToolParts.ShowError(error, null, Loc.Get("FileOps_Error_NoInput"));
                return;
            }

            DeinterleaveOptions? options = Options(out string? problem);
            if (options is null)
            {
                ToolParts.ShowError(error, null, problem);
                return;
            }

            try
            {
                DeinterleavePlan plan = Interleaver.PlanDeinterleave(input.Length, input.Name, input.DefaultDirectory, options);
                summary.Text = Loc.Format("Deint_Summary", plan.Outputs.Count, string.Join(", ", plan.Outputs.Select(o => Bytes(o.Length))));
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (FileToolException ex)
            {
                ToolParts.ShowError(error, null, FileToolMessage(ex));
            }
        }

        countBox.TextChanged += (_, _) => Update();
        unitBox.TextChanged += (_, _) => Update();
        padValue.TextChanged += (_, _) => Update();
        folder.TextChanged += (_, _) => Update();
        format.TextChanged += (_, _) => Update();
        unitCombo.SelectionChanged += (_, _) => Update();
        pad.Checked += (_, _) => Update();
        keep.Checked += (_, _) => Update();
        changedHook = Update;
        dialog.Opened += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || getInput() is not { } chosen || Options(out _) is not { } chosenOptions)
        {
            return;
        }

        AppState.SetString(FileOpsKey + "deint.count", countBox.Text);
        await RunDeinterleaveAsync(chosen, chosenOptions);
    }

    private static bool TryParseByte(string text, out byte value)
    {
        string hex = text.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        value = 0;
        return hex.Length is 1 or 2 && byte.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    internal async Task<SplitResult?> RunDeinterleaveAsync(ToolInput input, DeinterleaveOptions options)
    {
        DeinterleavePlan plan;
        try
        {
            plan = Interleaver.PlanDeinterleave(input.Length, input.Name, input.DefaultDirectory, options);
        }
        catch (FileToolException ex)
        {
            ShowNotice(FileToolMessage(ex), InfoBarSeverity.Error);
            return null;
        }

        if (Interleaver.CheckSpace(plan, Volumes) is { } shortage)
        {
            await ShowSpaceShortageAsync(shortage);
            return null;
        }

        if (plan.ExistingFiles.Count > 0 && !await ConfirmOverwriteAsync(plan.ExistingFiles, "DeinterleaveOverwriteDialog"))
        {
            return null;
        }

        IContentReader reader = input.Open();
        SplitResult result;
        try
        {
            result = await Vm.Operations.RunAsync(Loc.Format("Deint_Operation", input.Name), OperationKind.WritesExternal, null, plan.InputLength,
                op => Task.FromResult(Interleaver.Deinterleave(plan, reader, options, op, op.CancellationToken)));
        }
        finally
        {
            reader.Dispose();
        }

        return await FinishFileToolAsync(result, Loc.Format("Deint_Done", result.Completed.Count, plan.OutputDirectory));
    }

    // ---- インターリーブ (TOOL-15) ----

    private async Task ShowInterleaveDialogAsync()
    {
        var list = new FileListEditor("Inter", sortButtons: false, folderButton: false)
        {
            PickFiles = () => PickFilesAsync("HexEditor.InterleaveFiles", multiple: true),
        };
        ComboBox unitCombo = DialogParts.Combo("Inter_Unit", Loc.Get("Deint_Unit"),
            [Loc.Get("Deint_Unit1"), Loc.Get("Deint_Unit2"), Loc.Get("Deint_Unit4"), Loc.Get("Deint_Unit8"), Loc.Get("Deint_UnitCustom")], 0);
        TextBox unitBox = DialogParts.Field("Inter_UnitCustom", Loc.Get("Deint_UnitBytes"), "3");
        RadioButton padShort = DialogParts.Radio("Inter_PadShort", Loc.Get("Inter_PadShort"), "InterMismatch", true);
        RadioButton errorOnMismatch = DialogParts.Radio("Inter_ErrorOnMismatch", Loc.Get("Inter_ErrorOnMismatch"), "InterMismatch", false);
        TextBox padValue = DialogParts.Field("Inter_PadValue", Loc.Get("Deint_PadValue"), "FF");
        RadioButton outNew = DialogParts.Radio("Inter_OutNew", Loc.Get("Join_OutNew"), "InterOut", true);
        RadioButton outFile = DialogParts.Radio("Inter_OutFile", Loc.Get("Join_OutFile"), "InterOut", false);
        TextBox outPath = DialogParts.Field("Inter_OutPath", Loc.Get("Join_OutPath"), string.Empty, monospace: false);
        TextBlock summary = DialogParts.Caption("Inter_Summary");
        TextBlock error = ToolParts.ErrorText("Inter_Error");

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement element in new UIElement[]
        {
            new TextBlock { Text = Loc.Get("Inter_Inputs"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] }, list, unitCombo, unitBox,
            padShort, errorOnMismatch, padValue, outNew, outFile, outPath, summary, error,
        })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "InterleaveDialog", Loc.Get("Inter_Title"), new ScrollViewer { Content = body, MaxHeight = 600 }, Loc.Get("Inter_Run"));

        InterleaveOptions? Options(out string? problem)
        {
            problem = null;
            int unit;
            if (unitCombo.SelectedIndex < 4)
            {
                unit = 1 << unitCombo.SelectedIndex;
            }
            else if (!int.TryParse(unitBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out unit) || unit < 1 || unit > Interleaver.MaxUnit)
            {
                problem = Loc.Get("Deint_Error_Unit");
                return null;
            }

            if (!TryParseByte(padValue.Text, out byte fill))
            {
                problem = Loc.Get("Join_Error_PadValue");
                return null;
            }

            return new InterleaveOptions
            {
                Unit = unit,
                Mismatch = padShort.IsChecked == true ? LengthMismatchMode.Pad : LengthMismatchMode.Error,
                PadValue = fill,
            };
        }

        void Update()
        {
            unitBox.IsEnabled = unitCombo.SelectedIndex == 4;
            padValue.IsEnabled = padShort.IsChecked == true;
            outPath.IsEnabled = outFile.IsChecked == true;
            summary.Text = string.Empty;
            ToolParts.ShowError(error, null, null);
            dialog.IsPrimaryButtonEnabled = false;
            if (list.Items.Count < 2)
            {
                ToolParts.ShowError(error, null, Loc.Get("Inter_Error_Count"));
                return;
            }

            if (list.Items.Count > Interleaver.MaxCount)
            {
                ToolParts.ShowError(error, null, Loc.Get("Inter_Error_Count"));
                return;
            }

            InterleaveOptions? options = Options(out string? problem);
            if (options is null)
            {
                ToolParts.ShowError(error, null, problem);
                return;
            }

            try
            {
                long[] lengths = [.. list.Items.Select(p => new FileInfo(p).Length)];
                long length = Interleaver.InterleavedLength(lengths, options);
                summary.Text = Loc.Format("Inter_Summary", list.Items.Count, Bytes(length));
                if (outFile.IsChecked == true && outPath.Text.Trim().Length == 0)
                {
                    ToolParts.ShowError(error, null, Loc.Get("Join_Error_OutPath"));
                    return;
                }

                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (FileToolException ex)
            {
                ToolParts.ShowError(error, null, FileToolMessage(ex));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ToolParts.ShowError(error, null, ex.Message);
            }
        }

        list.Changed += (_, _) => Update();
        unitBox.TextChanged += (_, _) => Update();
        padValue.TextChanged += (_, _) => Update();
        outPath.TextChanged += (_, _) => Update();
        unitCombo.SelectionChanged += (_, _) => Update();
        foreach (RadioButton radio in new[] { padShort, errorOnMismatch, outNew, outFile })
        {
            radio.Checked += (_, _) => Update();
        }

        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || Options(out _) is not { } chosen)
        {
            return;
        }

        await RunInterleaveAsync([.. list.Items], chosen, outFile.IsChecked == true ? outPath.Text.Trim() : null);
    }

    /// <summary>インターリーブを実行する。<paramref name="outputPath"/> が null なら新しいドキュメントとして開く。</summary>
    internal async Task<bool> RunInterleaveAsync(IReadOnlyList<string> files, InterleaveOptions options, string? outputPath)
    {
        var readers = new List<IContentReader>();
        try
        {
            foreach (string file in files)
            {
                readers.Add(new FileContentReader(file));
            }

            long length = Interleaver.InterleavedLength([.. readers.Select(r => r.Length)], options);
            if (outputPath is null)
            {
                string name = Loc.Format("Inter_DocumentName", Path.GetFileName(files[0]));
                var doc = new Document(Core.Sources.MemoryByteSource.CreateEmpty(name), Vm.DocumentOptions);
                try
                {
                    EditContent content = await Vm.Operations.RunAsync(Loc.Format("Inter_Operation", files.Count), OperationKind.ReadOnly, null, length,
                        op => Task.FromResult(Interleaver.InterleaveToContent(readers, options, doc, op, op.CancellationToken)));
                    doc.InsertContent(0, content, Loc.Get("Inter_Title"));
                    doc.ClearHistory();
                    Vm.AddGenerated(doc, name);
                }
                catch
                {
                    doc.Dispose();
                    throw;
                }

                return true;
            }

            string full = Path.GetFullPath(outputPath);
            if (File.Exists(full) && !await ConfirmOverwriteAsync([full], "InterleaveOverwriteDialog"))
            {
                return false;
            }

            await Vm.Operations.RunAsync(Loc.Format("Inter_Operation", files.Count), OperationKind.WritesExternal, null, length, op =>
            {
                Interleaver.InterleaveToFile(readers, options, full, op, op.CancellationToken);
                return Task.CompletedTask;
            });
            ShowNotice(Loc.Format("Inter_Done", Path.GetFileName(full)), InfoBarSeverity.Success);
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.Get("FileOps_Cancelled"), InfoBarSeverity.Informational);
            return false;
        }
        catch (FileToolException ex) when (ex.Space is { } shortage)
        {
            await ShowSpaceShortageAsync(shortage);
            return false;
        }
        catch (FileToolException ex)
        {
            ShowNotice(FileToolMessage(ex), InfoBarSeverity.Error);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Join_Error_Write", ex.Message), InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            foreach (IContentReader reader in readers)
            {
                reader.Dispose();
            }
        }
    }
}
