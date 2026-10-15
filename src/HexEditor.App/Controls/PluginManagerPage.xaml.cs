using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace HexEditor.App.Controls;

/// <summary>プラグインマネージャ (08 の AUTO-32) の画面。状態と操作は <see cref="PluginManagerViewModel"/>。ここではドロップと文字列だけを扱う。</summary>
public sealed partial class PluginManagerPage : UserControl
{
    public PluginManagerPage(PluginManagerViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        TitleText.Text = Loc.Get("PluginManager_Title");
        IntroText.Text = Loc.Get("PluginManager_Intro");
        SafeModeBar.Message = Loc.Get("PluginManager_SafeMode");
        AddButton.Content = Loc.Get("PluginManager_Add");
        RestartButton.Content = Loc.Get("PluginManager_Restart");
        OpenFolderButton.Content = Loc.Get("PluginManager_OpenFolder");
        RemoveButton.Content = Loc.Get("PluginManager_Remove");
        SettingsButton.Content = Loc.Get("PluginManager_Settings");
        EnabledSwitch.Header = Loc.Get("PluginManager_Enabled");
        EnabledSwitch.OnContent = Loc.Get("PluginManager_EnabledOn");
        EnabledSwitch.OffContent = Loc.Get("PluginManager_EnabledOff");
        EmptyText.Text = Loc.Get("PluginManager_Empty");
        DevHeading.Text = Loc.Get("PluginManager_DevFolders");
        DevIntro.Text = Loc.Get("PluginManager_DevFoldersIntro");
        AddDevFolderButton.Content = Loc.Get("PluginManager_AddDevFolder");
        PublisherHeading.Text = Loc.Get("PluginManager_Publishers");
        NoPublishersText.Text = Loc.Get("PluginManager_PublishersIntro");
        foreach ((FrameworkElement element, string key) in new (FrameworkElement, string)[]
        {
            (AddButton, "PluginManager_Add"), (RestartButton, "PluginManager_Restart"), (OpenFolderButton, "PluginManager_OpenFolder"),
            (RemoveButton, "PluginManager_Remove"), (SettingsButton, "PluginManager_Settings"), (EnabledSwitch, "PluginManager_Enabled"),
            (PluginList, "PluginManager_ListName"), (AddDevFolderButton, "PluginManager_AddDevFolder"),
        })
        {
            AutomationProperties.SetName(element, Loc.Get(key));
        }

        ToolTipService.SetToolTip(PluginList, Loc.Get("PluginManager_DropHint"));
    }

    public PluginManagerViewModel Vm { get; }

    /// <summary>メッセージの重要度 (x:Bind の関数)。</summary>
    public InfoBarSeverity MessageSeverity(bool error) => error ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    /// <summary>一覧にフォーカスを移す (コマンド「プラグイン: 管理」で開いたとき)。</summary>
    public void FocusList()
    {
        if (Vm.Items.Count > 0)
        {
            PluginList.Focus(FocusState.Keyboard);
        }
        else
        {
            AddButton.Focus(FocusState.Keyboard);
        }
    }

    private void PluginList_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get("PluginManager_DropCaption");
        }
    }

    private async void PluginList_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            deferral.Complete();
            foreach (IStorageItem item in items.Where(i => i is StorageFile))
            {
                await Vm.InstallAsync(item.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warning($"Plugin drop failed: {ex.GetType().Name}");
        }
    }

    private void RemoveDevFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folder })
        {
            Vm.RemoveDevFolderCommand.Execute(folder);
        }
    }

    private void RevokePublisher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TrustedPublisherItem item })
        {
            Vm.RevokePublisherCommand.Execute(item);
        }
    }
}
