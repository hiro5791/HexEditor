using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// 別ファイルとの並列表示と同期スクロール (VIEW-39)。操作中のタブ (左) の右に、選んだドキュメントのビューを最大 3 つ並べる (合わせて 4 つ)。
/// 同期は <see cref="ViewSync"/> (Core。比較 ANA-04 も同じ部品を使う) で行い、「違いを強調」は表示中の範囲だけを比べて層 11 の強調にする。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>並べられるビューの数 (左を含む。VIEW-39 の仕様 1)。</summary>
    public const int MaxSideBySide = 4;

    /// <summary>並べて表示の組 1 つ (左のドキュメントと、右に並べたドキュメント)。</summary>
    internal sealed class SideBySideGroup(DocumentViewModel left) : IDisposable
    {
        public DocumentViewModel Left { get; } = left;

        public List<DocumentViewModel> Partners { get; } = [];

        public List<HexView> Views { get; } = [];

        public ViewSync? Sync { get; private set; }

        /// <summary>同期のモード (並べた時点で「同じオフセット」を有効にする。VIEW-39 の仕様 2)。</summary>
        public SyncMode Mode => Sync?.Mode ?? SyncMode.Off;

        /// <summary>「違いを強調」(VIEW-39 の仕様 5。既定オフ)。</summary>
        public bool HighlightDifferences { get; set; }

        /// <summary>同期するビュー (左が先頭)。</summary>
        public IEnumerable<EditorState> Editors => [Left.Editor, .. Partners.Select(p => p.PrimaryEditor)];

        /// <summary>「比較に従う」の対応位置 (2 つを並べ、その 2 つの比較の結果があるときだけ。VIEW-39 の仕様 2)。</summary>
        public IOffsetMapper? Mapper { get; set; }

        public void Resync(SyncMode mode, bool selection)
        {
            // 比較の結果がなくなった (並べたドキュメントが変わった) ら「同じオフセット」に戻す。
            if (mode == SyncMode.Mapped && (Mapper is null || Partners.Count != 1))
            {
                mode = SyncMode.SameOffset;
            }

            Sync?.Dispose();
            Sync = new ViewSync(Editors, mode) { SyncSelection = selection, Mapper = Mapper };
            if (mode == SyncMode.SameOffset)
            {
                // 並べた時点・同期を有効にした時点で、左に合わせる。
                Sync.SyncFrom(Left.Editor);
            }
        }

        public void Dispose()
        {
            Sync?.Dispose();
            Sync = null;
        }
    }

    private readonly List<SideBySideGroup> _sideBySide = [];
    private bool _sideBySideHooked;

    /// <summary>ドキュメントを含む並べて表示の組。</summary>
    internal SideBySideGroup? SideBySideOf(DocumentViewModel? doc) =>
        doc is null ? null : _sideBySide.FirstOrDefault(g => g.Left == doc || g.Partners.Contains(doc));

    private CommandState SideBySideState() => Vm.Selected is not { } doc ? NeedsDocument()
        : Vm.Documents.Count(d => !ReferenceEquals(d.Document, doc.Document)) == 0 ? CommandState.Unavailable(Loc.Get("Command_NoOtherDocument"))
        : SideBySideOf(doc) is { } group && group.Partners.Count >= MaxSideBySide - 1
            ? CommandState.Unavailable(Loc.Format("Command_SideBySideLimit", MaxSideBySide))
            : CommandState.Available;

    /// <summary>
    /// 「並べて表示…」(VIEW-39 の仕様 1)。引数にタブの番号 (0 始まり) か表示名を渡すとそのドキュメントを、なければ一覧のメニューを出す。
    /// </summary>
    private Task ShowSideBySideAsync(string? argument)
    {
        if (Vm.Selected is not { } left || !SideBySideState().Enabled)
        {
            return Task.CompletedTask;
        }

        List<DocumentViewModel> candidates = [.. Vm.Documents.Where(d => !ReferenceEquals(d.Document, left.Document)
            && SideBySideOf(left)?.Partners.Contains(d) != true)];
        DocumentViewModel? chosen = argument is { Length: > 0 }
            ? int.TryParse(argument, out int index) && index >= 0 && index < Vm.Documents.Count ? Vm.Documents[index]
                : candidates.FirstOrDefault(d => string.Equals(d.DisplayName, argument, StringComparison.OrdinalIgnoreCase))
            : candidates.Count == 1 ? candidates[0] : null;
        if (chosen is not null)
        {
            AddSideBySide(left, chosen);
            return Task.CompletedTask;
        }

        // 選ぶメニュー (開いているドキュメント)。
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "SideBySideMenu");
        foreach (DocumentViewModel d in candidates)
        {
            var item = new MenuFlyoutItem { Text = d.DisplayName };
            AutomationProperties.SetAutomationId(item, "SideBySide_" + Vm.Documents.IndexOf(d));
            item.Click += (_, _) => AddSideBySide(left, d);
            menu.Items.Add(item);
        }

        if (SelectedView() is { } view)
        {
            menu.ShowAt(view, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = new Windows.Foundation.Point(view.ContentLeft, 0) });
        }

        return Task.CompletedTask;
    }

    /// <summary><paramref name="partner"/> を <paramref name="left"/> の右に並べ、「同じオフセット」で同期する。</summary>
    internal void AddSideBySide(DocumentViewModel left, DocumentViewModel partner)
    {
        HookSideBySide();
        SideBySideGroup group = SideBySideOf(left) ?? NewGroup(left);
        if (group.Partners.Contains(partner) || group.Partners.Count >= MaxSideBySide - 1)
        {
            return;
        }

        group.Partners.Add(partner);
        group.Resync(SyncMode.SameOffset, App.Settings.GetBool("view.sync.selection", false));
        ShowSideBySide();
        UpdateSyncStatus();
        RefreshCommandUi();

        SideBySideGroup NewGroup(DocumentViewModel l)
        {
            var g = new SideBySideGroup(l);
            _sideBySide.Add(g);
            return g;
        }
    }

    // ---- セッション (VIEW-39 の仕様 7) ----

    /// <summary>並べて表示の組の記録。番号はセッションに書くタブ (復元できるタブ) の中の番号。</summary>
    private List<SessionSideBySide> CaptureSideBySide()
    {
        List<DocumentViewModel> restorable = [.. Vm.Documents.Where(d => SessionRules.IsRestorable(d.ToSessionTab()))];
        var groups = new List<SessionSideBySide>();
        foreach (SideBySideGroup group in _sideBySide)
        {
            int left = restorable.IndexOf(group.Left);
            List<int> partners = [.. group.Partners.Select(p => restorable.IndexOf(p)).Where(i => i >= 0)];
            if (left >= 0 && partners.Count > 0)
            {
                groups.Add(new SessionSideBySide
                {
                    Left = left,
                    Partners = partners,
                    Mode = group.Mode.ToString(),
                    Differences = group.HighlightDifferences,
                });
            }
        }

        return groups;
    }

    /// <summary>
    /// 記録した並べて表示の組を戻す。<paramref name="first"/> は復元したタブの最初の番号。まだ開いていない (遅延して開く) タブは開く。
    /// 「比較に従う」は比較の結果がないので「同じオフセット」で戻す。
    /// </summary>
    private void RestoreSideBySide(SessionWindow window, int first)
    {
        if (window.SideBySide.Count == 0)
        {
            return;
        }

        DocumentViewModel? selected = Vm.Selected;
        DocumentViewModel? At(int index)
        {
            int i = first + index;
            if (i < 0 || i >= Vm.Documents.Count)
            {
                return null;
            }

            if (Vm.Documents[i].IsPending)
            {
                MaterializePending(Vm.Documents[i]);
            }

            return Vm.Documents[i] is { IsPending: false, IsMissing: false } d ? d : null;
        }

        foreach (SessionSideBySide record in window.SideBySide)
        {
            if (At(record.Left) is not { } left)
            {
                continue;
            }

            foreach (int partner in record.Partners)
            {
                if (At(partner) is { } p && !ReferenceEquals(p.Document, left.Document))
                {
                    AddSideBySide(left, p);
                }
            }

            if (SideBySideOf(left) is { } group)
            {
                SyncMode mode = Enum.TryParse(record.Mode, out SyncMode m) && m != SyncMode.Mapped ? m : SyncMode.SameOffset;
                group.Resync(mode, App.Settings.GetBool("view.sync.selection", false));
                group.HighlightDifferences = record.Differences;
                UpdateDifferenceSources(group);
            }
        }

        if (selected is not null && Vm.Documents.Contains(selected))
        {
            Vm.Selected = selected;
        }

        ShowSideBySide();
        UpdateSyncStatus();
    }

    private void HookSideBySide()
    {
        if (_sideBySideHooked)
        {
            return;
        }

        _sideBySideHooked = true;

        // 一方のドキュメントが閉じられたら、並列表示を解除し、同期も解除する (VIEW-39 の「エラー」)。
        Vm.Documents.CollectionChanged += (_, _) =>
        {
            foreach (SideBySideGroup group in _sideBySide.ToList())
            {
                if (!Vm.Documents.Contains(group.Left))
                {
                    RemoveGroup(group);
                    continue;
                }

                if (group.Partners.RemoveAll(p => !Vm.Documents.Contains(p)) > 0)
                {
                    if (group.Partners.Count == 0)
                    {
                        RemoveGroup(group);
                    }
                    else
                    {
                        group.Resync(group.Mode, App.Settings.GetBool("view.sync.selection", false));
                    }
                }
            }

            ShowSideBySide();
            UpdateSyncStatus();
        };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                ShowSideBySide();
            }
        };
    }

    private void RemoveGroup(SideBySideGroup group)
    {
        group.Dispose();
        _sideBySide.Remove(group);
        foreach (HexView view in group.Views)
        {
            view.SetHighlightSource("differences", null);
            view.Editor = null;
        }

        group.Views.Clear();
    }

    /// <summary>選択中のタブの組の右のビューを並べる。組がなければ右の列を隠す。</summary>
    private void ShowSideBySide()
    {
        SideBySideGroup? group = Vm.Selected is { } doc ? _sideBySide.FirstOrDefault(g => g.Left == doc) : null;
        SideBySideHost.Children.Clear();
        SideBySideHost.ColumnDefinitions.Clear();
        if (group is null || group.Partners.Count == 0)
        {
            SideBySideHost.Visibility = Visibility.Collapsed;
            SideBySideColumn.Width = new GridLength(0);
            UpdateDifferenceSources(null);
            return;
        }

        // 新しい Hex ビューを作り直す (並べたドキュメントのビューを表示する)。
        foreach (HexView old in group.Views)
        {
            old.Editor = null;
        }

        group.Views.Clear();
        for (int i = 0; i < group.Partners.Count; i++)
        {
            DocumentViewModel partner = group.Partners[i];
            SideBySideHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var panel = new Grid { BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(panel, i);

            // 見出し: ドキュメント名、同期のモードの切り替え (鎖の図柄)、閉じる。
            var header = new Grid { Padding = new Thickness(8, 2, 2, 2), ColumnSpacing = 4 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = partner.DisplayName, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            AutomationProperties.SetAutomationId(name, "SideBySide_Name" + (i + 1));
            header.Children.Add(name);
            var chain = new ToggleButton
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                IsChecked = group.Mode != SyncMode.Off,
                Padding = new Thickness(6, 2, 6, 2),
            };
            string chainName = Loc.Get("SideBySide_Sync");
            AutomationProperties.SetName(chain, chainName);
            AutomationProperties.SetAutomationId(chain, "SideBySide_Sync" + (i + 1));
            ToolTipService.SetToolTip(chain, chainName);
            chain.Click += (_, _) => ToggleSync();
            Grid.SetColumn(chain, 1);
            header.Children.Add(chain);
            var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 2, 6, 2) };
            string closeName = Loc.Get("SideBySide_Close");
            AutomationProperties.SetName(close, closeName);
            AutomationProperties.SetAutomationId(close, "SideBySide_Close" + (i + 1));
            ToolTipService.SetToolTip(close, closeName);
            DocumentViewModel removed = partner;
            close.Click += (_, _) => RemoveSideBySide(group, removed);
            Grid.SetColumn(close, 2);
            header.Children.Add(close);
            panel.Children.Add(header);

            var view = new HexView { DataContext = partner };
            AutomationProperties.SetAutomationId(view, "SideBySideView" + (i + 1));
            view.CommandRequested += HexView_CommandRequested;
            view.EditRejected += HexView_EditRejected;
            view.Loaded += HexView_Loaded;

            Grid.SetRow(view, 1);
            panel.Children.Add(view);
            view.Editor = partner.PrimaryEditor;
            group.Views.Add(view);
            SideBySideHost.Children.Add(panel);
        }

        SideBySideColumn.Width = new GridLength(group.Partners.Count, GridUnitType.Star);
        SideBySideHost.Visibility = Visibility.Visible;
        UpdateDifferenceSources(group);
    }

    private void RemoveSideBySide(SideBySideGroup group, DocumentViewModel partner)
    {
        group.Partners.Remove(partner);
        if (group.Partners.Count == 0)
        {
            RemoveGroup(group);
        }
        else
        {
            group.Resync(group.Mode, App.Settings.GetBool("view.sync.selection", false));
        }

        ShowSideBySide();
        UpdateSyncStatus();
        RefreshCommandUi();
    }

    /// <summary>同期のモードを変える (VIEW-39 の仕様 2)。「位置の差を保つ」は変えた時点の差を保つ。</summary>
    private void SetSyncMode(SyncMode mode)
    {
        if (SideBySideOf(Vm.Selected) is not { } group)
        {
            return;
        }

        group.Mapper = mode == SyncMode.Mapped ? CompareMapperFor(group) : null;
        group.Resync(mode, App.Settings.GetBool("view.sync.selection", false));
        if (mode == SyncMode.Mapped)
        {
            group.Sync?.SyncFrom(group.Left.Editor);
        }

        ShowSideBySide();
        UpdateSyncStatus();
        RefreshCommandUi();
    }

    /// <summary>
    /// 「比較に従う」の対応位置 (VIEW-39 の仕様 2)。並べた 2 つのドキュメントを比べた比較タブ (ANA-01) があり、結果があるときだけ。
    /// 対応の求め方は比較の同期スクロールと同じ (<see cref="Core.Compare.DiffNavigation.Map"/>)。
    /// </summary>
    internal IOffsetMapper? CompareMapperFor(SideBySideGroup group)
    {
        if (group.Partners.Count != 1)
        {
            return null;
        }

        Document left = group.Left.Document;
        Document right = group.Partners[0].Document;
        foreach (CompareSessionViewModel session in Compares)
        {
            if (session.Result is null)
            {
                continue;
            }

            Document? a = session.Left.Owner?.Document;
            Document? b = session.Right.Owner?.Document;
            if (ReferenceEquals(a, left) && ReferenceEquals(b, right))
            {
                return new CompareOffsetMapper(session, firstIsRight: false);
            }

            if (ReferenceEquals(a, right) && ReferenceEquals(b, left))
            {
                return new CompareOffsetMapper(session, firstIsRight: true);
            }
        }

        return null;
    }

    /// <summary>比較の結果による対応位置。ビューの 0 番目 (左) が比較のどちら側かを持つ。比較し直したら新しい結果を使う。</summary>
    private sealed class CompareOffsetMapper(CompareSessionViewModel session, bool firstIsRight) : IOffsetMapper
    {
        public long Map(long offset, int from, int to)
        {
            if (from == to || session.Result is not { } result)
            {
                return offset;
            }

            bool fromRight = from == 0 ? firstIsRight : !firstIsRight;
            return Math.Max(0, Core.Compare.DiffNavigation.Map(result, fromRight, offset));
        }
    }

    /// <summary>「表示: 同期スクロールの切り替え」: オフと「同じオフセット」を切り替える。</summary>
    private void ToggleSync()
    {
        if (SideBySideOf(Vm.Selected) is { } group)
        {
            SetSyncMode(group.Mode == SyncMode.Off ? SyncMode.SameOffset : SyncMode.Off);
        }
    }

    private void ToggleSyncDifferences()
    {
        if (SideBySideOf(Vm.Selected) is { } group)
        {
            group.HighlightDifferences = !group.HighlightDifferences;
            UpdateDifferenceSources(group);
            RefreshCommandUi();
        }
    }

    /// <summary>ステータスバーの「同期」(VIEW-39 の仕様 6)。同期スクロール中だけ。</summary>
    private string SyncStatusText() =>
        SideBySideOf(Vm.Selected) is { Mode: not SyncMode.Off } || Vm.Selected is { PaneSyncEnabled: true } ? Loc.Get("Status_Sync") : string.Empty;

    private void UpdateSyncStatus()
    {
        if (StatusSync is not null)
        {
            StatusSync.Content = SyncStatusText();
            QueueStatusBarLayout();
        }
    }

    // ---- 違いを強調 (VIEW-39 の仕様 5) ----

    private const string DifferenceSource = "differences";

    /// <summary>
    /// 「違いを強調」: 表示中の範囲で、相手のビューと同じオフセットの値が違うバイトを層 11 (差分) で強調する。表示中の範囲だけを、キャッシュに
    /// あるデータだけで比べる (読み込みを待たない。比較機能 ANA とは別に動く)。
    /// </summary>
    private void UpdateDifferenceSources(SideBySideGroup? group)
    {
        HexView? leftView = group is null ? null : _views.FirstOrDefault(v => ReferenceEquals(v.Editor, group.Left.Editor) && !group.Views.Contains(v));
        foreach (HexView view in _views)
        {
            view.SetHighlightSource(DifferenceSource, null);
        }

        if (group is not { HighlightDifferences: true })
        {
            return;
        }

        var documents = new List<(HexView View, Document Document)>();
        if (leftView is not null)
        {
            documents.Add((leftView, group.Left.Document));
        }

        for (int i = 0; i < group.Views.Count; i++)
        {
            documents.Add((group.Views[i], group.Partners[i].Document));
        }

        foreach ((HexView view, Document doc) in documents)
        {
            Document[] others = [.. documents.Where(d => !ReferenceEquals(d.Document, doc)).Select(d => d.Document)];
            HexView target = view;
            view.SetHighlightSource(DifferenceSource, (start, end) => Differences(target, doc, others, start, end));
        }

        foreach (Document doc in documents.Select(d => d.Document).Distinct())
        {
            // 読み込みが終わったら、もう一度比べる。
            doc.DataLoaded -= Difference_DataLoaded;
            doc.DataLoaded += Difference_DataLoaded;
            doc.Changed -= Difference_Changed;
            doc.Changed += Difference_Changed;
        }
    }

    private void Difference_DataLoaded(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshDifferences);

    private void Difference_Changed(object? sender, DocumentChangedEventArgs e) => RefreshDifferences();

    private void RefreshDifferences()
    {
        foreach (HexView view in _views)
        {
            view.RefreshHighlights();
        }
    }

    private static IEnumerable<HexHighlight> Differences(HexView view, Document doc, Document[] others, long start, long end)
    {
        if (doc.IsDisposed || others.Length == 0)
        {
            yield break;
        }

        start = Math.Max(0, start);
        int length = (int)Math.Clamp(end - start, 0, 1 << 20);
        if (length == 0)
        {
            yield break;
        }

        byte[] mine = new byte[length];
        var mineStates = new ByteState[length];
        int read = doc.Current.ReadForDisplay(start, mine, mineStates);
        bool[] differ = new bool[read];
        byte[] theirs = new byte[length];
        var theirStates = new ByteState[length];
        foreach (Document other in others)
        {
            if (other.IsDisposed)
            {
                continue;
            }

            int got = other.Current.ReadForDisplay(start, theirs, theirStates);
            for (int i = 0; i < read; i++)
            {
                // 相手のドキュメントが短い場合、末尾より後ろは違うとみなす。読み込み中のバイトは比べない。
                bool known = mineStates[i] == ByteState.Valid && (i >= got || theirStates[i] == ByteState.Valid);
                if (known && (i >= got || mine[i] != theirs[i]))
                {
                    differ[i] = true;
                }
            }
        }

        Brush brush = view.DifferenceBrush;
        IReadOnlyList<double> dash = DifferenceDash;
        for (int i = 0; i < read; i++)
        {
            if (!differ[i])
            {
                continue;
            }

            int last = i;
            while (last + 1 < read && differ[last + 1])
            {
                last++;
            }

            // 層 11 の背景と、差分の種類 (変更) の模様 (破線の枠)。
            yield return new HexHighlight(start + i, last - i + 1, CellLayer.Difference, view.IsHighContrast ? null : brush, brush, dash, "difference");
            i = last;
        }
    }

    private static readonly IReadOnlyList<double> DifferenceDash = [2, 2];
}
