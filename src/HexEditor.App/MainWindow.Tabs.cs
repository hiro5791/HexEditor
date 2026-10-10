using System.Collections.Specialized;
using System.ComponentModel;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using HexEditor.Core.Tabs;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App;

/// <summary>
/// タブ (UI-09): 最近使った順の切り替え (Ctrl+Tab)、並び順の切り替え (Ctrl+PageDown)、同名のファイルの区別、右クリックメニュー、
/// 「すべてのタブ」の一覧。並べ替えとピン留め (UI-10)。セッションから復元したタブを初めて表示したときに開く (UI-31 の仕様 6)。
/// ドラッグとウィンドウの間の移動は MainWindow.TabDrag.cs。
/// </summary>
public sealed partial class MainWindow
{
    private readonly TabMru<DocumentViewModel> _mru = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _mruTimer;
    private Popup? _switcher;
    private bool _materializing;

    /// <summary>タブの初期化 (コンストラクターから呼ぶ)。</summary>
    private void InitializeTabs()
    {
        // 他のアプリの書き込みを禁止できなかったら、その文書の中で知らせる (ENG-15 の仕様 2)。
        Vm.LockFailed += (_, doc) => DispatcherQueue.TryEnqueue(() => ShowLockFailed(doc));

        // タブ列の項目 (文書とページのタブ) と選択 (MainWindow.TabItems.cs)。
        InitializeTabItems();

        // 幅は 100〜240 px (TabView の既定の最小・最大幅)。入りきらなければ横にスクロールする (UI-09 の仕様 5)。
        Tabs.TabWidthMode = TabViewWidthMode.SizeToContent;
        Tabs.TabStripFooter = CreateAllTabsButton();

        Vm.PropertyChanged += Vm_SelectedChanged;
        Vm.Documents.CollectionChanged += TabDocuments_CollectionChanged;
        InitializeTabDrag();

        // 長時間処理の実行中のタブに進捗リングを出す (UI-09 の仕様 2)。処理の一覧はアプリ全体で 1 つなので、閉じたら購読をやめる。
        EventHandler operationsChanged = (_, _) => DispatcherQueue.TryEnqueue(UpdateBusyTabs);
        Vm.Operations.Changed += operationsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                Vm.Operations.Changed -= operationsChanged;
                _mruTimer?.Stop();
            }
        };
    }

    private void UpdateBusyTabs()
    {
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            doc.IsBusy = Vm.Operations.ActiveFor(doc.Document).Count > 0;
        }
    }

    private void Vm_SelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Selected) || Vm.IsRearranging || Vm.Selected is not { } doc)
        {
            return;
        }

        // まだ開いていない復元したタブは、初めて表示したときに開く (UI-31 の仕様 6)。
        if (doc.IsPending && !_materializing)
        {
            MaterializePending(doc);
            return;
        }

        _mru.Touch(doc);
    }

    private void TabDocuments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocumentViewModel removed in e.OldItems?.OfType<DocumentViewModel>() ?? [])
        {
            removed.PropertyChanged -= Document_NameChanged;

            // 復元したタブを開いて置き換えるときは、最近使った順の位置を引き継ぐ (MaterializePending)。
            if (!Vm.Documents.Contains(removed) && !_materializing)
            {
                _mru.Remove(removed);
            }
        }

        foreach (DocumentViewModel added in e.NewItems?.OfType<DocumentViewModel>() ?? [])
        {
            added.PropertyChanged -= Document_NameChanged;
            added.PropertyChanged += Document_NameChanged;
        }

        UpdateTabNames();
    }

    private void Document_NameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.DisplayName))
        {
            UpdateTabNames();
        }
    }

    /// <summary>同じファイル名のタブに、区別できる最小の親フォルダ名を付ける (UI-09 の仕様 3)。</summary>
    private void UpdateTabNames()
    {
        var docs = Vm.Documents.ToList();
        string[] suffixes = TabNames.Suffixes([.. docs.Select(d => (d.DisplayName, d.FilePath ?? d.PendingRecord?.Path ?? d.MissingPath))]);
        for (int i = 0; i < docs.Count; i++)
        {
            docs[i].NameSuffix = suffixes[i];
        }
    }

    /// <summary>復元したタブを開く (UI-31 の仕様 6)。同じ位置に開き、見つからなければ「ファイルが見つかりません」のタブにする。</summary>
    private void MaterializePending(DocumentViewModel pending)
    {
        SessionTab tab = pending.PendingRecord!;
        int index = Vm.Documents.IndexOf(pending);
        _materializing = true;
        try
        {
            Vm.RemovePending(pending);
            DocumentViewModel? opened = Vm.OpenRestored(
                tab,
                (path, readOnly) => TryOpen(path, index, readOnly, restorePosition: false),
                vm => ShowNotice(Loc.Get("Session_FileChanged"), InfoBarSeverity.Informational, vm),
                index);
            opened ??= Vm.AddMissing(tab, index);
            _mru.Replace(pending, opened);
            Vm.Selected = opened;
            _mru.Touch(opened);
        }
        finally
        {
            _materializing = false;
        }

        UpdateTitle();
    }

    // ---- コマンド ----

    private void RegisterTabCommands()
    {
        Func<CommandState> twoTabs = () => Vm.Documents.Count < 2 ? CommandState.Unavailable(Loc.Get("Command_OnlyTab")) : CommandState.Available;
        Commands.Register("tab.next", () => StepRecentTab(forward: true), twoTabs);
        Commands.Register("tab.previous", () => StepRecentTab(forward: false), twoTabs);
        Commands.Register("tab.nextInOrder", () => StepTabInOrder(forward: true), twoTabs);
        Commands.Register("tab.previousInOrder", () => StepTabInOrder(forward: false), twoTabs);
        Commands.Register("tab.togglePin", () =>
        {
            if (Vm.Selected is { } doc)
            {
                Vm.SetPinned(doc, !doc.IsPinned);
            }
        }, () => Vm.Selected is { } d ? Toggle(d.IsPinned) : NeedsDocument());
        Commands.Register("tab.moveLeft", () => MoveSelectedTab(right: false), () => CanMoveSelected(right: false));
        Commands.Register("tab.moveRight", () => MoveSelectedTab(right: true), () => CanMoveSelected(right: true));
        Commands.Register("tab.moveToNewWindow", () =>
        {
            if (Vm.Selected is { } doc)
            {
                MoveTabToNewWindow(doc, null);
            }
        }, () => NeedsDocument(_ => Vm.Documents.Count < 2 ? Loc.Get("Command_OnlyTab") : null));

        // 一括で閉じる操作は、ピン留めしたタブを閉じない (UI-10 の仕様 3)。
        Commands.Register("file.closeAll", () => CloseTabsAsync(TabCloseSet.All, Vm.Selected), NeedsDocument);
        Commands.Register("file.closeOthers", () => CloseTabsAsync(TabCloseSet.Others, Vm.Selected), NeedsDocument);
        Commands.Register("file.closeToRight", () => CloseTabsAsync(TabCloseSet.ToRight, Vm.Selected), NeedsDocument);
        Commands.Register("file.closeSaved", () => CloseTabsAsync(TabCloseSet.Saved, Vm.Selected), NeedsDocument);
        RegisterWindowCommands();
    }

    private CommandState CanMoveSelected(bool right) => NeedsDocument(d =>
        TabStripRules.Step([.. Vm.Documents.Select(x => x.IsPinned)], Vm.Documents.IndexOf(d), right) is null ? Loc.Get("Command_TabAtEdge") : null);

    /// <summary>「タブ: 左へ移動 / 右へ移動」(UI-10 の仕様 1)。</summary>
    private void MoveSelectedTab(bool right)
    {
        if (Vm.Selected is { } doc
            && TabStripRules.Step([.. Vm.Documents.Select(x => x.IsPinned)], Vm.Documents.IndexOf(doc), right) is int target)
        {
            Vm.MoveDocument(doc, target);
        }
    }

    /// <summary>右クリックメニューの「閉じる」の範囲 (UI-09 の仕様 9)。ピン留めしたタブは一括の操作では閉じない (UI-10 の仕様 3)。</summary>
    private async Task CloseTabsAsync(TabCloseSet set, DocumentViewModel? target)
    {
        var docs = Vm.Documents.ToList();
        int index = target is null ? -1 : docs.IndexOf(target);
        IReadOnlyList<int> targets = TabStripRules.CloseTargets(set, [.. docs.Select(d => d.IsPinned)], [.. docs.Select(d => d.Document.IsModified)], index);
        if (targets.Count > 0)
        {
            await CloseAsync([.. targets.Select(i => docs[i])]);
        }
    }

    /// <summary>Ctrl+PageDown / Ctrl+PageUp: 見出しの並び順で次 / 前のタブ (UI-09 の仕様 6)。</summary>
    private void StepTabInOrder(bool forward)
    {
        int index = Vm.Selected is { } doc ? Vm.Documents.IndexOf(doc) : -1;
        int next = TabStripRules.Cycle(Vm.Documents.Count, index, forward);
        if (next >= 0)
        {
            Vm.Selected = Vm.Documents[next];
        }
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab: 最近使った順で切り替える (UI-09 の仕様 6)。Ctrl を押している間は候補の一覧を出し、押すたびに次の候補へ。
    /// Ctrl を離したら決める。
    /// </summary>
    private void StepRecentTab(bool forward)
    {
        if (_mru.Step([.. Vm.Documents], forward) is not { } next)
        {
            return;
        }

        Vm.Selected = next;
        if (IsCtrlDown())
        {
            ShowSwitcher();
        }

        if (_mruTimer is null)
        {
            _mruTimer = DispatcherQueue.CreateTimer();
            _mruTimer.Interval = TimeSpan.FromMilliseconds(50);
            _mruTimer.Tick += (_, _) =>
            {
                if (IsCtrlDown())
                {
                    return;
                }

                _mruTimer.Stop();
                HideSwitcher();
                if (_mru.Commit() is { } chosen && Vm.Documents.Contains(chosen) && Vm.Selected != chosen)
                {
                    Vm.Selected = chosen;
                }
            };
        }

        _mruTimer.Start();
    }

    private static bool IsCtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Ctrl+Tab の候補の一覧 (Ctrl を押している間だけ出す)。</summary>
    private void ShowSwitcher()
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            MinWidth = 280,
            MaxHeight = 400,
        };
        AutomationProperties.SetAutomationId(list, "TabSwitcher");
        AutomationProperties.SetName(list, Loc.Get("Tab_Switcher"));
        foreach (DocumentViewModel doc in _mru.Candidates)
        {
            var text = new TextBlock { Text = doc.TabTitle };
            ToolTipService.SetToolTip(text, doc.ToolTip);
            list.Items.Add(text);
        }

        list.SelectedIndex = _mru.CandidateIndex;
        var border = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Padding='4' CornerRadius='8' BorderThickness='1' "
            + "Background='{ThemeResource AcrylicInAppFillColorDefaultBrush}' BorderBrush='{ThemeResource SurfaceStrokeColorFlyoutBrush}' />");
        border.Child = list;
        border.RequestedTheme = Root.ActualTheme;
        if (_switcher is null)
        {
            _switcher = new Popup { XamlRoot = Root.XamlRoot, IsLightDismissEnabled = false };
            Root.Children.Add(_switcher);
        }

        _switcher.Child = border;
        border.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _switcher.HorizontalOffset = Math.Max(0, (Root.ActualWidth - border.DesiredSize.Width) / 2);
        _switcher.VerticalOffset = 96;
        _switcher.IsOpen = true;
    }

    private void HideSwitcher()
    {
        if (_switcher is not null)
        {
            _switcher.IsOpen = false;
            _switcher.Child = null;
        }
    }

    // ---- 「すべてのタブ」(UI-09 の仕様 5) ----

    private DropDownButton CreateAllTabsButton()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var button = new DropDownButton
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Flyout = menu,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };
        AutomationProperties.SetAutomationId(button, "AllTabsButton");
        AutomationProperties.SetName(button, Loc.Get("Tab_AllTabs"));
        ToolTipService.SetToolTip(button, Loc.Get("Tab_AllTabs"));
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (DocumentViewModel doc in Vm.Documents)
            {
                var item = new ToggleMenuFlyoutItem { Text = doc.DisplayName + doc.NameSuffix, IsChecked = doc == Vm.Selected };
                ToolTipService.SetToolTip(item, doc.ToolTip);
                DocumentViewModel target = doc;
                item.Click += (_, _) => Vm.Selected = target;
                menu.Items.Add(item);
            }
        };
        return button;
    }

    // ---- タブの右クリックメニュー (UI-09 の仕様 9) ----

    /// <summary>右クリックメニューの項目 (テストからも同じ一覧を使う)。</summary>
    private sealed record TabMenuEntry(string Id, string Text, bool Enabled, Action? Execute, bool? Checked = null, IReadOnlyList<TabMenuEntry>? Children = null);

    /// <summary>XAML の x:Uid と同じリソース (名前/Text) の文字列。</summary>
    private static string MenuText(string uid) => Loc.Get(uid + "/Text");

    private void TabMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout || (flyout.Target as FrameworkElement)?.DataContext is not DocumentViewModel doc)
        {
            return;
        }

        flyout.Items.Clear();
        foreach (MenuFlyoutItemBase item in TabMenuEntries(doc).Select(CreateMenuItem))
        {
            flyout.Items.Add(item);
        }
    }

    private static MenuFlyoutItemBase CreateMenuItem(TabMenuEntry? entry)
    {
        if (entry is null)
        {
            return new MenuFlyoutSeparator();
        }

        MenuFlyoutItemBase item;
        if (entry.Children is { } children)
        {
            var sub = new MenuFlyoutSubItem { Text = entry.Text, IsEnabled = entry.Enabled };
            foreach (TabMenuEntry child in children)
            {
                sub.Items.Add(CreateMenuItem(child));
            }

            item = sub;
        }
        else
        {
            MenuFlyoutItem flyoutItem = entry.Checked is { } on ? new ToggleMenuFlyoutItem { IsChecked = on } : new MenuFlyoutItem();
            flyoutItem.Text = entry.Text;
            flyoutItem.IsEnabled = entry.Enabled;
            Action? run = entry.Execute;
            flyoutItem.Click += (_, _) => run?.Invoke();
            item = flyoutItem;
        }

        AutomationProperties.SetAutomationId(item, entry.Id);
        return item;
    }

    /// <summary>
    /// 右クリックメニュー: 閉じる、他のタブを閉じる、右側のタブを閉じる、保存済みのタブを閉じる、すべて閉じる、ピン留め / ピン留めを外す、
    /// 新しいウィンドウに移動、別のウィンドウに移動 > (ウィンドウ一覧)、パスをコピー、エクスプローラーで表示、読み取り専用。null は区切り。
    /// </summary>
    private List<TabMenuEntry?> TabMenuEntries(DocumentViewModel doc)
    {
        int index = Vm.Documents.IndexOf(doc);
        bool others = Vm.Documents.Any(d => d != doc && !d.IsPinned);
        bool right = Vm.Documents.Skip(index + 1).Any(d => !d.IsPinned);
        var windows = WindowManager.Windows.Where(w => w != this).ToList();
        string? path = doc.FilePath ?? doc.PendingRecord?.Path ?? doc.MissingPath;
        return
        [
            new("TabMenu_Close", MenuText("Tab_Close"), true, () => _ = CloseTabsAsync(TabCloseSet.This, doc)),
            new("TabMenu_CloseOthers", MenuText("Tab_CloseOthers"), others, () => _ = CloseTabsAsync(TabCloseSet.Others, doc)),
            new("TabMenu_CloseRight", MenuText("Tab_CloseRight"), right, () => _ = CloseTabsAsync(TabCloseSet.ToRight, doc)),
            new("TabMenu_CloseSaved", Loc.Get("Tab_CloseSaved"), Vm.Documents.Any(d => !d.IsPinned && !d.Document.IsModified),
                () => _ = CloseTabsAsync(TabCloseSet.Saved, doc)),
            new("TabMenu_CloseAll", Loc.Get("Tab_CloseAll"), Vm.Documents.Any(d => !d.IsPinned), () => _ = CloseTabsAsync(TabCloseSet.All, doc)),
            null,
            new("TabMenu_Pin", Loc.Get(doc.IsPinned ? "Tab_Unpin" : "Tab_Pin"), true, () => Vm.SetPinned(doc, !doc.IsPinned)),
            null,
            new("TabMenu_MoveToNewWindow", Loc.Get("Tab_MoveToNewWindow"), Vm.Documents.Count > 1, () => MoveTabToNewWindow(doc, null)),
            new("TabMenu_MoveToWindow", Loc.Get("Tab_MoveToWindow"), windows.Count > 0, null, Children:
            [
                .. windows.Select(w => new TabMenuEntry(
                    "TabMenu_MoveToWindow_" + WindowManager.NumberOf(w),
                    Loc.Format("Tab_WindowItem", WindowManager.NumberOf(w), w.Vm.Selected?.TabTitle ?? Loc.Get("Tab_WindowEmpty")),
                    true,
                    () => MoveTab(doc, this, w, null))),
            ]),
            null,
            new("TabMenu_CopyPath", Loc.Get("Tab_CopyPath"), path is not null, () =>
            {
                var package = new DataPackage();
                package.SetText(path!);
                SystemClipboard.SetContent(package);
            }),
            new("TabMenu_RevealInExplorer", Loc.Get("Tab_RevealInExplorer"), path is not null && File.Exists(path), () => ShowInExplorer(path!)),
            new("TabMenu_ReadOnly", Loc.Get("Tab_ReadOnly"), !doc.IsPending && !doc.IsMissing, () =>
            {
                Vm.Selected = doc;
                _ = Commands.ExecuteAsync("edit.readOnly");
            }, Checked: doc.Editor.ReadOnly),

            // デコードして開いたドキュメントの「形式の設定...」(TOOL-11 の仕様 2)。
            .. doc.Encoded is null ? Array.Empty<TabMenuEntry?>() :
            [
                new("TabMenu_FormatSettings", Loc.Get("Tab_FormatSettings"), true, () =>
                {
                    Vm.Selected = doc;
                    _ = Commands.ExecuteAsync("file.formatSettings");
                }),
            ],
        ];
    }
}
