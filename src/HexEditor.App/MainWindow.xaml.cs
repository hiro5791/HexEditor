using System.Runtime.InteropServices;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.View;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace HexEditor.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // ドキュメントの編集画面なので幅 1280 epx 以上で開く。
        double scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(800 * scale)));

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                UpdateTitle();
            }
        };
        Closed += MainWindow_Closed;
    }

    public MainViewModel Vm { get; }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private WindowId WindowId => AppWindow.Id;

    private void UpdateTitle()
    {
        string title = Vm.Selected is { } d ? $"{d.DisplayName} - HexEditor" : "HexEditor";
        Title = title;
        AppTitleBar.Title = title;
    }

    // ---- ファイル ----

    private void New_Click(object sender, RoutedEventArgs e) => Vm.NewDocument();

    private void Tabs_AddTabButtonClick(TabView sender, object args) => Vm.NewDocument();

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(WindowId);
        picker.FileTypeFilter.Add("*");
        PickFileResult? result = await picker.PickSingleFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            Vm.Open(result.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_Open", result.Path, ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await SaveAsync(doc, saveAs: false);
        }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await SaveAsync(doc, saveAs: true);
        }
    }

    /// <summary>保存する。保存しなかった (キャンセル・失敗) 場合は false。</summary>
    private async Task<bool> SaveAsync(DocumentViewModel doc, bool saveAs)
    {
        string? path = doc.FilePath;
        if (saveAs || path is null || !doc.Document.CanSave)
        {
            var picker = new FileSavePicker(WindowId)
            {
                SuggestedFileName = doc.IsUntitled ? doc.DisplayName + ".bin" : doc.DisplayName,
            };
            picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [Path.GetExtension(picker.SuggestedFileName) is { Length: > 0 } ext ? ext : ".bin"]);
            PickFileResult? result = await picker.PickSaveFileAsync();
            if (result is null)
            {
                return false;
            }

            path = result.Path;
            if (Vm.Documents.Any(d => d != doc && string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                ShowNotice(Loc.Get("Error_SaveOpenElsewhere"), InfoBarSeverity.Error);
                return false;
            }
        }

        try
        {
            await Vm.SaveAsync(doc, path);
            UpdateTitle();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InsufficientSpaceException ex)
        {
            ShowNotice(Loc.Format("Error_NoSpace", ex.Drive, ex.Required.ToString("N0"), ex.Available.ToString("N0")), InfoBarSeverity.Error);
        }
        catch (UnreadableDataException)
        {
            ShowNotice(Loc.Get("Error_Unreadable"), InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException)
        {
            ShowNotice(Loc.Get("Error_SaveDenied"), InfoBarSeverity.Error);
        }
        catch (IOException ex)
        {
            ShowNotice(Loc.Format("Error_SaveIo", ex.Message), InfoBarSeverity.Error);
        }

        return false;
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await CloseAsync(doc);
        }
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is DocumentViewModel doc)
        {
            await CloseAsync(doc);
        }
    }

    /// <summary>閉じる (ENG-17、UI-13)。変更があれば保存するかを確かめる。</summary>
    private async Task<bool> CloseAsync(DocumentViewModel doc)
    {
        if (Vm.Operations.ActiveFor(doc.Document).Count > 0)
        {
            ShowNotice(Loc.Get("Notice_Busy"), InfoBarSeverity.Warning);
            return false;
        }

        if (doc.Document.IsModified)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = Loc.Format("Close_Title", doc.DisplayName),
                Content = Loc.Get("Close_Body"),
                PrimaryButtonText = Loc.Get("Close_Save"),
                SecondaryButtonText = Loc.Get("Close_DontSave"),
                CloseButtonText = Loc.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            ContentDialogResult choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None || (choice == ContentDialogResult.Primary && !await SaveAsync(doc, saveAs: false)))
            {
                return false;
            }
        }

        Vm.Close(doc);
        UpdateTitle();
        return true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private bool _closingConfirmed;

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closingConfirmed || !Vm.Documents.Any(d => d.Document.IsModified))
        {
            return;
        }

        args.Handled = true;
        foreach (DocumentViewModel doc in Vm.Documents.ToList())
        {
            Vm.Selected = doc;
            if (!await CloseAsync(doc))
            {
                return;
            }
        }

        _closingConfirmed = true;
        Close();
    }

    // ---- 編集・移動 ----

    private EditorState? Editor => Vm.Selected?.Editor;

    private void Undo_Click(object sender, RoutedEventArgs e) => Editor?.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Editor?.Redo();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Editor?.SelectAll();

    private void ToggleInsert_Click(object sender, RoutedEventArgs e)
    {
        if (Editor?.ToggleInsertMode() == EditResult.FixedLength)
        {
            ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Informational);
        }
    }

    private void GoStart_Click(object sender, RoutedEventArgs e) => Editor?.MoveToStart();

    private void GoEnd_Click(object sender, RoutedEventArgs e) => Editor?.MoveToEnd();

    private readonly List<HexView> _views = [];

    private void HexView_Loaded(object sender, RoutedEventArgs e)
    {
        var view = (HexView)sender;
        if (!_views.Contains(view))
        {
            _views.Add(view);
            view.Unloaded += (_, _) => _views.Remove(view);
        }

        view.Focus(FocusState.Programmatic);
    }

    /// <summary>選択中のタブの Hex ビューにフォーカスを戻す。</summary>
    private void FocusEditor() => _views.FirstOrDefault(v => v.Editor == Editor)?.Focus(FocusState.Programmatic);

    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        if (Editor is null)
        {
            return;
        }

        // 移動バーは検索バーと同じ場所に出す (VIEW-29 の仕様 1)。
        FindBar.Visibility = Visibility.Collapsed;
        GoToBar.Editor = Editor;
        GoToBar.Open();
    }

    private void Bar_Closed(object? sender, EventArgs e) => FocusEditor();

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (Editor is null)
        {
            return;
        }

        GoToBar.Visibility = Visibility.Collapsed;
        FindBar.Editor = Editor;
        FindBar.Operations = Vm.Operations;
        FindBar.Open();
    }

    private async void FindNext_Click(object sender, RoutedEventArgs e) => await FindAgainAsync(forward: true);

    private async void FindPrevious_Click(object sender, RoutedEventArgs e) => await FindAgainAsync(forward: false);

    /// <summary>F3 / Shift+F3。一度も検索していなければ検索バーを開く (FIND-09 の仕様 7)。</summary>
    private async Task FindAgainAsync(bool forward)
    {
        if (Editor is null)
        {
            return;
        }

        if (!FindBar.HasPattern)
        {
            Find_Click(this, new RoutedEventArgs());
            return;
        }

        FindBar.Editor = Editor;
        FindBar.Operations = Vm.Operations;
        await FindBar.FindAsync(forward);
    }

    private void GoBack_Click(object sender, RoutedEventArgs e) => Editor?.GoBack();

    private void GoForward_Click(object sender, RoutedEventArgs e) => Editor?.GoForward();

    private void HexView_EditRejected(object? sender, EditResult result)
    {
        string key = result switch
        {
            EditResult.FixedLength => "Notice_FixedLength",
            EditResult.NotEncodable => "Notice_NotEncodable",
            _ => "Notice_Busy",
        };
        ShowNotice(Loc.Get(key), InfoBarSeverity.Informational);
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }
}
