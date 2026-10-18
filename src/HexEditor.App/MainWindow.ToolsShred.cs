using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Services;
using HexEditor.Core.Operations;
using HexEditor.Core.Tools.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 安全な削除 (シュレッダー。TOOL-17): 対象の一覧 (ファイルとフォルダ)、上書きの方式、確認ダイアログ、処理センターの進捗。
/// アプリで開いているファイル・システムフォルダ・ドライブのルートは対象にできない。書き込み保護モードの間は実行できない。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterToolsShredCommands()
    {
        Commands.Register("tools.files.shred", () => ShowShredDialogAsync(), ShredState);
        WriteBlockMode.Changed += OnWriteBlockChanged;
    }

    private void OnWriteBlockChanged(object? sender, EventArgs e) => Commands.NotifyStatesChanged();

    private CommandState ShredState() =>
        WriteBlockMode.IsEnabled ? CommandState.Unavailable(Loc.Get("WriteBlock_Enabled")) : CommandState.Available;

    /// <summary>アプリ (全ウィンドウ) で開いているファイルのパス。</summary>
    private static IEnumerable<string> OpenFilePaths() =>
        WindowManager.Windows.SelectMany(w => w.Vm.Documents).Select(d => d.FilePath).Where(p => p is not null).Select(p => p!);

    private static string RejectionMessage(ShredRejection rejection, string path) => rejection switch
    {
        ShredRejection.OpenInApp => Loc.Format("Shred_Reject_Open", Path.GetFileName(path)),
        ShredRejection.SystemFolder => Loc.Get("Shred_Reject_System"),
        ShredRejection.DriveRoot => Loc.Get("Shred_Reject_Root"),
        ShredRejection.NotFound => Loc.Format("Shred_Reject_Missing", path),
        _ => string.Empty,
    };

    private async Task ShowShredDialogAsync()
    {
        if (WriteBlockMode.IsEnabled)
        {
            return;
        }

        IShredFileSystem fs = WindowsShredFileSystem.Instance;
        TextBlock message = ToolParts.ErrorText("Shred_Message");
        var list = new FileListEditor("Shred", sortButtons: false, folderButton: true)
        {
            PickFiles = () => PickFilesAsync("HexEditor.ShredFiles", multiple: true),
            PickFolder = () => PickFolderToolAsync("HexEditor.ShredFolder"),
        };
        list.Accept = path =>
        {
            ShredRejection rejection = SecureDelete.Check(path, fs, OpenFilePaths());
            ToolParts.ShowError(message, null, rejection == ShredRejection.None ? null : RejectionMessage(rejection, path));
            return Task.FromResult<IReadOnlyList<string>>(rejection == ShredRejection.None ? [path] : []);
        };

        RadioButton zero = DialogParts.Radio("Shred_Zero", Loc.Get("Shred_Method_Zero"), "ShredMethod", true);
        RadioButton random = DialogParts.Radio("Shred_Random", Loc.Get("Shred_Method_Random"), "ShredMethod", false);
        RadioButton dod = DialogParts.Radio("Shred_Dod", Loc.Get("Shred_Method_Dod"), "ShredMethod", false);
        RadioButton custom = DialogParts.Radio("Shred_Custom", Loc.Get("Shred_Method_Custom"), "ShredMethod", false);
        TextBox passes = DialogParts.Field("Shred_Passes", Loc.Get("Shred_Passes"), "3");
        TextBox patterns = DialogParts.Field("Shred_Patterns", Loc.Get("Shred_Patterns"), "00\nFF\nrandom");
        patterns.AcceptsReturn = true;
        patterns.TextWrapping = TextWrapping.NoWrap;
        patterns.MinHeight = 70;
        CheckBox verify = DialogParts.Check("Shred_Verify", Loc.Get("Shred_Verify"), false);
        TextBlock error = ToolParts.ErrorText("Shred_Error");

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (UIElement element in new UIElement[]
        {
            new TextBlock { Text = Loc.Get("Shred_Targets"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] }, list, message,
            new TextBlock { Text = Loc.Get("Shred_Method"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
            zero, random, dod, custom, passes, patterns, verify, error,
        })
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ShredDialog", Loc.Get("Shred_Title"), new ScrollViewer { Content = body, MaxHeight = 600 }, Loc.Get("Shred_Run"));

        ShredMethod? Method(out string? problem)
        {
            problem = null;
            if (zero.IsChecked == true)
            {
                return ShredMethod.ZeroOnce;
            }

            if (random.IsChecked == true)
            {
                return ShredMethod.RandomOnce;
            }

            if (dod.IsChecked == true)
            {
                return ShredMethod.Dod;
            }

            if (!int.TryParse(passes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 1 or > ShredMethod.MaxCustomPasses)
            {
                problem = Loc.Get("Shred_Error_Passes");
                return null;
            }

            string[] lines = patterns.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<ShredPass>();
            for (int i = 0; i < n; i++)
            {
                string line = lines.Length == 0 ? "00" : lines[Math.Min(i, lines.Length - 1)];
                if (ShredMethod.ParsePass(line) is not { } pass)
                {
                    problem = Loc.Format("Shred_Error_Pattern", line);
                    return null;
                }

                result.Add(pass);
            }

            return ShredMethod.Custom(result);
        }

        void Update()
        {
            passes.IsEnabled = patterns.IsEnabled = custom.IsChecked == true;
            ShredMethod? method = Method(out string? problem);
            ToolParts.ShowError(error, null, problem);
            dialog.IsPrimaryButtonEnabled = list.Items.Count > 0 && method is not null && !WriteBlockMode.IsEnabled;
        }

        list.Changed += (_, _) => Update();
        foreach (RadioButton radio in new[] { zero, random, dod, custom })
        {
            radio.Checked += (_, _) => Update();
        }

        passes.TextChanged += (_, _) => Update();
        patterns.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || Method(out _) is not { } chosen)
        {
            return;
        }

        await ShredAfterConfirmAsync([.. list.Items], chosen, verify.IsChecked == true);
    }

    private static string MethodName(ShredMethod method) => method.Id switch
    {
        "zero" => Loc.Get("Shred_Method_Zero"),
        "random" => Loc.Get("Shred_Method_Random"),
        "dod" => Loc.Get("Shred_Method_Dod"),
        _ => Loc.Format("Shred_Method_CustomSummary", method.Passes.Count),
    };

    /// <summary>
    /// 確認ダイアログ (対象の数・合計サイズ・方式、取り消せないこと、上書きの限界) を出し、「完全に削除する」なら実行する。
    /// </summary>
    internal async Task<ShredResult?> ShredAfterConfirmAsync(IReadOnlyList<string> targets, ShredMethod method, bool verify)
    {
        IShredFileSystem fs = WindowsShredFileSystem.Instance;
        IReadOnlyList<string> files = SecureDelete.ExpandTargets(targets, fs);
        long total = SecureDelete.TotalSize(files, fs);
        var content = new StackPanel { Spacing = 8, MaxWidth = 460 };
        TextBlock Line(string id, string text, bool strong = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (strong)
            {
                block.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
            }

            AutomationProperties.SetAutomationId(block, id);
            content.Children.Add(block);
            return block;
        }

        Line("ShredConfirm_Summary", Loc.Format("Shred_Confirm_Summary", files.Count, Bytes(total), MethodName(method)));
        Line("ShredConfirm_Irreversible", Loc.Get("Shred_Confirm_Irreversible"), strong: true);
        Line("ShredConfirm_LimitSsd", Loc.Get("Shred_Limit_Ssd"));
        Line("ShredConfirm_LimitCopies", Loc.Get("Shred_Limit_Copies"));
        Line("ShredConfirm_LimitFree", Loc.Get("Shred_Limit_Free"));
        if (verify)
        {
            Line("ShredConfirm_Verify", Loc.Get("Shred_Confirm_Verify"));
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ShredConfirmDialog", Loc.Get("Shred_Confirm_Title"), new ScrollViewer { Content = content, MaxHeight = 460 },
            Loc.Get("Shred_Confirm_Primary"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        return await RunShredAsync(targets, files, method, verify);
    }

    /// <summary>確認の後に実行する(長時間処理。キャンセルしたら途中のファイルは残し、壊れていることを知らせる)。</summary>
    internal async Task<ShredResult?> RunShredAsync(IReadOnlyList<string> targets, IReadOnlyList<string> files, ShredMethod method, bool verify)
    {
        if (WriteBlockMode.IsEnabled)
        {
            return null;
        }

        IShredFileSystem fs = WindowsShredFileSystem.Instance;
        string[] folders = [.. targets.Where(fs.DirectoryExists)];
        ShredResult result = await Vm.Operations.RunAsync(Loc.Format("Shred_Operation", files.Count), OperationKind.WritesExternal, null, null,
            op => Task.FromResult(SecureDelete.Execute(files, method, verify, fs, op, op.CancellationToken, folders)));
        if (result.Cancelled)
        {
            await ShowShredIssuesAsync(result);
            ShowNotice(Loc.Format("Shred_Cancelled", result.DeletedFiles), InfoBarSeverity.Warning);
        }
        else if (result.Failures.Count > 0)
        {
            await ShowShredIssuesAsync(result);
        }
        else
        {
            ShowNotice(Loc.Format("Shred_Done", result.DeletedFiles), InfoBarSeverity.Success);
        }

        return result;
    }

    private static string FailureReason(ShredFailure failure) => failure.Kind switch
    {
        ShredFailureKind.InUse => Loc.Get("Shred_Failure_InUse"),
        ShredFailureKind.AccessDenied => Loc.Get("Shred_Failure_Access"),
        ShredFailureKind.NotFound => Loc.Get("Shred_Failure_Missing"),
        _ => failure.Message,
    };

    private async Task ShowShredIssuesAsync(ShredResult result)
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 440 };
        if (result.Failures.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = Loc.Format("Shred_Failures", result.Failures.Count, result.DeletedFiles), TextWrapping = TextWrapping.Wrap });
            var table = new TextBlock
            {
                Text = string.Join(Environment.NewLine, result.Failures.Select(f => $"{f.Path}{Environment.NewLine}    {FailureReason(f)}")),
                FontFamily = ToolParts.Mono,
                FontSize = 12,
                IsTextSelectionEnabled = true,
            };
            AutomationProperties.SetAutomationId(table, "Shred_FailureList");
            body.Children.Add(new ScrollViewer { Content = table, MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        if (result.LeftCorrupted.Count > 0)
        {
            var corrupted = new TextBlock
            {
                Text = Loc.Format("Shred_Corrupted", result.LeftCorrupted.Count) + Environment.NewLine + string.Join(Environment.NewLine, result.LeftCorrupted),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
            AutomationProperties.SetAutomationId(corrupted, "Shred_CorruptedList");
            body.Children.Add(corrupted);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ShredResultDialog", Loc.Get("Shred_ResultTitle"), body, Loc.Get("Common_Close"));
        dialog.CloseButtonText = string.Empty;
        await ShowEditDialogAsync(dialog);
    }
}
