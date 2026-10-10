using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Devices;
using HexEditor.Core.Processes;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>ディスク・プロセスを開くダイアログと表示の補助 (ENG-29、ENG-31、ENG-32)。</summary>
public sealed partial class MainWindow
{
    /// <summary>「ディスクを開く」「プロセスを開く」の一覧の 1 行。</summary>
    public sealed class DeviceListItem(string text, string detail, bool needsAdmin, bool locked, Func<DeviceOpenInfo> createInfo, bool isRemovableUsb)
    {
        public string Text { get; } = text;

        public string Detail { get; } = detail;

        public bool NeedsAdmin { get; } = needsAdmin;

        public bool Locked { get; } = locked;

        public bool IsRemovableUsb { get; } = isRemovableUsb;

        public ProcessEntry? Process { get; init; }

        public DeviceOpenInfo CreateInfo() => createInfo();

        /// <summary>一覧に出す文字列 (盾・錠のアイコンを色だけに頼らず文字でも示す。ENG-29 の仕様 3)。</summary>
        public string Label =>
            (Locked ? LockGlyph + " " : NeedsAdmin ? ShieldGlyph + " " : string.Empty) + Text + "    " + Detail;

        public override string ToString() => Label;
    }

    private async Task<(bool Ok, DeviceListItem? Chosen, bool ReadOnly)> ShowDeviceDialogAsync(string title, IReadOnlyList<DeviceListItem> items, string automationId)
    {
        var list = new ListView
        {
            ItemsSource = items,
            SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(DeviceListItem.Label),
            MaxHeight = 360,
            MinWidth = 420,
        };
        AutomationProperties.SetName(list, title);
        var readOnly = new CheckBox { Content = Loc.Get("OpenDevice_ReadOnly"), IsChecked = true };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(list);
        panel.Children.Add(readOnly);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = title,
            Content = panel,
            PrimaryButtonText = Loc.Get("Common_Open"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is not null)
            {
                dialog.Hide();
            }
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        ContentDialogResult result = await dialog.ShowQueuedAsync();
        bool ok = result == ContentDialogResult.Primary || (list.SelectedItem is not null && result == ContentDialogResult.None);
        return (ok && list.SelectedItem is DeviceListItem, list.SelectedItem as DeviceListItem, readOnly.IsChecked == true);
    }

    private async Task<(bool Ok, int SectorSize, bool AllowResize)> ShowDiskImageDialogAsync(string path, int defaultSize)
    {
        var combo = new ComboBox { Header = Loc.Get("DiskImage_SectorSize") };
        foreach (int size in DiskImage.CommonSectorSizes)
        {
            combo.Items.Add(size);
        }

        combo.Items.Add(Loc.Get("DiskImage_Custom"));
        combo.SelectedItem = DiskImage.CommonSectorSizes.Contains(defaultSize) ? defaultSize : DiskImage.CommonSectorSizes[0];

        var custom = new NumberBox
        {
            Header = Loc.Get("DiskImage_CustomSize"),
            Minimum = DiskImage.MinSectorSize,
            Maximum = DiskImage.MaxSectorSize,
            Value = defaultSize,
            Visibility = Visibility.Collapsed,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
        };
        var allowResize = new CheckBox { Content = Loc.Get("DiskImage_AllowResize") };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(combo);
        panel.Children.Add(custom);
        panel.Children.Add(allowResize);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("DiskImage_Title"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Common_Open"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "OpenDiskImageDialog");

        int Chosen() => combo.SelectedItem is int s ? s : (int)custom.Value;
        void Validate()
        {
            bool isCustom = combo.SelectedItem is not int;
            custom.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = DiskImage.IsValidSectorSize(Chosen());
        }

        combo.SelectionChanged += (_, _) => Validate();
        custom.ValueChanged += (_, _) => Validate();
        Validate();

        ContentDialogResult result = await dialog.ShowQueuedAsync();
        return (result == ContentDialogResult.Primary, Chosen(), allowResize.IsChecked == true);
    }

    private async Task ShowMessageAsync(string title, string body, string automationId)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, automationId);
        await dialog.ShowQueuedAsync();
    }

    private static string DiskDisplayName(DiskInfo disk)
    {
        string size = disk.Size is { } bytes ? StatusFormat.ShortSize(bytes, CultureInfo.CurrentCulture) ?? bytes.ToString("N0", CultureInfo.CurrentCulture) : Loc.Get("Common_Unknown");
        string model = disk.Model ?? Loc.Get("Common_Unknown");
        return Loc.Format("OpenDisk_DiskName", disk.Number, model, size);
    }

    private static string VolumeDisplayName(VolumeDeviceInfo volume)
    {
        string label = string.IsNullOrEmpty(volume.Label) ? Loc.Get("OpenDisk_NoLabel") : volume.Label;
        return Loc.Format("OpenDisk_VolumeName", volume.Name, label);
    }

    private static string ProcessDisplayName(ProcessEntry entry) => Loc.Format("OpenProcess_Name", entry.Name, entry.Pid);

    private static string DeviceErrorMessage(DeviceException ex) => ex.ErrorCode switch
    {
        Win32Errors.AccessDenied => Loc.Get("OpenDisk_AccessDenied"),
        Win32Errors.NotReady => Loc.Get("OpenDisk_NoMedia"),
        var fve when fve == Win32Errors.FveLocked => Loc.Get("OpenDisk_BitLocker"),
        _ => Loc.Format("OpenDisk_Error", ex.Message),
    };

    private void UpdateHelperShield()
    {
        if (StatusHelperShield is null)
        {
            return;
        }

        // 表示・非表示はステータスバーの並べ方 (UpdateStatusBarLayout) が決める。
        bool running = App.Devices.IsHelperRunning;
        ToolTipService.SetToolTip(StatusHelperShield, running ? Loc.Get("Status_HelperRunning") : null);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusHelperShield, running ? Loc.Get("Status_HelperRunning") : string.Empty);
        UpdateStatusBarLayout();
    }
}
