using System.ComponentModel;
using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.Selection;
using HexEditor.Core.View;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// Hex ビューの強調 (FOR-16 の領域、FOR-15 のファイルの断片)、凡例、行の左端の印、ステータスバーの LBA・パーティション・クラスタ・持ち主 (FOR-06 の仕様 9、
/// FOR-15 の仕様 1)、Hex ビューの右クリックメニュー (FOR-06 の仕様 7、FOR-15)。
/// </summary>
public sealed partial class MainWindow
{
    private DocumentViewModel? _forensicStatusDocument;

    /// <summary>Hex ビューに強調と印を付ける (AttachAnnotations から呼ぶ)。</summary>
    private void AttachFileSystemHighlights(HexView view)
    {
        view.SetHighlightSource("forensicRegions", (start, end) => ForensicRegionHighlights(view, start, end));
        view.SetHighlightSource("fragments", (start, end) => FragmentHighlights(view, start, end));
        view.SetOffsetMarkerSource("forensicRegions", (start, end) => ForensicRegionMarks(view, start, end));
        view.MinimapFragments = () => DocumentOf(view) is { } d && StateOf(d) is { HighlightedFragments.Count: > 0 } s
            ? s.HighlightedFragments.Select(f => (f.Offset, f.Length))
            : [];
        UpdateForensicLegend(view);
    }

    /// <summary>ドキュメントを表示している Hex ビューの強調・凡例・ミニマップを描き直す。</summary>
    internal void RefreshForensicHighlights(DocumentViewModel d)
    {
        foreach (HexView view in _views.Where(v => DocumentOf(v) == d))
        {
            view.RefreshHighlights();
            view.Minimap?.RefreshMarks();
            UpdateForensicLegend(view);
        }
    }

    // ---- 領域の強調 (FOR-16) ----

    /// <summary>領域の種類ごとの模様 (仕様 2: スラックは斜線、未割り当ては点。RAM スラックとドライブスラックは向きの違う斜線)。</summary>
    internal static HexMark MarkOf(FsRegionKind kind) => kind switch
    {
        FsRegionKind.Metadata => HexMark.Horizontal,
        FsRegionKind.RamSlack => HexMark.Hatch,
        FsRegionKind.DriveSlack => HexMark.BackHatch,
        FsRegionKind.Unallocated => HexMark.Dots,
        FsRegionKind.VolumeSlack or FsRegionKind.PartitionGap => HexMark.CrossHatch,
        _ => HexMark.None,
    };

    /// <summary>行の左端の印の文字 (色だけに頼らない。仕様 2)。</summary>
    internal static string LetterOf(FsRegionKind kind) => Loc.Get("FsRegion_Letter_" + kind);

    /// <summary>背景の色 (テーマのブラシ。設定 forensics.highlight.colors で変えられる)。ハイコントラストでは塗らない。</summary>
    private Brush? RegionBackground(FsRegionKind kind, HexView view)
    {
        if (view.IsHighContrast)
        {
            return null;
        }

        if (CustomRegionColor(kind) is { } custom)
        {
            return new SolidColorBrush(custom);
        }

        string key = kind switch
        {
            FsRegionKind.Metadata => "SystemFillColorAttentionBackgroundBrush",
            FsRegionKind.RamSlack => "SystemFillColorCautionBackgroundBrush",
            FsRegionKind.DriveSlack => "SystemFillColorCriticalBackgroundBrush",
            FsRegionKind.Unallocated => "SystemFillColorSuccessBackgroundBrush",
            _ => "SystemFillColorNeutralBackgroundBrush",
        };
        return Application.Current.Resources.TryGetValue(key, out object brush) ? brush as Brush : null;
    }

    private static Brush MarkBrush(HexView view) =>
        (Brush)Application.Current.Resources[view.IsHighContrast ? "SystemColorWindowTextColorBrush" : "TextFillColorTertiaryBrush"];

