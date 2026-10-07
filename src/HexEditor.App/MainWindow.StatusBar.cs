using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Operations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>ステータスバー (UI-06) と処理センターの進捗表示 (UI-37)。</summary>
public sealed partial class MainWindow
{
    public const string StatusItemsKey = "ui.statusBar.items";

    /// <summary>項目の ID (設定 ui.statusBar.items と右クリックメニューに使う)。</summary>
    private static readonly string[] StatusItemIds =
        ["cursor", "value", "selection", "column", "encoding", "mode", "modified", "size", "operations", "notifications"];

    /// <summary>幅が足りないときに隠す順 (UI-06 の仕様 5)。カーソル位置・選択範囲・入力モード・変更の有無・処理センターは隠さない。</summary>
    private static readonly string[] CollapseOrder = ["column", "value", "size-short", "encoding", "size", "notifications"];

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _operationsTimer;
    private bool _statusLayoutPending;

    private IEnumerable<Button> StatusButtons => StatusItems.Children.OfType<Button>();

    private void InitializeStatusBar()
    {
        foreach (string id in StatusItemIds)
        {
            var item = new ToggleMenuFlyoutItem { Text = Loc.Get("Status_Item_" + char.ToUpperInvariant(id[0]) + id[1..]), Tag = id };
            item.Click += (_, _) =>
            {
                SetStatusItemVisible(id, item.IsChecked);
                UpdateStatusBarLayout();
            };
            StatusItemsMenu.Items.Add(item);
        }

        StatusItemsMenu.Opening += (_, _) =>
        {
            foreach (ToggleMenuFlyoutItem item in StatusItemsMenu.Items.OfType<ToggleMenuFlyoutItem>())
            {
                item.IsChecked = IsStatusItemVisible((string)item.Tag);
            }
        };

        // 処理センターの表示 (UI-37 の仕様 1)。処理の数に関係なく、更新は 1 秒に 4 回まで。
        ProcessingCenter.Center = Vm.Operations;
        ProcessingCenter.TargetName = target => Vm.Documents.FirstOrDefault(d => ReferenceEquals(d.Document, target))?.DisplayName;
        _operationsTimer = DispatcherQueue.CreateTimer();
        _operationsTimer.Interval = TimeSpan.FromMilliseconds(250);
        _operationsTimer.Tick += (_, _) => UpdateOperationsStatus();
        _operationsTimer.Start();
        ProcessingCenterFlyout.Opening += (_, _) => ProcessingCenter.Refresh();

        // 値の変化で項目の有無・幅が変わるので、そのたびに並べ直す。
        foreach (Button button in StatusButtons)
        {
            button.SizeChanged += (_, _) => QueueStatusBarLayout();
        }

        UpdateStatusBarLayout();
    }

    private bool IsStatusItemVisible(string id)
    {
        string value = App.Settings.GetString(StatusItemsKey, string.Empty);
        return value.Length == 0 || value.Split(',').Contains(id);
    }

    private void SetStatusItemVisible(string id, bool visible)
    {
        var visibleIds = StatusItemIds.Where(i => i == id ? visible : IsStatusItemVisible(i)).ToList();
        string value = visibleIds.Count == StatusItemIds.Length ? string.Empty : string.Join(',', visibleIds);
        App.Settings.SetString(StatusItemsKey, value, string.Empty);
    }

    private void UpdateOperationsStatus()
    {
        var running = Vm.Operations.Active.Where(op => op.ShouldShow).ToList();
        bool show = running.Count > 0;
        if (show)
        {
            StatusOperationsText.Text = running.Count == 1 ? running[0].Name : Loc.Format("Operations_Count", running.Count);

            // 全体の進捗: 各処理の残りバイト数の合計で計算する。分からない処理だけなら不確定の表示。
            var known = running.Where(op => op.TotalBytes is > 0).ToList();
            StatusOperationsProgress.IsIndeterminate = known.Count == 0;
            if (known.Count > 0)
            {
                double total = known.Sum(op => (double)op.TotalBytes!.Value);
                StatusOperationsProgress.Value = known.Sum(op => (double)op.ProcessedBytes) / total;
            }
        }

        if ((StatusOperations.Visibility == Visibility.Visible) != (show && IsStatusItemVisible("operations")))
        {
            UpdateStatusBarLayout();
        }

        if (ProcessingCenterFlyout.IsOpen)
        {
            ProcessingCenter.Refresh();
        }
    }

    private void StatusBar_SizeChanged(object sender, SizeChangedEventArgs e) => QueueStatusBarLayout();

    private void QueueStatusBarLayout()
    {
        if (_statusLayoutPending)
        {
            return;
        }

        _statusLayoutPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _statusLayoutPending = false;
            UpdateStatusBarLayout();
        });
    }

    /// <summary>
    /// 項目の表示・非表示を決める: 利用者が隠した項目、値のない項目 (選択がないときの選択範囲など) を隠し、
    /// それでも幅が足りなければ優先度の低い項目から隠す (UI-06 の仕様 5)。
    /// </summary>
    private void UpdateStatusBarLayout()
    {
        DocumentViewModel? doc = Vm.Selected;
        bool operations = Vm.Operations.Active.Any(op => op.ShouldShow);
        var wanted = new Dictionary<string, bool>
        {
            ["cursor"] = doc is not null,
            ["value"] = doc is not null && doc.ValueText.Length > 0,
            ["selection"] = doc is not null && doc.Editor.HasSelection,
            ["column"] = doc is not null,
            ["encoding"] = doc is not null,
            ["mode"] = doc is not null,
            ["modified"] = doc is not null && doc.Document.IsModified,
            ["size"] = doc is not null,
            ["operations"] = operations,
            ["notifications"] = true,
        };
        foreach (Button button in StatusButtons)
        {
            string id = (string)button.Tag;
            button.Visibility = wanted[id] && IsStatusItemVisible(id) ? Visibility.Visible : Visibility.Collapsed;
        }

        StatusSize.Content = doc?.SizeText ?? string.Empty;
        double available = StatusBar.ActualWidth - StatusBar.Padding.Left - StatusBar.Padding.Right;
        if (available <= 0)
        {
            return;
        }

        foreach (string step in CollapseOrder)
        {
            StatusItems.Measure(new Windows.Foundation.Size(double.PositiveInfinity, StatusBar.ActualHeight));
            if (StatusItems.DesiredSize.Width <= available)
            {
                break;
            }

            if (step == "size-short")
            {
                // ファイルサイズの括弧内 (正確なバイト数) を省く。
                StatusSize.Content = doc?.SizeText.Split(" (")[0] ?? string.Empty;
                continue;
            }

            Button target = StatusButtons.First(b => (string)b.Tag == step);
            target.Visibility = Visibility.Collapsed;
        }
    }

    // ---- 項目のクリック (UI-06 の仕様 1) ----

    private void StatusCursor_Click(object sender, RoutedEventArgs e) => GoTo_Click(sender, e);

    private void StatusColumn_Click(object sender, RoutedEventArgs e) => Editor?.ToggleColumn();

    private void StatusMode_Click(object sender, RoutedEventArgs e)
    {
        if (Editor is { ReadOnly: false })
        {
            ToggleInsert_Click(sender, e);
        }
    }

    /// <summary>正確なバイト数をクリップボードにコピーする。</summary>
    private void StatusSize_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(doc.Document.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Clipboard.SetContent(package);
        ShowNotice(Loc.Get("Status_SizeCopied"), InfoBarSeverity.Success);
    }
}
