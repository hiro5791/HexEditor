using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.App.ViewModels;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>スクリプト一覧 (AUTO-19) の表示と操作。状態は <see cref="ScriptListViewModel"/>。</summary>
public sealed partial class ScriptListPanel : UserControl, Panels.IPanelContent
{
    private readonly IScriptListHost _host;

    internal ScriptListPanel(ScriptListViewModel vm, IScriptListHost host)
    {
        Vm = vm;
        _host = host;
        InitializeComponent();
        AutomationProperties.SetName(FilterBox, Loc.Get("ScriptList_Filter"));
        FilterBox.PlaceholderText = Loc.Get("ScriptList_Filter");
        AutomationProperties.SetName(NewButton, Loc.Get("ScriptList_New"));
        ToolTipService.SetToolTip(NewButton, Loc.Get("ScriptList_New"));
        AutomationProperties.SetName(Tree, Loc.Get("ScriptList_Title"));
        NewJsItem.Text = Loc.Get("ScriptList_NewJs");
        NewCSharpItem.Text = Loc.Get("ScriptList_NewCSharp");
        OpenFolderItem.Text = Loc.Get("ScriptList_OpenFolder");
    }

    internal ScriptListViewModel Vm { get; }

    /// <summary>項目の下に出す「フォルダが見つかりません」(AUTO-19 の「エラー」)。</summary>
    public static string MissingText(bool missing) => missing ? Loc.Get("ScriptList_FolderMissing") : string.Empty;

    /// <summary>F6 で来たときは検索欄に置く (↓ で一覧に移る)。</summary>
    bool Panels.IPanelContent.FocusContent() => FilterBox.Focus(FocusState.Keyboard);

    private ScriptListItem? Selected => Tree.SelectedItem as ScriptListItem;

