using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.Devices;
using HexEditor.Core.Expressions;
using HexEditor.Core.Processes;
using HexEditor.Core.Settings;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>プロセスを開く範囲 (ENG-32 の仕様 2)。</summary>
public enum ProcessOpenScope
{
    /// <summary>アドレス空間全体 (既定)。</summary>
    Whole,

    /// <summary>モジュールを選ぶ (開いた後にモジュールの一覧から選ぶ)。</summary>
    Module,

    /// <summary>開始アドレスと長さを指定する。</summary>
    Range,
}

/// <summary>「範囲を指定して開く」の入力 (開始・長さの入力式。長さが空なら末尾まで。ENG-13 と同じ入力)。</summary>
public sealed record DeviceRangeInput(string Start, string Length, bool InSectors);

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

        /// <summary>大きさ (並べ替え用。不明なら null)。</summary>
        public long? Size { get; init; }

        /// <summary>論理セクタサイズ (範囲の入力の確かめ。不明なら null)。</summary>
        public int? SectorSize { get; init; }

        public DeviceOpenInfo CreateInfo() => createInfo();

        /// <summary>盾・錠のアイコンの説明 (ツールチップ。ENG-29 の仕様 3 の 3、ENG-32 の仕様 1)。</summary>
        public string? Tooltip => Locked ? Loc.Get("OpenProcess_LockedTip") : NeedsAdmin ? Loc.Get("OpenDevice_ShieldTip") : null;

        /// <summary>一覧に出す文字列 (盾・錠のアイコンを色だけに頼らず文字でも示す。ENG-29 の仕様 3)。2 行目に詳しい情報。</summary>
        public string Label =>
            (Locked ? LockGlyph + " " : NeedsAdmin ? ShieldGlyph + " " : string.Empty) + Text + (Detail.Length > 0 ? Environment.NewLine + "    " + Detail : string.Empty);

        public override string ToString() => Label;
    }

    /// <summary>「ディスクを開く」の結果。</summary>
    internal sealed record DiskDialogResult(DeviceListItem Item, bool ReadOnly, DeviceRangeInput? Range);

    /// <summary>「プロセスを開く」の結果。</summary>
    internal sealed record ProcessDialogResult(ProcessEntry Entry, bool ReadOnly, ProcessOpenScope Scope, DeviceRangeInput? Range);

    private static ListView DeviceList(string automationId, string name)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(DeviceListItem.Label),
            MaxHeight = 320,
            MinWidth = 460,
        };
        AutomationProperties.SetName(list, name);
        AutomationProperties.SetAutomationId(list, automationId);

        // 盾・錠のアイコンのツールチップ (行ごと)。
        list.ContainerContentChanging += (_, e) =>
        {
            if (e.Item is DeviceListItem item && e.ItemContainer is { } container)
            {
                ToolTipService.SetToolTip(container, item.Tooltip);
                AutomationProperties.SetHelpText(container, item.Tooltip ?? string.Empty);
            }
        };
        return list;
    }

    /// <summary>
    /// 「ディスクを開く」のダイアログ (ENG-29 の仕様 1・2): 物理ディスク・ボリューム・光学ドライブの一覧 (並べ替えあり)、「読み取り専用で開く」(既定オン)、
    /// 「範囲を指定して開く」(開始・長さをセクタ数またはバイトで指定)。
    /// </summary>
    internal async Task<DiskDialogResult?> ShowOpenDiskDialogAsync(IReadOnlyList<DeviceListItem> items)
    {
        string title = Loc.Get("OpenDisk_Title");
        ListView list = DeviceList("OpenDisk_List", title);
        ComboBox sort = DialogParts.Combo("OpenDisk_Sort", Loc.Get("OpenDevice_SortBy"),
            [Loc.Get("OpenDevice_Sort_Kind"), Loc.Get("OpenDevice_Sort_Name"), Loc.Get("OpenDevice_Sort_Size")], 0);
        void Fill()
        {
            object? selected = list.SelectedItem;
            IEnumerable<DeviceListItem> ordered = sort.SelectedIndex switch
            {
                1 => items.OrderBy(i => i.Text, StringComparer.CurrentCultureIgnoreCase),
                2 => items.OrderByDescending(i => i.Size ?? -1),
                _ => items,
            };
            list.ItemsSource = ordered.ToList();
            list.SelectedItem = selected;
        }

        sort.SelectionChanged += (_, _) => Fill();
        Fill();

        CheckBox readOnly = DialogParts.Check("OpenDevice_ReadOnly", Loc.Get("OpenDevice_ReadOnly"), true);
        (CheckBox useRange, StackPanel rangePanel, Func<DeviceRangeInput> rangeInput, Func<long?, int?, string?> validate) =
            RangeInputs("OpenDisk", allowSectors: true);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(sort);
        panel.Children.Add(list);
        panel.Children.Add(readOnly);
        panel.Children.Add(useRange);
        panel.Children.Add(rangePanel);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 };

        ContentDialog dialog = DialogParts.Dialog(Root, "OpenDiskDialog", title, scroll, Loc.Get("Common_Open"));
        TextBlock error = DialogParts.Caption("OpenDisk_RangeError");
        rangePanel.Children.Add(error);
        void Validate()
        {
            string? problem = null;
            if (useRange.IsChecked == true && list.SelectedItem is DeviceListItem item)
            {
                problem = validate(item.Size, item.SectorSize);
            }

            error.Text = problem ?? string.Empty;
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is DeviceListItem && problem is null;
        }

        list.SelectionChanged += (_, _) => Validate();
        useRange.Click += (_, _) => Validate();
        foreach (TextBox box in rangePanel.Children.OfType<TextBox>())
        {
            box.TextChanged += (_, _) => Validate();
        }

        foreach (ComboBox box in rangePanel.Children.OfType<ComboBox>())
        {
            box.SelectionChanged += (_, _) => Validate();
        }

        list.DoubleTapped += (_, _) =>
        {
            if (dialog.IsPrimaryButtonEnabled)
            {
                dialog.Hide();
                _diskDialogAccepted = true;
            }
        };
        Validate();
        _diskDialogAccepted = false;
        ContentDialogResult result = await dialog.ShowQueuedAsync();
        if ((result != ContentDialogResult.Primary && !_diskDialogAccepted) || list.SelectedItem is not DeviceListItem chosen)
        {
            return null;
        }

        return new DiskDialogResult(chosen, readOnly.IsChecked == true, useRange.IsChecked == true ? rangeInput() : null);
    }

    private bool _diskDialogAccepted;

    /// <summary>
    /// 「範囲を指定して開く」の入力欄 (開始・長さ・単位)。<paramref name="allowSectors"/> ならセクタ数とバイトを選べる。
    /// 戻り値の validate は (全体の長さ, セクタサイズ) から問題の説明 (なければ null) を返す。
    /// </summary>
    private static (CheckBox Use, StackPanel Panel, Func<DeviceRangeInput> Input, Func<long?, int?, string?> Validate) RangeInputs(string prefix,
        bool allowSectors)
    {
        CheckBox use = DialogParts.Check(prefix + "_Range", Loc.Get("OpenDevice_Range"), false);
        TextBox start = DialogParts.Field(prefix + "_RangeStart", Loc.Get("OpenDevice_RangeStart"), "0");
        TextBox length = DialogParts.Field(prefix + "_RangeLength", Loc.Get("OpenDevice_RangeLength"));
        length.PlaceholderText = Loc.Get("OpenDevice_RangeToEnd");
        ComboBox unit = DialogParts.Combo(prefix + "_RangeUnit", Loc.Get("OpenDevice_RangeUnit"),
            [Loc.Get("OpenDevice_Unit_Sectors"), Loc.Get("OpenDevice_Unit_Bytes")], allowSectors ? 0 : 1);
        unit.IsEnabled = allowSectors;
        var panel = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        panel.Children.Add(start);
        panel.Children.Add(length);
        if (allowSectors)
        {
            panel.Children.Add(unit);
        }

        use.Click += (_, _) => panel.Visibility = use.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DeviceRangeInput Input() => new(start.Text, length.Text, allowSectors && unit.SelectedIndex == 0);
        string? Validate(long? total, int? sectorSize)
        {
            DeviceRangeInput input = Input();
            int sector = sectorSize ?? 512;
            var context = new DeviceRangeContext(total ?? long.MaxValue, sector);
            bool startOk = DialogParts.TryEvaluate(input.Start, context, out long s, out ExpressionException? startError);
            DialogParts.MarkInvalid(start, !startOk);
            if (!startOk)
            {
                return startError is null ? Loc.Get("OpenDevice_RangeStartOutside") : DialogParts.ExpressionError(startError);
            }

            long? l = null;
            if (input.Length.Trim().Length > 0)
            {
                bool lengthOk = DialogParts.TryEvaluate(input.Length, context, out long lv, out ExpressionException? lengthError);
                DialogParts.MarkInvalid(length, !lengthOk);
                if (!lengthOk)
                {
                    return lengthError is null ? Loc.Get("Range_LengthInvalid") : DialogParts.ExpressionError(lengthError);
                }

                l = lv;
            }
            else
            {
                DialogParts.MarkInvalid(length, false);
            }

            (_, _, DeviceRangeError problem, _) = DeviceRange.Resolve(s, l, input.InSectors, allowSectors ? sector : 1, total ?? long.MaxValue);
            DialogParts.MarkInvalid(start, problem is DeviceRangeError.StartOutside or DeviceRangeError.StartNotAligned);
            DialogParts.MarkInvalid(length, problem == DeviceRangeError.LengthInvalid);
            return problem switch
            {
                DeviceRangeError.StartOutside => Loc.Get("OpenDevice_RangeStartOutside"),
                DeviceRangeError.StartNotAligned => Loc.Format("OpenDevice_RangeNotAligned", sector),
                DeviceRangeError.LengthInvalid => Loc.Get("Range_LengthInvalid"),
                _ => null,
            };
        }

        return (use, panel, Input, Validate);
    }

    /// <summary>範囲の入力式の評価の文脈 (全体の長さと <c>sector</c>)。</summary>
    private sealed class DeviceRangeContext(long length, int sectorSize) : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => length;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => sectorSize;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }

    /// <summary>範囲の入力を、開いたデバイス・アドレス空間の大きさに当てはめる。問題があれば例外 (ArgumentException、説明は利用者向け)。</summary>
    internal static (long Start, long Length) ResolveRange(DeviceRangeInput input, long total, int sectorSize)
    {
        var context = new DeviceRangeContext(total, sectorSize);
        if (!DialogParts.TryEvaluate(input.Start, context, out long start, out _))
        {
            throw new ArgumentException(Loc.Get("OpenDevice_RangeStartOutside"));
        }

        long? length = null;
        if (input.Length.Trim().Length > 0)
        {
            if (!DialogParts.TryEvaluate(input.Length, context, out long l, out _))
            {
                throw new ArgumentException(Loc.Get("Range_LengthInvalid"));
            }

            length = l;
        }

        (long s, long len, DeviceRangeError error, _) = DeviceRange.Resolve(start, length, input.InSectors, sectorSize, total);
        return error switch
        {
            DeviceRangeError.None => (s, len),
            DeviceRangeError.StartNotAligned => throw new ArgumentException(Loc.Format("OpenDevice_RangeNotAligned", sectorSize)),
            DeviceRangeError.LengthInvalid => throw new ArgumentException(Loc.Get("Range_LengthInvalid")),
            _ => throw new ArgumentException(Loc.Get("OpenDevice_RangeStartOutside")),
        };
    }

    /// <summary>
    /// 「プロセスを開く」のダイアログ (ENG-32 の仕様 1〜3): 名前・PID・タイトルでの絞り込み、「自分のプロセスだけ表示」、「更新」、並べ替え、
    /// 盾・錠のアイコン (ツールチップ付き)、開く範囲 (全体・モジュール・範囲)、「読み取り専用で開く」(既定は設定「プロセスを読み取り専用で開く」)。
    /// </summary>
    internal async Task<ProcessDialogResult?> ShowOpenProcessDialogAsync(Func<IReadOnlyList<ProcessEntry>> load)
    {
        string title = Loc.Get("OpenProcess_Title");
        IReadOnlyList<ProcessEntry> processes = load();
        ListView list = DeviceList("OpenProcess_List", title);
        TextBox filter = DialogParts.Field("OpenProcess_Filter", Loc.Get("OpenProcess_Filter"), monospace: false);
        CheckBox ownOnly = DialogParts.Check("OpenProcess_OwnOnly", Loc.Get("OpenProcess_OwnOnly"), false);
        var refresh = new Button { Content = Loc.Get("OpenProcess_Refresh") };
        AutomationProperties.SetAutomationId(refresh, "OpenProcess_Refresh");
        ComboBox sort = DialogParts.Combo("OpenProcess_Sort", Loc.Get("OpenDevice_SortBy"),
            [.. Enum.GetValues<ProcessSort>().Select(s => Loc.Get("OpenProcess_Sort_" + s))], 0);

        void Fill()
        {
            int? selectedPid = (list.SelectedItem as DeviceListItem)?.Process?.Pid;
            var rows = ProcessListModel.Filter(processes.Where(p => p.Pid is not (0 or 4)), filter.Text, ownOnly.IsChecked == true,
                    (ProcessSort)Math.Max(0, sort.SelectedIndex), descending: sort.SelectedIndex == (int)ProcessSort.Memory)
                .Select(ProcessItem).ToList();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(r => r.Process?.Pid == selectedPid);
        }

        filter.TextChanged += (_, _) => Fill();
        ownOnly.Click += (_, _) => Fill();
        sort.SelectionChanged += (_, _) => Fill();
        refresh.Click += (_, _) =>
        {
            try
            {
                processes = load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Info($"Process list refresh failed: {ex.Message}");
            }

            Fill();
        };
        Fill();

        bool defaultReadOnly = App.Settings?.GetBool(DeviceSettings.ProcessReadOnlyKey, true) ?? true;
        CheckBox readOnly = DialogParts.Check("OpenDevice_ReadOnly", Loc.Get("OpenDevice_ReadOnly"), defaultReadOnly);
        RadioButton whole = DialogParts.Radio("OpenProcess_ScopeWhole", Loc.Get("OpenProcess_ScopeWhole"), "processScope", true);
        RadioButton module = DialogParts.Radio("OpenProcess_ScopeModule", Loc.Get("OpenProcess_ScopeModule"), "processScope", false);
        RadioButton range = DialogParts.Radio("OpenProcess_ScopeRange", Loc.Get("OpenProcess_ScopeRange"), "processScope", false);
        (CheckBox _, StackPanel rangePanel, Func<DeviceRangeInput> rangeInput, Func<long?, int?, string?> validate) = RangeInputs("OpenProcess", allowSectors: false);

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(filter);
        top.Children.Add(sort);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        options.Children.Add(ownOnly);
        options.Children.Add(refresh);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(top);
        panel.Children.Add(options);
        panel.Children.Add(list);
        panel.Children.Add(whole);
        panel.Children.Add(module);
        panel.Children.Add(range);
        panel.Children.Add(rangePanel);
        panel.Children.Add(readOnly);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 };

        ContentDialog dialog = DialogParts.Dialog(Root, "OpenProcessDialog", title, scroll, Loc.Get("Common_Open"));
        TextBlock error = DialogParts.Caption("OpenProcess_RangeError");
        rangePanel.Children.Add(error);
        void Validate()
        {
            rangePanel.Visibility = range.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            string? problem = range.IsChecked == true ? validate(null, 1) : null;
            error.Text = problem ?? string.Empty;
            dialog.IsPrimaryButtonEnabled = list.SelectedItem is DeviceListItem && problem is null;
        }

        list.SelectionChanged += (_, _) => Validate();
        foreach (RadioButton radio in new[] { whole, module, range })
        {
            radio.Checked += (_, _) => Validate();
        }

        foreach (TextBox box in rangePanel.Children.OfType<TextBox>())
        {
            box.TextChanged += (_, _) => Validate();
        }

        bool accepted = false;
        list.DoubleTapped += (_, _) =>
        {
            if (dialog.IsPrimaryButtonEnabled)
            {
                accepted = true;
                dialog.Hide();
            }
        };
        Validate();
        ContentDialogResult result = await dialog.ShowQueuedAsync();
        if ((result != ContentDialogResult.Primary && !accepted) || (list.SelectedItem as DeviceListItem)?.Process is not { } entry)
        {
            return null;
        }

        ProcessOpenScope scope = module.IsChecked == true ? ProcessOpenScope.Module : range.IsChecked == true ? ProcessOpenScope.Range : ProcessOpenScope.Whole;
        return new ProcessDialogResult(entry, readOnly.IsChecked == true, scope, scope == ProcessOpenScope.Range ? rangeInput() : null);
    }

    /// <summary>「プロセスを開く」の一覧の 1 行 (名前・PID・アーキテクチャ・ユーザー・タイトル・使用メモリ。ENG-32 の仕様 1)。</summary>
    private DeviceListItem ProcessItem(ProcessEntry p)
    {
        bool needsAdmin = p.Access == ProcessAccessLevel.NeedsElevation && !DeviceService.IsElevated;
        bool locked = p.Access == ProcessAccessLevel.Protected;
        string memory = p.CommitBytes is { } bytes ? StatusFormat.ShortSize(bytes, CultureInfo.CurrentCulture) ?? bytes.ToString("N0", CultureInfo.CurrentCulture)
            : Loc.Get("Common_Unknown");
        string detail = string.Join("  ", new[]
        {
            p.Architecture == ProcessArchitecture.Unknown ? null : p.Architecture.ToString(),
            p.User,
            memory,
            p.WindowTitle,
        }.Where(s => !string.IsNullOrEmpty(s)));
        ProcessEntry entry = p;
        return new DeviceListItem(ProcessDisplayName(p), detail, needsAdmin, locked,
            () => new DeviceOpenInfo { Path = $"process:{entry.Pid}", DisplayName = ProcessDisplayName(entry) }, false)
        {
            Process = entry,
            Size = p.CommitBytes,
        };
    }

    /// <summary>モジュールを選ぶ (ENG-32 の仕様 2 の「モジュールを選ぶ」)。取り消したら null。</summary>
    private async Task<ProcessModule?> ChooseModuleAsync(IReadOnlyList<ProcessModule> modules)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360, MinWidth = 420, FontFamily = DialogParts.Mono };
        AutomationProperties.SetAutomationId(list, "OpenProcess_Modules");
        AutomationProperties.SetName(list, Loc.Get("OpenProcess_ChooseModule"));
        var rows = modules.OrderBy(m => m.BaseAddress).ToList();
        list.ItemsSource = rows.Select(m => $"{m.Name}  0x{m.BaseAddress:X}  {m.Path}").ToList();
        ContentDialog dialog = DialogParts.Dialog(Root, "ChooseModuleDialog", Loc.Get("OpenProcess_ChooseModule"), list, Loc.Get("Common_Open"));
        dialog.IsPrimaryButtonEnabled = false;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0 ? rows[list.SelectedIndex] : null;
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
            bool valid = DiskImage.IsValidSectorSize(Chosen());
            dialog.IsPrimaryButtonEnabled = valid;

            // 範囲外・2 の累乗でないセクタサイズは入力欄を赤枠にする (ENG-31 の「エラー」)。
            DialogParts.MarkInvalid(custom, isCustom && !valid);
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

    private static string Unknown(string? value) => string.IsNullOrEmpty(value) ? Loc.Get("Common_Unknown") : value;

    private static string YesNoUnknown(bool? value) => value switch
    {
        true => Loc.Get("Common_Yes"),
        false => Loc.Get("Common_No"),
        _ => Loc.Get("Common_Unknown"),
    };

    /// <summary>物理ディスクの詳しい情報 (ENG-29 の仕様 1): 接続方式、パーティションの形式、論理 / 物理セクタサイズ、取り外し可能か、システムディスクか。</summary>
    private static string DiskDetail(DiskInfo disk)
    {
        string sectors = disk.LogicalSectorSize is { } l
            ? $"{l.ToString(CultureInfo.CurrentCulture)} / {(disk.PhysicalSectorSize ?? l).ToString(CultureInfo.CurrentCulture)}"
            : Loc.Get("Common_Unknown");
        return Loc.Format("OpenDisk_DiskDetail",
            disk.Bus == DiskBusType.Unknown ? Loc.Get("Common_Unknown") : Loc.Get("OpenDisk_Bus_" + disk.Bus),
            disk.PartitionStyle == DiskPartitionStyle.Unknown ? Loc.Get("Common_Unknown") : Loc.Get("OpenDisk_Style_" + disk.PartitionStyle),
            sectors, YesNoUnknown(disk.Removable), YesNoUnknown(disk.IsSystem));
    }

    /// <summary>ボリュームの詳しい情報 (ENG-29 の仕様 1): ファイルシステム、サイズ、所属する物理ディスク、BitLocker の状態。</summary>
    private static string VolumeDetail(VolumeDeviceInfo volume)
    {
        string size = volume.Size is { } bytes ? StatusFormat.ShortSize(bytes, CultureInfo.CurrentCulture) ?? bytes.ToString("N0", CultureInfo.CurrentCulture) : Loc.Get("Common_Unknown");
        string disk = volume.DiskNumber is { } n ? n.ToString(CultureInfo.CurrentCulture) : Loc.Get("Common_Unknown");
        return Loc.Format("OpenDisk_VolumeDetail", Unknown(volume.FileSystem), size, disk, Loc.Get("OpenDisk_BitLocker_" + volume.BitLocker));
    }

    private static string ProcessDisplayName(ProcessEntry entry) => Loc.Format("OpenProcess_Name", entry.Name, entry.Pid);

    private static string DeviceErrorMessage(DeviceException ex) => ex.ErrorCode switch
    {
        Win32Errors.AccessDenied or Win32Errors.SharingViolation or Win32Errors.LockViolation => Loc.Format("OpenDisk_AccessDenied", ex.Message),
        Win32Errors.NotReady => Loc.Get("OpenDisk_NoMedia"),
        Win32Errors.WriteProtect => Loc.Get("DiskWrite_WriteProtected"),
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
