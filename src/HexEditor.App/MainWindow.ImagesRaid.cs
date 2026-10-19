using System.Collections.ObjectModel;
using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Raid;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>RAID の再構成 (12 の FOR-26): メンバーの一覧 (並べ替え可)、種類、パラメーター、先頭 1 MiB の判定結果、構成の保存と読み込み。</summary>
public sealed partial class MainWindow
{
    /// <summary>RAID のダイアログのメンバー 1 つ。<see cref="Document"/> は開いているドキュメント (null ならファイル)。</summary>
    internal sealed class ImgRaidMemberRow(string source, string label, DocumentViewModel? document)
    {
        public string Source { get; } = source;

        public string Label { get; } = label;

        public DocumentViewModel? Document { get; } = document;

        public long DataOffset { get; set; }

        public bool Missing { get; set; }

        public override string ToString() => (Missing ? Loc.Get("ImgRaid_MissingMark") + " " : string.Empty) + Label
            + (DataOffset != 0 ? " +" + DataOffset.ToString("N0", CultureInfo.CurrentCulture) : string.Empty);
    }

    /// <summary>最後の判定結果の表示 (テスト用)。</summary>
    internal string? LastRaidDetection { get; private set; }

    /// <summary>テスト用: RAID のダイアログを開いた状態で操作する (並べ替え・種類の変更・判定の確認)。</summary>
    internal Func<ImgRaidDialogControls, Task>? ImgRaidInteraction { get; set; }

    /// <summary>表示中の RAID のダイアログの部品 (テスト用)。</summary>
    internal ImgRaidDialogControls? ImgRaidControls { get; private set; }

    /// <summary>テスト用に公開する RAID のダイアログの部品。</summary>
    internal sealed record ImgRaidDialogControls(ContentDialog Dialog, ObservableCollection<ImgRaidMemberRow> Members, ComboBox Level, ComboBox Stripe, ComboBox Layout,
        ComboBox UseMember, TextBlock Detection, Action Refresh, Action<int, int> Move);

    private static readonly long[] ImgStripeSizes = [.. Enumerable.Range(9, 15).Select(i => 1L << i)]; // 512 B〜16 MiB

    private async Task ReconstructRaidAsync()
    {
        if (await ShowRaidDialogAsync() is { } built)
        {
            DocumentViewModel vm = Vm.OpenRaidSource(built);
            if (built.SizesDiffer)
            {
                ShowNotice(Loc.Get("ImgRaid_SizesDiffer"), InfoBarSeverity.Warning, vm);
            }

            UpdateTitle();
        }
    }