    private void Filter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down)
        {
            FocusTree();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (FilterBox.Text.Length > 0)
            {
                FilterBox.Text = string.Empty;
            }
            else
            {
                _host.FocusEditor();
            }

            e.Handled = true;
        }
    }

    /// <summary>一覧に移り、選択がなければ先頭の項目を選ぶ。</summary>
    private void FocusTree()
    {
        if (Tree.SelectedItem is null && Vm.Items.Count > 0)
        {
            Tree.SelectedItem = Vm.Items[0];
        }

        if (Tree.ContainerFromItem(Tree.SelectedItem) is Control container)
        {
            container.Focus(FocusState.Keyboard);
        }
        else
        {
            Tree.Focus(FocusState.Keyboard);
        }
    }

    private void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when Selected is { } item:
                Activate(item);
                e.Handled = true;
                break;
            case VirtualKey.Delete when Selected is { IsScript: true } item:
                _ = DeleteAsync(item);
                e.Handled = true;
                break;
            case VirtualKey.F2 when Selected is { IsScript: true } item:
                _ = RenameAsync(item);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                _host.FocusEditor();
                e.Handled = true;
                break;
        }
    }

    private void Tree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ScriptListItem { IsScript: true } item)
        {
            Activate(item);
        }
    }

    /// <summary>Enter・ダブルクリック: スクリプトは実行、フォルダ・見出しは開閉する (AUTO-19 の仕様 3)。</summary>
    private void Activate(ScriptListItem item)
    {
        if (item.IsScript)
        {
            Run(item);
        }
        else
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private static void Run(ScriptListItem item) => ScriptWorkspace.Run(item.Node);

    // ---- 右クリックメニュー ----

    private void ItemMenu_Opening(object sender, object e)
    {
        ItemMenu.Items.Clear();
        if (Selected is not { } item)
        {
            AddNewItems();
            return;
        }

        if (item.IsScript)
        {
            bool sample = item.Node.Root.Kind == ScriptRootKind.Samples;
            Add("ScriptList_Run", () => Run(item));
            Add(sample ? "ScriptList_OpenSample" : "ScriptList_Edit", () => Edit(item));
            if (!sample)
            {
                Add("ScriptList_Rename", () => _ = RenameAsync(item));
                Add("ScriptList_Delete", () => _ = DeleteAsync(item));
            }

            Add("ScriptList_ShowInFolder", () => _host.ShowInFolder(item.Path));
            if (item.Node.IsCommand)
            {
                ItemMenu.Items.Add(new MenuFlyoutSeparator());
                Add("ScriptList_AssignShortcut", () => _host.AssignShortcut(ScriptCommandIds.For(item.Node)));
                Add("ScriptList_HookSettings", () => _host.OpenHookSettings(item.Path));
            }

            return;
        }

        if (item.IsFolder || item.IsGroup)
        {
            if (item.Node.Root.Kind != ScriptRootKind.Samples)
            {
                AddNewItems(item.IsFolder || item.IsGroup && !item.IsMissing ? item.Path : null);
            }

            if (!item.IsMissing)
            {
                Add("ScriptList_ShowInFolder", () => _host.ShowInFolder(item.Path));
            }
        }
    }

    private void AddNewItems(string? folder = null)
    {
        Add("ScriptList_NewJs", () => NewScript(ScriptLanguage.JavaScript, folder));
        Add("ScriptList_NewCSharp", () => NewScript(ScriptLanguage.CSharp, folder));
    }

    private void Add(string key, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key) };
        AutomationProperties.SetAutomationId(item, key);
        item.Click += (_, _) => action();
        ItemMenu.Items.Add(item);
    }

    // ---- 操作 (AUTO-19 の仕様 3) ----

    /// <summary>編集: スクリプトエディタで開く。サンプルは利用者のスクリプトフォルダにコピーしてから開く (AUTO-46 の仕様 3)。</summary>
    private void Edit(ScriptListItem item)
    {
        string path = item.Path;
        if (item.Node.Root.Kind == ScriptRootKind.Samples)
        {
            try
            {
                path = ScriptLibrary.CopySample(item.Path, ScriptingService.Folders.Scripts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _host.ShowNotice(Loc.Format("ScriptList_CopyFailed", ex.Message), error: true);
                return;
            }
        }

        _host.OpenScriptEditor(path);
    }

    private void NewScript(ScriptLanguage language, string? folder)
    {
        try
        {
            string path = ScriptLibrary.CreateScript(folder ?? ScriptingService.Folders.Scripts, language, Loc.Get("ScriptList_NewName"));
            _host.OpenScriptEditor(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotice(Loc.Format("ScriptList_CreateFailed", ex.Message), error: true);
        }
    }

    private void NewJs_Click(object sender, RoutedEventArgs e) => NewScript(ScriptLanguage.JavaScript, null);

    private void NewCSharp_Click(object sender, RoutedEventArgs e) => NewScript(ScriptLanguage.CSharp, null);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(ScriptingService.Folders.Scripts);
        _host.ShowInFolder(ScriptingService.Folders.Scripts);
    }

    private async Task RenameAsync(ScriptListItem item)
    {
        string current = System.IO.Path.GetFileNameWithoutExtension(item.Path);
        if (await _host.PromptNameAsync(Loc.Get("ScriptList_Rename"), Loc.Get("ScriptList_RenameLabel"), current) is not { Length: > 0 } name || name == current)
        {
            return;
        }

        try
        {
            ScriptLibrary.Rename(item.Path, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotice(Loc.Format("ScriptList_RenameFailed", ex.Message), error: true);
        }
    }

    private async Task DeleteAsync(ScriptListItem item)
    {
        if (item.Node.Root.Kind == ScriptRootKind.Samples)
        {
            return;
        }

        try
        {
            ScriptWorkspace.DeleteToRecycleBin(item.Path);
            _host.ShowStatus(Loc.Format("ScriptList_Deleted", item.DisplayName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotice(Loc.Format("ScriptList_DeleteFailed", ex.Message), error: true);
        }

        await Task.CompletedTask;
    }
}