    /// <summary>設定の色 (<c>unallocated=#80C080;ramSlack=#E0C060</c> の形)。</summary>
    private Windows.UI.Color? CustomRegionColor(FsRegionKind kind)
    {
        string text = App.Settings.GetString(HighlightColorsKey, string.Empty);
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && string.Equals(kv[0], kind.ToString(), StringComparison.OrdinalIgnoreCase) && kv[1].StartsWith('#') && kv[1].Length == 7
                && uint.TryParse(kv[1].AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                return Windows.UI.Color.FromArgb(0xC0, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
        }

        return null;
    }

    private IEnumerable<HexHighlight> ForensicRegionHighlights(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } d || StateOf(d) is not { HighlightOn: true, Session: { } session })
        {
            yield break;
        }

        Brush mark = MarkBrush(view);
        foreach (FsRegion r in session.Classifier.Classify(Math.Max(0, start), Math.Min(end, d.Document.Length)))
        {
            if (r.Kind is FsRegionKind.FileData or FsRegionKind.None)
            {
                continue;
            }

            yield return new HexHighlight(r.Offset, r.Length, CellLayer.ColoringRule, RegionBackground(r.Kind, view), null, null, "fs:" + r.Kind,
                LightBackground: true, Mark: MarkOf(r.Kind), MarkBrush: mark);
        }
    }

    /// <summary>行の左端の印: 行の中で最初に出てくる (ファイルのデータ以外の) 領域の文字。</summary>
    private IEnumerable<HexOffsetMarker> ForensicRegionMarks(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } d || StateOf(d) is not { HighlightOn: true, Session: { } session } || view.Editor is not { } editor)
        {
            yield break;
        }

        int perRow = Math.Max(1, editor.Layout.BytesPerRow);
        Brush fill = (Brush)Application.Current.Resources[view.IsHighContrast ? "SystemColorWindowColorBrush" : "SubtleFillColorSecondaryBrush"];
        Brush text = (Brush)Application.Current.Resources[view.IsHighContrast ? "SystemColorWindowTextColorBrush" : "TextFillColorPrimaryBrush"];
        Brush border = (Brush)Application.Current.Resources[view.IsHighContrast ? "SystemColorWindowTextColorBrush" : "ControlStrokeColorDefaultBrush"];
        IReadOnlyList<FsRegion> regions = session.Classifier.Classify(Math.Max(0, start), Math.Min(end, d.Document.Length));
        long rowStart = editor.Layout.RowStart(editor.Layout.RowOf(Math.Max(0, start)));
        for (long row = rowStart; row < end; row += perRow)
        {
            FsRegion? first = regions.FirstOrDefault(r => r.Kind is not (FsRegionKind.FileData or FsRegionKind.None) && r.Offset < row + perRow && r.End > row);
            if (first is { } f && row >= start)
            {
                yield return new HexOffsetMarker(row, fill, border, LetterOf(f.Kind), text, "fs:" + f.Kind);
            }
        }
    }

    /// <summary>凡例 (仕様 2: Hex ビューの右上。既定は表示)。強調がオンのときだけ。</summary>
    private void UpdateForensicLegend(HexView view)
    {
        bool show = DocumentOf(view) is { } d && StateOf(d) is { HighlightOn: true, Session: not null } && App.Settings.GetBool(HighlightLegendKey, true);
        if (!show)
        {
            view.SetOverlay("forensicLegend", null);
            return;
        }

        var panel = new StackPanel { Spacing = 2 };
        foreach (FsRegionKind kind in new[] { FsRegionKind.Metadata, FsRegionKind.RamSlack, FsRegionKind.DriveSlack, FsRegionKind.Unallocated, FsRegionKind.VolumeSlack, FsRegionKind.PartitionGap })
        {
            var swatch = new Grid { Width = 28, Height = 14, Background = RegionBackground(kind, view) };
            swatch.Children.Add(new Border { BorderBrush = MarkBrush(view), BorderThickness = new Thickness(1) });
            swatch.Children.Add(SwatchPattern(kind, MarkBrush(view)));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = LetterOf(kind), FontFamily = DialogParts.Mono, Width = 12 });
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = Loc.Get("FsRegion_" + kind), FontSize = 12 });
            panel.Children.Add(row);
        }

        var legend = new Border
        {
            Child = panel,
            Padding = new Thickness(8, 6, 8, 6),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorTertiaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
            Opacity = 0.95,
        };
        AutomationProperties.SetAutomationId(legend, "ForensicLegend");
        AutomationProperties.SetName(legend, Loc.Get("FsRegion_Legend"));
        view.SetOverlay("forensicLegend", legend);
    }

    /// <summary>凡例の見本の模様 (Hex ビューの模様と同じ形)。</summary>
    private static Microsoft.UI.Xaml.Shapes.Path SwatchPattern(FsRegionKind kind, Brush brush)
    {
        var group = new GeometryGroup();
        const double w = 28, h = 14;
        switch (MarkOf(kind))
        {
            case HexMark.Hatch:
                for (double i = -h; i < w; i += 6)
                {
                    group.Children.Add(new LineGeometry { StartPoint = new Windows.Foundation.Point(i, h), EndPoint = new Windows.Foundation.Point(i + h, 0) });
                }

                break;
            case HexMark.BackHatch:
                for (double i = -h; i < w; i += 6)
                {
                    group.Children.Add(new LineGeometry { StartPoint = new Windows.Foundation.Point(i, 0), EndPoint = new Windows.Foundation.Point(i + h, h) });
                }

                break;
            case HexMark.Dots:
                for (double y = 3; y < h; y += 5)
                {
                    for (double x = 2; x < w; x += 5)
                    {
                        group.Children.Add(new LineGeometry { StartPoint = new Windows.Foundation.Point(x, y), EndPoint = new Windows.Foundation.Point(x + 0.75, y) });
                    }
                }

                break;
            case HexMark.Horizontal:
                for (double y = 2; y < h; y += 4)
                {
                    group.Children.Add(new LineGeometry { StartPoint = new Windows.Foundation.Point(0, y), EndPoint = new Windows.Foundation.Point(w, y) });
                }

                break;
            case HexMark.CrossHatch:
                for (double i = -h; i < w; i += 8)
                {
                    group.Children.Add(new LineGeometry { StartPoint = new Windows.Foundation.Point(i, h), EndPoint = new Windows.Foundation.Point(i + h, 0) });
                    group.Children.Add(new LineGeometry { StartPoint = new Windows.Foundation.Point(i, 0), EndPoint = new Windows.Foundation.Point(i + h, h) });
                }

                break;
        }

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = group,
            Stroke = brush,
            StrokeThickness = MarkOf(kind) == HexMark.Dots ? 1.5 : 1,
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, h) },
        };
    }

    /// <summary>
    /// 強調の切り替え (FOR-16 の仕様 3: 既定はオフ)。初めてオンにしたときは、ファイルの末尾クラスタの一覧を作る長時間処理を行い、終わるまでスラックの
    /// 強調を出さない (「スラックの位置を計算中」を InfoBar で示す。仕様 4)。
    /// </summary>
    private void ToggleForensicHighlight()
    {
        if (Vm.Selected is not { } d || StateOf(d) is not { Session: { } session } state)
        {
            return;
        }

        if (!session.Classifier.CanClassify)
        {
            ShowNotice(Loc.Get("Fs_HighlightUnavailable"), InfoBarSeverity.Warning, d);
            return;
        }

        state.HighlightOn = !state.HighlightOn;
        if (state.HighlightOn && session.Classifier.Tails is null && !state.TailsRunning)
        {
            state.TailsRunning = true;
            Core.Notifications.Notification notice = ShowNotice(Loc.Get("Fs_ComputingSlack"), InfoBarSeverity.Informational, d);
            _ = Vm.Operations.RunAsync(Loc.Get("Fs_ComputingSlack"), Core.Operations.OperationKind.ReadOnly, d.Document, 1_000_000, op =>
            {
                FileTailTable tails = FileTailTable.Build(session.FileSystem, keepPaths: false, OperationProgress(op), op.CancellationToken);
                DispatcherQueue.TryEnqueue(() =>
                {
                    session.Classifier.Tails = tails;
                    state.TailsRunning = false;
                    Vm.Notifications.Dismiss(notice);
                    RefreshForensicHighlights(d);
                    state.RaiseChanged();
                });
                return Task.CompletedTask;
            }).ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        state.TailsRunning = false;
                        Vm.Notifications.Dismiss(notice);
                    });
                }
            }, TaskScheduler.Default);
        }

        RefreshForensicHighlights(d);
        state.RaiseChanged();
        Commands.NotifyStatesChanged();
    }

    // ---- ファイルの断片の強調 (FOR-15 の仕様 4・5) ----

    private IEnumerable<HexHighlight> FragmentHighlights(HexView view, long start, long end)
    {
        if (DocumentOf(view) is not { } d || StateOf(d) is not { HighlightedFragments.Count: > 0 } state)
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        Brush border = (Brush)Application.Current.Resources[hc ? "SystemColorHighlightColorBrush" : "AccentFillColorDefaultBrush"];
        Brush? background = hc ? null : (Brush)Application.Current.Resources["SystemFillColorAttentionBackgroundBrush"];
        int index = 0;
        foreach ((long offset, long length, _) in state.HighlightedFragments)
        {
            index++;
            if (offset < end && offset + length > start)
            {
                yield return new HexHighlight(offset, length, CellLayer.ColoringRule, background, border, hc ? [3, 2] : null, "fragment:" + index,
                    Thickness: 2);
            }
        }
    }

    /// <summary>「ファイルのクラスタを表示」(FOR-15 の仕様 4): 全断片を強調して選択し、断片の一覧をパネルに出す。</summary>
    private void ShowSelectedFileClusters()
    {
        if (Vm.Selected is { } d && FileSystemView?.SelectedEntries is { Count: > 0 } entries)
        {
            ShowFileClusters(d, entries[0]);
        }
    }

    internal void ShowFileClusters(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d) is not { Session: { } session } state)
        {
            return;
        }

        state.HighlightedEntry = entry;
        state.HighlightedFragments = session.DocumentFragments(entry);
        if (state.HighlightedFragments.Count > 0)
        {
            d.Editor.SelectFragments(state.HighlightedFragments.Select(f => new ByteRange(f.Offset, Math.Min(f.Length, d.Document.Length - f.Offset))));
        }

        ShowPanel(FragmentsPanelId, focus: false);
        state.RaiseChanged();
        RefreshForensicHighlights(d);
        Commands.NotifyStatesChanged();
        UpdateGoMenuFragments();
    }

    /// <summary>「次の断片」「前の断片」(仕様 5)。</summary>
    private void MoveToFragment(bool next)
    {
        if (Vm.Selected is not { } d || StateOf(d) is not { HighlightedFragments.Count: > 0 } state)
        {
            return;
        }

        (long, long)[] fragments = [.. state.HighlightedFragments.Select(f => (f.Offset, f.Length))];
        long cursor = d.Editor.Cursor;
        long? target = next ? FragmentNavigation.Next(fragments, cursor) : FragmentNavigation.Previous(fragments, cursor);
        if (target is long t)
        {
            d.Editor.GoTo(t);
        }
    }

    /// <summary>強調の表示中だけ、移動メニューに「次の断片」「前の断片」を出す (仕様 5)。</summary>
    private void UpdateGoMenuFragments()
    {
        bool show = Vm.Selected is { } d && StateOf(d)?.HighlightedFragments.Count > 0;
        GoNextFragmentItem.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        GoPreviousFragmentItem.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>断片の強調を外す。</summary>
    internal void ClearFileClusters(DocumentViewModel d)
    {
        if (StateOf(d) is { } state)
        {
            state.HighlightedEntry = null;
            state.HighlightedFragments = [];
            state.RaiseChanged();
            RefreshForensicHighlights(d);
            UpdateGoMenuFragments();
        }
    }

    // ---- ステータスバー (FOR-06 の仕様 9、FOR-15 の仕様 1、FOR-09 の仕様 6) ----

    private void HookForensicStatus()
    {
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Selected) or nameof(MainViewModel.StatusDocument) or "" or null)
            {
                AttachForensicStatusDocument();
            }
        };
        AttachForensicStatusDocument();
    }

    private void AttachForensicStatusDocument()
    {
        if (_forensicStatusDocument is { } old)
        {
            old.PropertyChanged -= OnForensicStatusDocumentChanged;
        }

        _forensicStatusDocument = Vm.StatusDocument;
        if (_forensicStatusDocument is { } doc)
        {
            doc.PropertyChanged += OnForensicStatusDocumentChanged;
        }

        UpdateForensicStatus();
        UpdateGoMenuFragments();
    }

    private void OnForensicStatusDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.CursorText) or "" or null)
        {
            UpdateForensicStatus();
        }
    }

    /// <summary>ステータスバーの持ち主の表示に「調べる」が出ているか (押すと逆引き表を作る)。</summary>
    private bool _forensicOwnerLink;

    /// <summary>ステータスバーの項目の文字列 (テスト用の読み出しにも使う)。</summary>
    internal string ForensicStatusText { get; private set; } = string.Empty;

    /// <summary>
    /// ステータスバーの項目を更新する: ディスクは LBA とパーティションの番号、ファイルシステムは LBA・クラスタ番号・クラスタ内の位置・持ち主、ファイルのタブは
    /// ディスク上のオフセット。
    /// </summary>
    internal void UpdateForensicStatus()
    {
        _forensicOwnerLink = false;
        string text = Vm.StatusDocument is { } d ? ForensicStatusOf(d, out _forensicOwnerLink) : string.Empty;
        ForensicStatusText = text;
        if (!ReferenceEquals(StatusForensic.Content as string, text) && (string?)StatusForensic.Content != text)
        {
            StatusForensic.Content = text;
            QueueStatusBarLayout();
        }
    }

    private string ForensicStatusOf(DocumentViewModel d, out bool ownerLink)
    {
        ownerLink = false;
        FileSystemDocumentState? state = FileSystemDocumentState.Find(d.Document);
        long cursor = d.Editor.Cursor;
        var parts = new List<string>();
        if (state?.DiskOffsetOf is { } map)
        {
            parts.Add(map(cursor) is long diskOffset ? Loc.Format("FsStatus_DiskOffset", "0x" + diskOffset.ToString("X", CultureInfo.InvariantCulture)) : Loc.Get("FsStatus_NoDiskOffset"));
            return string.Join("  ·  ", parts);
        }

        int sector = SectorSizeOf(d);
        bool disk = IsDiskDocument(d) || state?.PartitionParent is not null || state?.Session is not null;
        if (!disk)
        {
            return string.Empty;
        }

        long absolute = cursor + (d.LinkParent is not null ? d.Document.LinkStart : 0);
        parts.Add(Loc.Format("FsStatus_Lba", Number(absolute / sector)));
        if (state?.Partitions?.PartitionAt(cursor) is { } row)
        {
            parts.Add(Loc.Format("FsStatus_Partition", row.Number));
        }
        else if (state?.PartitionRow is { } own)
        {
            parts.Add(Loc.Format("FsStatus_Partition", own.Number));
        }

        if (state?.Session is { } session && session.ClusterAtDocument(cursor) is (long cluster, long within))
        {
            parts.Add(Loc.Format(session.FileSystem.UsesBlocks ? "FsStatus_Block" : "FsStatus_Cluster", Number(cluster),
                "0x" + within.ToString("X", CultureInfo.InvariantCulture)));
            parts.Add(OwnerText(session, cluster, cursor, out ownerLink));
        }

        return string.Join("  ·  ", parts);
    }

    /// <summary>持ち主 (ファイルのパス、メタデータ名、「未割り当て」、「スラック」)。逆引き表がなければ「調べる」。</summary>
    private string OwnerText(FileSystemSession session, long cluster, long cursor, out bool link)
    {
        link = false;
        if (session.Owners is { } owners)
        {
            IReadOnlyList<ClusterOwner> found = owners.OwnersOf(cluster);
            ClusterOwner? live = found.FirstOrDefault(o => o.Kind != ClusterOwnerKind.Deleted);
            if (live is not null)
            {
                return Loc.Format("FsStatus_Owner", live.Path);
            }

            return found.Count > 0 ? Loc.Format("FsStatus_OwnerDeleted", found[0].Path) : Loc.Get("FsStatus_Unallocated");
        }

        FsRegionKind kind = session.Classifier.Classify(cursor, cursor + 1).FirstOrDefault().Kind;
        switch (kind)
        {
            case FsRegionKind.Unallocated:
                return Loc.Get("FsStatus_Unallocated");
            case FsRegionKind.RamSlack or FsRegionKind.DriveSlack:
                return Loc.Get("FsStatus_Slack");
        }

        link = true;
        return Loc.Get("FsStatus_FindOwner");
    }

    private void StatusForensic_Click(object sender, RoutedEventArgs e)
    {
        if (_forensicOwnerLink)
        {
            _ = BuildOwnerMapAsync(Vm.Selected);
        }
    }

    // ---- Hex ビューの右クリックメニュー ----

    private static readonly (string Id, string TextKey)[] ForensicHexMenuCommands =
    [
        ("forensics.partitions.openHere", "Cmd_forensics_partitions_openHere"),
        ("forensics.fs.whoUsesThis", "Cmd_forensics_fs_whoUsesThis"),
        ("forensics.fs.goToCluster", "Cmd_forensics_fs_goToCluster"),
    ];

    private void ExtendHexViewForensicsMenu(MenuFlyout menu)
    {
        const string SeparatorId = "HexViewMenu_ForensicsSeparator";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == SeparatorId))
        {
            var separator = new MenuFlyoutSeparator();
            AutomationProperties.SetAutomationId(separator, SeparatorId);
            menu.Items.Add(separator);
            foreach ((string id, string key) in ForensicHexMenuCommands)
            {
                var item = new MenuFlyoutItem { Text = Loc.Get(key), Tag = "Command" };
                AutomationProperties.SetAutomationId(item, "HexViewMenu_" + id);
                item.Click += (_, _) => _ = Commands.ExecuteAsync(id);
                menu.Items.Add(item);
            }
        }

        bool disk = Vm.Selected is { } d && (IsDiskDocument(d) || StateOf(d)?.PartitionParent is not null);
        bool fs = Vm.Selected is { } doc && StateOf(doc)?.Session is not null;
        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            string id = AutomationProperties.GetAutomationId(item);
            if (id == SeparatorId)
            {
                item.Visibility = disk || fs ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (ForensicHexMenuCommands.FirstOrDefault(c => "HexViewMenu_" + c.Id == id) is { Id: { } command } && item is MenuFlyoutItem m)
            {
                bool wanted = command == "forensics.partitions.openHere" ? disk : fs;
                m.Visibility = wanted ? Visibility.Visible : Visibility.Collapsed;
                m.IsEnabled = Commands.StateOf(command).Enabled;
                if (command == "forensics.fs.goToCluster" && Vm.Selected is { } current && StateOf(current)?.Session is { } s)
                {
                    m.Text = Loc.Get(s.FileSystem.UsesBlocks ? "Fs_GoToBlock" : "Cmd_forensics_fs_goToCluster");
                }
            }
        }
    }
}
