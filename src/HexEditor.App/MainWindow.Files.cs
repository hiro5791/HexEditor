using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Expressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>ファイルを開く (ENG-11) と、サイズを指定して新規作成 (ENG-10 の仕様 2)。</summary>
public sealed partial class MainWindow
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>「開く」: 標準のダイアログで複数のファイルを選べる。前回のフォルダを初期表示する (ENG-11 の仕様 1)。</summary>
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        const string settingsIdentifier = "HexEditor.Open";
        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult(settingsIdentifier);
        if (paths is null)
        {
            var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
            picker.FileTypeFilter.Add("*");
            paths = [.. (await picker.PickMultipleFilesAsync()).Select(r => r.Path)];
        }

        foreach (string path in paths)
        {
            TryOpen(path);
        }

        UpdateTitle();
    }

    /// <summary>
    /// ファイルを開く。開けない場合は理由ごとの文言で知らせ (ENG-11 の「エラー」)、null を返す。書き込めないファイルは
    /// 開いたうえで理由を知らせる。
    /// </summary>
    /// <param name="readOnly">「読み取り専用で開く」(ENG-14)。</param>
    /// <param name="restorePosition">前回の位置を戻す (ENG-16 の仕様 5)。</param>
    private DocumentViewModel? TryOpen(string path, int? insertAt = null, bool readOnly = false, bool restorePosition = true)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            // デバイスのパスはファイルとしては開かない (仕様 5。ディスクを開く ENG-29 はフェーズ 2)。
            ShowNotice(Loc.Format("Error_DevicePath", path), InfoBarSeverity.Error);
            return null;
        }

        if (Directory.Exists(path))
        {
            ShowNotice(Loc.Format("Error_IsFolder", name), InfoBarSeverity.Error);
            return null;
        }

        // 別のウィンドウで開いているファイルは、そのタブをアクティブにする (UI-09 の仕様 1、UI-14)。
        if (WindowManager.FindOpenElsewhere(this, path) is { } elsewhere)
        {
            elsewhere.Window.Vm.Selected = elsewhere.Document;
            WindowManager.MarkActive(elsewhere.Window);
            WindowManager.BringToFront(elsewhere.Window);

            // まだ開いていない復元したタブは、選ぶと開いた文書に置き換わる (UI-31 の仕様 6)。
            return elsewhere.Document.IsPending ? elsewhere.Window.Vm.Selected ?? elsewhere.Document : elsewhere.Document;
        }

        try
        {
            DocumentViewModel doc = Vm.Open(path, insertAt, readOnly, restorePosition);
            AppLog.Debug($"Opened {path}");
            if (doc.Document.ReadOnlyReason == Core.Engine.ReadOnlyReason.FileAttribute)
            {
                // 読み取り専用属性: 理由と「編集を許可する」を出す (ENG-14 の仕様 1、EDIT-16 の仕様 3)。
                ShowNotice(Loc.Get("Notice_OpenedReadOnlyAttribute"), InfoBarSeverity.Informational, doc,
                    actions: [new Core.Notifications.NotificationAction(Loc.Get("ReadOnly_AllowEdit"), () => _ = AllowEditAsync(doc))]);
            }

            return doc;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            ShowNotice(Loc.Format("Error_NotFound", path), InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_AccessDenied", name), InfoBarSeverity.Error);
        }
        catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation)
        {
            IReadOnlyList<string> users = FileLockInfo.ProcessesUsing(path);
            ShowNotice(
                users.Count > 0 ? Loc.Format("Error_InUseBy", name, string.Join(", ", users)) : Loc.Format("Error_InUse", name),
                InfoBarSeverity.Error);
        }
        catch (IOException ex)
        {
            ShowNotice(Loc.Format("Error_Open", name, ex.Message), InfoBarSeverity.Error);
        }

        return null;
    }

    /// <summary>
    /// 「サイズを指定して新規作成」(ENG-10 の仕様 2): サイズ (入力式) と塗りつぶしの値 (Hex バイト列) を入力する。
    /// 不正な値のときは入力欄を赤枠にして「作成」を無効にする。
    /// </summary>
    private async void NewWithSize_Click(object sender, RoutedEventArgs e)
    {
        var size = new TextBox { Header = Loc.Get("NewSize_Size"), Text = "0", FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetAutomationId(size, "NewSize_Size");
        var sizeResult = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], FlowDirection = FlowDirection.LeftToRight };
        var fill = new TextBox { Header = Loc.Get("NewSize_Fill"), Text = "00", FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetAutomationId(fill, "NewSize_Fill");
        var fillResult = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(size);
        body.Children.Add(sizeResult);
        body.Children.Add(fill);
        body.Children.Add(fillResult);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("NewSize_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("NewSize_Create"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "NewSizeDialog");

        long length = 0;
        byte[] pattern = [0];
        void Validate()
        {
            bool sizeOk = ExpressionEvaluator.TryEvaluate(size.Text, NoDocument.Instance, out length, out ExpressionException? sizeError) && length >= 0;
            sizeResult.Text = sizeOk
                ? $"= {Core.View.StatusFormat.Hex(length)} ({Core.View.StatusFormat.Number(length, System.Globalization.CultureInfo.CurrentCulture)})"
                : sizeError is not null ? Loc.Format("GoTo_Error_" + sizeError.Error, sizeError.Detail) : Loc.Get("NewSize_InvalidSize");
            byte[]? bytes = Core.Clipboard.HexText.TryParse(fill.Text);
            bool fillOk = bytes is { Length: >= 1 and <= Core.Engine.GeneratedData.MaxPatternLength };
            pattern = fillOk ? bytes! : pattern;
            fillResult.Text = fillOk ? string.Empty : Loc.Get("NewSize_InvalidFill");
            MarkInvalid(size, !sizeOk);
            MarkInvalid(fill, !fillOk);
            dialog.IsPrimaryButtonEnabled = sizeOk && fillOk;
        }

        size.TextChanged += (_, _) => Validate();
        fill.TextChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        Vm.NewDocument(length, pattern);
        UpdateTitle();
    }

    private static void MarkInvalid(TextBox box, bool invalid)
    {
        box.BorderBrush = invalid ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : null;
        if (!invalid)
        {
            box.ClearValue(Control.BorderBrushProperty);
        }
    }

    /// <summary>文書のない入力式の文脈 (新規作成のサイズ)。カーソルなどの名前は 0。</summary>
    private sealed class NoDocument : IExpressionContext
    {
        public static readonly NoDocument Instance = new();

        public long Cursor => 0;

        public long Length => 0;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }

    /// <summary>
    /// 読み取り専用の解除で、書き込めるようにする (ENG-14 の仕様 3。EDIT-16 の確認の後に呼ぶ)。読み取り専用属性は保存のときに外す
    /// (ENG-22 の仕様 5)。「読み取り専用で開く」・権限・共有違反は、書き込み用に開けるかを確かめ、開けなければ理由を示して false。
    /// </summary>
    private Task<bool> ReopenForWritingAsync(DocumentViewModel doc)
    {
        if (doc.Document.Source is not Core.Sources.FileByteSource file)
        {
            return Task.FromResult(true);
        }

        if (doc.Document.ReadOnlyReason == Core.Engine.ReadOnlyReason.FileAttribute)
        {
            file.ApproveWriting();
            return Task.FromResult(true);
        }

        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle =
                File.OpenHandle(file.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return Task.FromResult(true);
        }
        catch (UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_AccessDenied", doc.DisplayName), InfoBarSeverity.Error, doc);
        }
        catch (IOException ex)
        {
            ShowNotice(Loc.Format("Error_Open", doc.DisplayName, ex.Message), InfoBarSeverity.Error, doc);
        }

        return Task.FromResult(false);
    }
}
