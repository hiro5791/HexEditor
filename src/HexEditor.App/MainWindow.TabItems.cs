using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>設定画面・ショートカット一覧のタブ (UI-22 の仕様 1、UI-39)。</summary>
public sealed class ToolPageTab(string id, string title, string glyph)
{
    /// <summary>ページの ID (<c>settings</c>、<c>shortcuts</c>)。</summary>
    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public string AutomationId => "ToolPageTab_" + Id;
}

/// <summary>文書のタブとページのタブの見出しの形を選ぶ。</summary>
internal sealed partial class TabTemplateSelector(DataTemplate document, DataTemplate toolPage) : DataTemplateSelector
{
    protected override DataTemplate SelectTemplateCore(object item) => item is ToolPageTab ? toolPage : document;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// タブ列の項目 (UI-09、UI-22): 文書の一覧 (<see cref="MainViewModel.Documents"/>) を写し、その後ろに設定画面などのページのタブを置く。
/// 選択は <see cref="MainViewModel.Selected"/> とつなぐ。ページのタブを選ぶと、ページをエディタ領域に重ねて出す (選んでいた文書はそのまま)。
/// </summary>
public sealed partial class MainWindow
{
    private readonly ObservableCollection<object> _tabItems = [];
    private readonly List<ToolPageTab> _toolTabs = [];
    private bool _syncingTabSelection;

    private void InitializeTabItems()
    {
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            _tabItems.Add(doc);
        }

        Vm.Documents.CollectionChanged += MirrorDocuments;
        Tabs.TabItemTemplateSelector = new TabTemplateSelector((DataTemplate)Tabs.Resources["DocumentTabTemplate"], (DataTemplate)Tabs.Resources["ToolPageTabTemplate"]);
        Tabs.TabItemsSource = _tabItems;
        Tabs.SelectionChanged += Tabs_SelectionChangedSync;
        Tabs.TabCloseRequested += (_, e) =>
        {
            if (e.Item is ToolPageTab tab)
            {
                CloseToolPage(tab.Id);
            }
        };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                SyncTabSelection();
            }
        };
        SyncTabSelection();
    }

    /// <summary>文書の一覧の変化をタブ列に写す (文書のタブは先頭から同じ位置)。</summary>
    private void MirrorDocuments(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _syncingTabSelection = true;
        try
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    for (int i = 0; i < e.NewItems!.Count; i++)
                    {
                        _tabItems.Insert(e.NewStartingIndex + i, e.NewItems[i]!);
                    }

                    break;
                case NotifyCollectionChangedAction.Remove:
                    for (int i = 0; i < e.OldItems!.Count; i++)
                    {
                        _tabItems.RemoveAt(e.OldStartingIndex);
                    }

                    break;
                case NotifyCollectionChangedAction.Move:
                    _tabItems.Move(e.OldStartingIndex, e.NewStartingIndex);
                    break;
                case NotifyCollectionChangedAction.Replace:
                    _tabItems[e.NewStartingIndex] = e.NewItems![0]!;
                    break;
                default:
                    _tabItems.Clear();
                    foreach (DocumentViewModel doc in Vm.Documents)
                    {
                        _tabItems.Add(doc);
                    }

                    foreach (ToolPageTab tab in _toolTabs)
                    {
                        _tabItems.Add(tab);
                    }

                    break;
            }
        }
        finally
        {
            _syncingTabSelection = false;
        }

        SyncTabSelection();
    }

    /// <summary>タブ列の選択を、表示中のページか選んでいる文書に合わせる。</summary>
    private void SyncTabSelection()
    {
        object? target = ActiveToolPage is { } id ? _toolTabs.FirstOrDefault(t => t.Id == id) : Vm.Selected;
        if (target is null || !_tabItems.Contains(target) || ReferenceEquals(Tabs.SelectedItem, target))
        {
            return;
        }

        _syncingTabSelection = true;
        try
        {
            Tabs.SelectedItem = target;
        }
        finally
        {
            _syncingTabSelection = false;
        }
    }

    /// <summary>利用者がタブを選んだ: 文書なら選び、ページを隠す。ページのタブならページを出す。</summary>
    private void Tabs_SelectionChangedSync(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabSelection)
        {
            return;
        }

        switch (Tabs.SelectedItem)
        {
            case DocumentViewModel doc:
                HideToolPages();
                if (Vm.Selected != doc)
                {
                    Vm.Selected = doc;
                }

                break;
            case ToolPageTab tab when ActiveToolPage != tab.Id:
                ShowToolPageTab(tab.Id);
                break;
        }
    }

    /// <summary>ページのタブを加える (なければ) か前に出す。</summary>
    private void AddToolTab(string id)
    {
        if (_toolTabs.Any(t => t.Id == id))
        {
            return;
        }

        var tab = id == "settings"
            ? new ToolPageTab(id, Loc.Get("ToolPage_Settings"), "")
            : new ToolPageTab(id, Loc.Get("ToolPage_Shortcuts"), "");
        _toolTabs.Add(tab);
        _tabItems.Add(tab);
    }

    private void RemoveToolTab(string id)
    {
        if (_toolTabs.FirstOrDefault(t => t.Id == id) is { } tab)
        {
            _toolTabs.Remove(tab);
            _syncingTabSelection = true;
            try
            {
                _tabItems.Remove(tab);
            }
            finally
            {
                _syncingTabSelection = false;
            }
        }
    }

    /// <summary>ページのタブの右クリックメニュー: 「設定 を閉じる」。</summary>
    private void ToolTabMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout || (flyout.Target as FrameworkElement)?.DataContext is not ToolPageTab tab)
        {
            return;
        }

        flyout.Items.Clear();
        var close = new MenuFlyoutItem { Text = Loc.Format("ToolPage_Close", tab.Title) };
        AutomationProperties.SetAutomationId(close, "ToolPageClose_" + tab.Id);
        close.Click += (_, _) => CloseToolPage(tab.Id);
        flyout.Items.Add(close);
    }

    /// <summary>テスト用: ページのタブの一覧。</summary>
    internal IReadOnlyList<string> ToolTabIds => [.. _toolTabs.Select(t => t.Id)];

    /// <summary>
    /// 設定画面は アプリ全体で 1 つ (UI-22 の仕様 1)。別のウィンドウで開いていれば、そのウィンドウを返す。
    /// </summary>
    private MainWindow? SettingsOpenElsewhere() => WindowManager.Windows.FirstOrDefault(w => w != this && w.ToolTabIds.Contains("settings"));
}