    /// <summary>RAID 再構成のダイアログ (FOR-26 の「画面」)。開くを押したら、構成から論理ディスクを作って返す (データは走査しない)。</summary>
    internal async Task<RaidByteSource?> ShowRaidDialogAsync(RaidConfig? initial = null)
    {
        // 開いているドキュメントをメンバーの候補として並べる (使わないものは「外す」)。
        var members = new ObservableCollection<ImgRaidMemberRow>(Vm.Documents
            .Select(d => new ImgRaidMemberRow(d.FilePath ?? (d.Document.Source is ImageSourceBase img ? img.PrimaryPath : d.Device?.Path ?? d.DisplayName), d.DisplayName, d)));
        if (initial is not null)
        {
            members.Clear();
            foreach (RaidMember m in initial.Members)
            {
                members.Add(new ImgRaidMemberRow(m.Source, Path.GetFileName(m.Source), Vm.Documents.FirstOrDefault(d => ImgSameSource(d, m.Source)))
                {
                    DataOffset = m.DataOffset,
                    Missing = m.Missing,
                });
            }
        }

        var list = new ListView { ItemsSource = members, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 200, MinWidth = 440 };
        AutomationProperties.SetAutomationId(list, "ImgRaid_Members");
        AutomationProperties.SetName(list, Loc.Get("ImgRaid_Members"));
        Button Btn(string id, string key)
        {
            var b = new Button { Content = Loc.Get(key) };
            AutomationProperties.SetAutomationId(b, id);
            return b;
        }

        Button up = Btn("ImgRaid_Up", "ImgRaid_MoveUp");
        Button down = Btn("ImgRaid_Down", "ImgRaid_MoveDown");
        Button add = Btn("ImgRaid_Add", "ImgRaid_AddFile");
        Button remove = Btn("ImgRaid_Remove", "ImgRaid_Remove");
        CheckBox missing = DialogParts.Check("ImgRaid_Missing", Loc.Get("ImgRaid_MemberMissing"), false);
        TextBox offset = DialogParts.Field("ImgRaid_Offset", Loc.Get("ImgRaid_DataOffset"), "0");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (Button b in new[] { up, down, add, remove })
        {
            buttons.Children.Add(b);
        }

        ComboBox level = DialogParts.Combo("ImgRaid_Level", Loc.Get("ImgRaid_Level"), ["JBOD", "RAID 0", "RAID 1", "RAID 5"], 3);
        ComboBox stripe = DialogParts.Combo("ImgRaid_Stripe", Loc.Get("ImgRaid_StripeSize"), ImgStripeSizes.Select(ImgStripeText), Array.IndexOf(ImgStripeSizes, 64 * 1024L));
        ComboBox layout = DialogParts.Combo("ImgRaid_Layout", Loc.Get("ImgRaid_Layout"),
            [Loc.Get("ImgRaid_Layout_LeftSymmetric"), Loc.Get("ImgRaid_Layout_LeftAsymmetric"), Loc.Get("ImgRaid_Layout_RightSymmetric"), Loc.Get("ImgRaid_Layout_RightAsymmetric")], 0);
        ComboBox useMember = DialogParts.Combo("ImgRaid_UseMember", Loc.Get("ImgRaid_UseMember"), [], -1);
        TextBlock detection = ImgNote("ImgRaid_Detection", string.Empty);
        TextBlock problem = ImgNote("ImgRaid_Problem", string.Empty, warning: true);
        Button save = Btn("ImgRaid_Save", "ImgRaid_SaveConfig");
        Button load = Btn("ImgRaid_Load", "ImgRaid_LoadConfig");
        var config = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        config.Children.Add(save);
        config.Children.Add(load);
        if (initial is not null)
        {
            level.SelectedIndex = (int)initial.Level;
            stripe.SelectedIndex = Math.Max(0, Array.IndexOf(ImgStripeSizes, initial.StripeSize));
            layout.SelectedIndex = (int)initial.Layout;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        foreach (FrameworkElement e in new FrameworkElement[] { list, buttons, missing, offset, level, stripe, layout, useMember, ImgSection(Loc.Get("ImgRaid_DetectionHeader"), detection), problem, config })
        {
            panel.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ImgRaidDialog", Loc.Get("ImgRaid_Title"), new ScrollViewer { Content = panel, MaxHeight = 560 }, Loc.Get("Common_Open"));
        RaidByteSource? preview = null;

        RaidConfig Config() => new()
        {
            Level = (RaidLevel)Math.Max(0, level.SelectedIndex),
            StripeSize = ImgStripeSizes[Math.Max(0, stripe.SelectedIndex)],
            Layout = (Raid5Layout)Math.Max(0, layout.SelectedIndex),
            UseMember = Math.Max(0, useMember.SelectedIndex),
            SectorSize = members.Select(m => m.Document?.Document.Source.LogicalSectorSize ?? 512).FirstOrDefault(s => s > 1, 512),
            Members = [.. members.Select(m => new RaidMember { Source = m.Source, DataOffset = m.DataOffset, Missing = m.Missing })],
        };

        bool refreshing = false;
        void Refresh()
        {
            if (refreshing)
            {
                return;
            }

            refreshing = true;
            try
            {
                RaidLevel l = (RaidLevel)Math.Max(0, level.SelectedIndex);
                stripe.IsEnabled = l is RaidLevel.Raid0 or RaidLevel.Raid5;
                layout.IsEnabled = l == RaidLevel.Raid5;
                useMember.IsEnabled = l == RaidLevel.Raid1;
                int keep = useMember.SelectedIndex;
                useMember.ItemsSource = members.Select(m => m.Label).ToList();
                useMember.SelectedIndex = members.Count == 0 ? -1 : Math.Clamp(keep, 0, members.Count - 1);
                (preview, string text, string? error) = BuildRaidPreview(Config(), members);
                detection.Text = text;
                LastRaidDetection = text;
                problem.Text = error ?? string.Empty;
                dialog.IsPrimaryButtonEnabled = preview is not null;
            }
            finally
            {
                refreshing = false;
            }
        }

        void Move(int from, int to)
        {
            if (from >= 0 && from < members.Count && to >= 0 && to < members.Count)
            {
                members.Move(from, to);
                list.ItemsSource = null;
                list.ItemsSource = members;
                list.SelectedIndex = to;
                Refresh();
            }
        }

        up.Click += (_, _) => Move(list.SelectedIndex, list.SelectedIndex - 1);
        down.Click += (_, _) => Move(list.SelectedIndex, list.SelectedIndex + 1);
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                members.RemoveAt(list.SelectedIndex);
                Refresh();
            }
        };
        add.Click += async (_, _) =>
        {
            IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.RaidMember");
            if (paths is null)
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.RaidMember" };
                picker.FileTypeFilter.Add("*");
                paths = [.. (await picker.PickMultipleFilesAsync()).Select(f => f.Path)];
            }

            foreach (string p in paths)
            {
                members.Add(new ImgRaidMemberRow(p, Path.GetFileName(p), null));
            }

            Refresh();
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ImgRaidMemberRow row)
            {
                missing.IsChecked = row.Missing;
                offset.Text = row.DataOffset.ToString(CultureInfo.InvariantCulture);
            }
        };
        missing.Click += (_, _) =>
        {
            if (list.SelectedItem is ImgRaidMemberRow row)
            {
                row.Missing = missing.IsChecked == true;
                Move(list.SelectedIndex, list.SelectedIndex);
            }
        };
        offset.TextChanged += (_, _) =>
        {
            bool ok = ImgParseSize(offset.Text) is long v && v % 512 == 0 || offset.Text.Trim() == "0";
            DialogParts.MarkInvalid(offset, !ok);
            if (ok && list.SelectedItem is ImgRaidMemberRow row)
            {
                row.DataOffset = offset.Text.Trim() == "0" ? 0 : ImgParseSize(offset.Text)!.Value;
                Refresh();
            }
        };
        foreach (ComboBox box in new[] { level, stripe, layout, useMember })
        {
            box.SelectionChanged += (_, _) => Refresh();
        }

        save.Click += async (_, _) => await SaveRaidConfigAsync(Config());
        load.Click += async (_, _) =>
        {
            if (await LoadRaidConfigAsync() is { } loaded)
            {
                members.Clear();
                foreach (RaidMember m in loaded.Members)
                {
                    members.Add(new ImgRaidMemberRow(m.Source, Path.GetFileName(m.Source), Vm.Documents.FirstOrDefault(d => ImgSameSource(d, m.Source)))
                    {
                        DataOffset = m.DataOffset,
                        Missing = m.Missing,
                    });
                }

                level.SelectedIndex = (int)loaded.Level;
                stripe.SelectedIndex = Math.Max(0, Array.IndexOf(ImgStripeSizes, loaded.StripeSize));
                layout.SelectedIndex = (int)loaded.Layout;
                Refresh();
            }
        };

        Refresh();
        Task<ContentDialogResult> shown = dialog.ShowQueuedAsync();
        ImgRaidControls = new ImgRaidDialogControls(dialog, members, level, stripe, layout, useMember, detection, Refresh, Move);
        if (ImgRaidInteraction is { } interact)
        {
            await interact(ImgRaidControls);
        }

        ContentDialogResult result;
        try
        {
            result = await shown;
        }
        finally
        {
            ImgRaidControls = null;
        }

        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        // 開く: メンバーのデータソースを、ドキュメントとは別に読み取り専用で開く (ドキュメントを閉じても論理ディスクは読める)。
        (RaidByteSource? built, _, string? buildError) = BuildRaidSource(Config(), members, own: true);
        if (built is null)
        {
            ShowNotice(buildError ?? Loc.Get("ImgRaid_CannotOpen"), InfoBarSeverity.Error);
        }

        return built;
    }

    private static string ImgStripeText(long size) => size >= 1024 * 1024 ? $"{size >> 20} MiB" : size >= 1024 ? $"{size >> 10} KiB" : $"{size} B";

    private static bool ImgSameSource(DocumentViewModel d, string source) =>
        string.Equals(d.FilePath, source, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Device?.Path, source, StringComparison.OrdinalIgnoreCase)
        || (d.Document.Source is ImageSourceBase img && string.Equals(img.PrimaryPath, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>判定結果の表示用の論理ディスク (メンバーはドキュメントのデータソースを共有する) と、判定の文 (FOR-26 の仕様 5)。</summary>
    private (RaidByteSource? Source, string Text, string? Error) BuildRaidPreview(RaidConfig config, IReadOnlyList<ImgRaidMemberRow> members)
    {
        (RaidByteSource? raid, string? _, string? error) = BuildRaidSource(config, members, own: false);
        if (raid is null)
        {
            return (null, Loc.Get("ImgRaid_NoDetection"), error);
        }

        SniffResult sniff = VolumeSniffer.Sniff(raid, config.SectorSize);
        string table = sniff.PartitionTable is { } t
            ? Loc.Format("ImgRaid_TableDetected", t, sniff.PartitionCount.ToString(CultureInfo.InvariantCulture))
            : Loc.Get("ImgRaid_NoTable");
        string fs = sniff.FileSystem is { } f ? Loc.Format("ImgRaid_FsDetected", f) : Loc.Get("ImgRaid_NoFs");
        string size = Loc.Format("ImgRaid_Size", raid.Length.ToString("N0", CultureInfo.CurrentCulture));
        string? warning = raid.SizesDiffer ? Loc.Get("ImgRaid_SizesDiffer") : null;
        return (raid, table + Environment.NewLine + fs + Environment.NewLine + size, warning);
    }

    /// <summary>構成から論理ディスクを作る。<paramref name="own"/> なら、メンバーをドキュメントとは別に開く (開いた論理ディスクが持つ)。</summary>
    private (RaidByteSource? Source, string? Unused, string? Error) BuildRaidSource(RaidConfig config, IReadOnlyList<ImgRaidMemberRow> members, bool own)
    {
        if (config.Validate() is { } problem)
        {
            return (null, null, Loc.Get("ImgRaid_Problem_" + problem));
        }

        var sources = new List<IByteSource?>();
        try
        {
            foreach (ImgRaidMemberRow m in members)
            {
                if (m.Missing)
                {
                    sources.Add(null);
                }
                else if (!own && m.Document is { } d)
                {
                    sources.Add(d.Document.Source);
                }
                else if (m.Document?.Device is { } device)
                {
                    // ディスクのメンバー: 同じアクセスで読み取り専用のハンドルを別に開く。
                    IDeviceHandle handle = device.Access.Open(device.Path, writable: false);
                    sources.Add(new DeviceByteSource(handle, device.Access, device.Info));
                }
                else
                {
                    sources.Add(ImageOpener.Open(m.Source, new ImageOpenOptions { SectorSize = config.SectorSize }));
                }
            }

            string name = Loc.Format("ImgRaid_DocumentName", config.Level switch { RaidLevel.Jbod => "JBOD", RaidLevel.Raid0 => "RAID 0", RaidLevel.Raid1 => "RAID 1", _ => "RAID 5" },
                members.Count.ToString(CultureInfo.InvariantCulture));
            return (new RaidByteSource(config, sources, name, ownsMembers: own), null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RaidConfigException or ArgumentException or DeviceException)
        {
            if (own)
            {
                foreach (IByteSource? s in sources)
                {
                    s?.Dispose();
                }
            }

            return (null, null, ex is RaidConfigException rc ? Loc.Get("ImgRaid_Problem_" + rc.Problem) : ex.Message);
        }
    }

    /// <summary>構成の保存 (<c>.raid.json</c>。ケースがあればケースフォルダを既定にする。FOR-26 の仕様 3)。</summary>
    private async Task SaveRaidConfigAsync(RaidConfig config)
    {
        string suggested = "raid.raid.json";
        if (!TestHooks.TrySavePicker(suggested, out string? path))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = "raid", SettingsIdentifier = "HexEditor.RaidConfig" };
            picker.FileTypeChoices.Add(Loc.Get("ImgRaid_FileType"), [".raid.json"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null && ImageForensics.Hooks.Case?.Folder is { } folder)
        {
            path = Path.Combine(folder, suggested);
        }

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, config.ToJson());
            ShowStatusMessage(Loc.Format("ImgRaid_Saved", Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("ImgRaid_SaveFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task<RaidConfig?> LoadRaidConfigAsync()
    {
        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.RaidConfig");
        string? path = paths?.FirstOrDefault();
        if (path is null)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.RaidConfig" };
            picker.FileTypeFilter.Add(".json");
            path = (await picker.PickSingleFileAsync())?.Path;
        }

        if (path is null)
        {
            return null;
        }

        try
        {
            return RaidConfig.Parse(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("ImgRaid_LoadFailed", ex.Message), InfoBarSeverity.Error);
            return null;
        }
    }
}
