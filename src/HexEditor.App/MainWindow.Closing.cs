using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Operations;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 閉じる・終了 (ENG-17、UI-13)。未保存の文書が 1 つなら個別の確認、複数ならチェックボックス付きの一覧で確認する。
/// 長時間処理の実行中の文書は、処理を中止して閉じるかを確認する (ENG-09 の仕様 12)。
/// </summary>
public sealed partial class MainWindow
{
    private bool _closingConfirmed;

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await CloseAsync([doc]);
        }
    }

    private async void CloseAll_Click(object sender, RoutedEventArgs e) => await CloseAsync(Vm.Documents.ToList());

    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        // 無題のものは「名前を付けて保存」のダイアログを順に出す (ENG-17 の仕様 7)。
        foreach (DocumentViewModel doc in Vm.Documents.Where(d => d.Document.IsModified).ToList())
        {
            Vm.Selected = doc;
            if (!await SaveAsync(doc, saveAs: false))
            {
                return;
            }
        }
    }

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is DocumentViewModel doc)
        {
            await CloseAsync([doc]);
        }
    }

    // ---- タブの右クリックメニュー ----

    private async void TabCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } doc)
        {
            await CloseAsync(Vm.Documents.Where(d => d != doc).ToList());
        }
    }

    private async void TabCloseRight_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } doc)
        {
            await CloseAsync(Vm.Documents.Skip(Vm.Documents.IndexOf(doc) + 1).ToList());
        }
    }

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } doc)
        {
            await CloseAsync([doc]);
        }
    }

    private static DocumentViewModel? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as DocumentViewModel;

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// ウィンドウを閉じる (終了): 全部の文書をまとめて確認してから閉じ、一時ファイルと復旧用データを消してから終わる
    /// (ENG-17 の仕様 3・9)。
    /// </summary>
    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closingConfirmed)
        {
            return;
        }

        args.Handled = true;
        if (!await CloseAsync(Vm.Documents.ToList()))
        {
            return;
        }

        _closingConfirmed = true;
        App.Settings.Flush();
        AppLog.Info("Exited");
        Close();
    }

    /// <summary>
    /// 文書を閉じる。処理中・未保存のものを確認し、キャンセルされた・保存に失敗した場合は false (その文書は残る)。
    /// </summary>
    private async Task<bool> CloseAsync(IReadOnlyList<DocumentViewModel> docs)
    {
        // 1. 長時間処理の実行中の文書。
        foreach (DocumentViewModel doc in docs.Where(d => Vm.Operations.ActiveFor(d.Document).Count > 0).ToList())
        {
            Vm.Selected = doc;
            if (!await ConfirmBusyAsync(doc))
            {
                return false;
            }
        }

        // 2. 未保存の文書。
        var modified = docs.Where(d => d.Document.IsModified && Vm.Documents.Contains(d)).ToList();
        if (modified.Count == 1)
        {
            Vm.Selected = modified[0];
            if (!await ConfirmSaveOneAsync(modified[0]))
            {
                return false;
            }
        }
        else if (modified.Count > 1 && !await ConfirmSaveManyAsync(modified))
        {
            return false;
        }

        foreach (DocumentViewModel doc in docs.Where(Vm.Documents.Contains).ToList())
        {
            Vm.Close(doc);
        }

        UpdateTitle();
        return true;
    }

    /// <summary>1 つの文書: 「data.bin への変更を保存しますか? (変更: N か所、M バイト)」。</summary>
    private async Task<bool> ConfirmSaveOneAsync(DocumentViewModel doc)
    {
        ChangeSummary changes = ChangeSummary.Of(doc.Document.Current);
        var dialog = NewDialog(
            Loc.Format("Close_Title", doc.DisplayName),
            Loc.Format("Close_BodyChanges", StatusFormat.Number(changes.Places, System.Globalization.CultureInfo.CurrentCulture),
                StatusFormat.Number(changes.Bytes, System.Globalization.CultureInfo.CurrentCulture)));
        dialog.PrimaryButtonText = Loc.Get("Close_Save");
        dialog.SecondaryButtonText = Loc.Get("Close_DontSave");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        ContentDialogResult choice = await dialog.ShowAsync();
        return choice switch
        {
            ContentDialogResult.Primary => await SaveAsync(doc, saveAs: false),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    /// <summary>複数の文書: チェックボックス付きの一覧 (既定ですべてオン)。</summary>
    private async Task<bool> ConfirmSaveManyAsync(IReadOnlyList<DocumentViewModel> docs)
    {
        var list = new StackPanel { Spacing = 4 };
        var boxes = new List<(DocumentViewModel Doc, CheckBox Box)>();
        foreach (DocumentViewModel doc in docs)
        {
            // ファイルのパスは左から右に固定する (UI-44 の仕様 2)。
            var box = new CheckBox
            {
                Content = new TextBlock { Text = doc.FilePath ?? doc.DisplayName, FlowDirection = FlowDirection.LeftToRight },
                IsChecked = true,
            };
            AutomationProperties.SetAutomationId(box, "Close_Item");
            boxes.Add((doc, box));
            list.Children.Add(box);
        }

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Close_ManyBody"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        var dialog = NewDialog(Loc.Format("Close_ManyTitle", docs.Count), body);
        dialog.PrimaryButtonText = Loc.Get("Close_SaveSelected");
        dialog.SecondaryButtonText = Loc.Get("Close_DontSaveAll");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        ContentDialogResult choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return false;
        }

        if (choice == ContentDialogResult.Primary)
        {
            foreach ((DocumentViewModel doc, CheckBox box) in boxes.Where(b => b.Box.IsChecked == true))
            {
                Vm.Selected = doc;

                // 保存に失敗したら、その文書を残して閉じる処理を中断する (ENG-17 の仕様 4)。
                if (!await SaveAsync(doc, saveAs: false))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 処理中の文書: 「<処理名> を実行中です」。「処理を中止して閉じる」はキャンセルして終わるのを待つ。保存中なら
    /// 「保存が終わったら閉じる」も選べる (UI-13 の仕様 3)。
    /// </summary>
    private async Task<bool> ConfirmBusyAsync(DocumentViewModel doc)
    {
        IReadOnlyList<LongRunningOperation> ops = Vm.Operations.ActiveFor(doc.Document);
        if (ops.Count == 0)
        {
            return true;
        }

        bool saving = ops.Any(op => op.Kind == OperationKind.WritesExternal);
        var dialog = NewDialog(doc.DisplayName, Loc.Format("Close_Busy", ops[0].Name));
        dialog.PrimaryButtonText = Loc.Get("Close_CancelOperation");
        if (saving)
        {
            dialog.SecondaryButtonText = Loc.Get("Close_AfterSave");
        }

        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        ContentDialogResult choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return false;
        }

        if (choice == ContentDialogResult.Primary)
        {
            foreach (LongRunningOperation op in ops)
            {
                op.Cancel();
            }
        }

        // 処理が終わるのを待つ (キャンセルは 500 ms 以内に止まる。保存の完了を待つ場合は処理センターで進捗を示す)。
        while (Vm.Operations.ActiveFor(doc.Document).Count > 0)
        {
            await Task.Delay(50);
        }

        // 保存が終わったら閉じる: 保存に失敗して変更が残っていれば閉じない。
        return choice == ContentDialogResult.Primary || !doc.Document.IsModified;
    }

    private ContentDialog NewDialog(string title, object content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = title,
            Content = content is string text ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } : content,
        };
        AutomationProperties.SetAutomationId(dialog, "CloseDialog");
        return dialog;
    }
}
