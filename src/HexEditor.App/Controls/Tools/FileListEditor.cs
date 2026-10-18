using System.Collections.ObjectModel;
using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App.Controls.Tools;

/// <summary>
/// ファイルツールのダイアログで使う、ファイル (とフォルダ) の一覧の部品 (TOOL-14 の仕様 1・2、TOOL-17 の仕様 1): 追加 (ファイル選択・ドラッグ &amp; ドロップ)、
/// 削除、上へ・下へ (ボタンのアクセスキーでも操作できる)、ドラッグでの並べ替え、名前順・自然順の並べ替え。
/// 追加の前に <see cref="Accept"/> で確かめ、受け付けないものは追加しない。
/// </summary>
internal sealed class FileListEditor : UserControl
{
    private readonly ListView _list = new();
    private readonly string _prefix;
    private readonly Button _remove;
    private readonly Button _up;
    private readonly Button _down;

    public FileListEditor(string idPrefix, bool sortButtons, bool folderButton)
    {
        _prefix = idPrefix;
        _list.ItemsSource = Items;
        _list.SelectionMode = ListViewSelectionMode.Extended;
        _list.CanReorderItems = true;
        _list.AllowDrop = true;
        _list.MinHeight = 120;
        _list.MaxHeight = 220;
        _list.MinWidth = 420;
        _list.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
        };
        _list.Drop += async (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var deferral = e.GetDeferral();
                IReadOnlyList<Windows.Storage.IStorageItem> dropped = await e.DataView.GetStorageItemsAsync();
                await AddPathsAsync([.. dropped.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p))]);
                deferral.Complete();
            }
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        Items.CollectionChanged += (_, _) =>
        {
            UpdateButtons();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        AutomationProperties.SetAutomationId(_list, idPrefix + "_List");
        AutomationProperties.SetName(_list, Loc.Get("FileList_List"));

        Button add = Button("Add", "FileList_Add", async () =>
        {
            if (PickFiles is { } pick)
            {
                await AddPathsAsync(await pick());
            }
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(add);
        if (folderButton)
        {
            buttons.Children.Add(Button("AddFolder", "FileList_AddFolder", async () =>
            {
                if (PickFolder is { } pick && await pick() is { } folder)
                {
                    await AddPathsAsync([folder]);
                }
            }));
        }

        _remove = Button("Remove", "FileList_Remove", RemoveSelected);
        _up = Button("Up", "FileList_Up", () => Move(-1));
        _down = Button("Down", "FileList_Down", () => Move(1));
        _remove.AccessKey = Loc.Get("FileList_Remove_Key");
        _up.AccessKey = Loc.Get("FileList_Up_Key");
        _down.AccessKey = Loc.Get("FileList_Down_Key");
        buttons.Children.Add(_remove);
        buttons.Children.Add(_up);
        buttons.Children.Add(_down);
        if (sortButtons)
        {
            buttons.Children.Add(Button("SortName", "FileList_SortName", () => Sort(Core.Tools.Files.FileJoiner.SortByName(Items))));
            buttons.Children.Add(Button("SortNatural", "FileList_SortNatural", () => Sort(Core.Tools.Files.FileJoiner.SortNatural(Items))));
        }

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_list);
        panel.Children.Add(new ScrollViewer
        {
            Content = buttons,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        });
        Content = panel;
        UpdateButtons();
    }

    public ObservableCollection<string> Items { get; } = [];

    /// <summary>一覧に追加するファイルを選ばせる。</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFiles { get; set; }

    /// <summary>フォルダを選ばせる (フォルダの追加のボタンがあるとき)。</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>追加してよいか。追加してよいパスの一覧 (連番のファイルの展開などで増減してよい) を返す。</summary>
    public Func<string, Task<IReadOnlyList<string>>>? Accept { get; set; }

    /// <summary>一覧が変わった (追加・削除・並べ替え)。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<string> Selected => [.. _list.SelectedItems.OfType<string>()];

    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            IReadOnlyList<string> accepted = Accept is { } accept ? await accept(path) : [path];
            foreach (string item in accepted)
            {
                if (!Items.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    Items.Add(item);
                }
            }
        }
    }

    public void RemoveSelected()
    {
        foreach (string item in Selected)
        {
            Items.Remove(item);
        }
    }

    public void Move(int delta)
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItem is not string item)
        {
            return;
        }

        int index = Items.IndexOf(item);
        int target = index + delta;
        if (target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        _list.SelectedItem = item;
    }

    public void Sort(IReadOnlyList<string> order)
    {
        string? selected = _list.SelectedItem as string;
        Items.Clear();
        foreach (string item in order)
        {
            Items.Add(item);
        }

        if (selected is not null)
        {
            _list.SelectedItem = selected;
        }
    }

    /// <summary>テスト用: 一覧の項目を選ぶ。</summary>
    internal void TestSelect(int index)
    {
        _list.SelectedItems.Clear();
        _list.SelectedItem = Items[index];
    }

    private void UpdateButtons()
    {
        _remove.IsEnabled = _list.SelectedItems.Count > 0;
        bool one = _list.SelectedItems.Count == 1 && _list.SelectedItem is string;
        int index = one ? Items.IndexOf((string)_list.SelectedItem) : -1;
        _up.IsEnabled = one && index > 0;
        _down.IsEnabled = one && index < Items.Count - 1;
    }

    private Button Button(string name, string textKey, Action click)
    {
        var button = new Button { Content = Loc.Get(textKey) };
        AutomationProperties.SetAutomationId(button, _prefix + "_" + name);
        button.Click += (_, _) => click();
        return button;
    }

    private Button Button(string name, string textKey, Func<Task> click)
    {
        var button = new Button { Content = Loc.Get(textKey) };
        AutomationProperties.SetAutomationId(button, _prefix + "_" + name);
        button.Click += async (_, _) => await click();
        return button;
    }
}
