using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// 画面分割 (VIEW-37) と同じドキュメントの複数ビュー (VIEW-38)。分割の状態はタブ (DocumentViewModel) が持ち、タブの中の PaneHost が
/// ペインの Hex ビューを並べる。操作中のペイン (フォーカスのあるペイン) がメニュー・ショートカット・ステータスバーの対象になる。
/// </summary>
public sealed partial class MainWindow
{
    private readonly HashSet<DocumentViewModel> _paneHooks = [];

    /// <summary>Hex ビューの入っている PaneHost。</summary>
    private static PaneHost? HostOf(HexView view) => VisualTreeHelper.GetParent(view) as PaneHost;

    /// <summary>選択中のタブの PaneHost。</summary>
    private PaneHost? SelectedHost() => _views.Where(v => Vm.Selected is { } d && d.Panes.Contains(v.Editor!)).Select(HostOf).FirstOrDefault(h => h is not null);

    /// <summary>タブのペインの変化を受ける (Hex ビューが読み込まれたときに 1 回つなぐ)。</summary>
    private void HookPanes(HexView view)
    {
        if (DocumentOfView(view) is not { } doc)
        {
            return;
        }

        if (_paneHooks.Add(doc))
        {
            doc.PanesChanged += (_, _) =>
            {
                if (Vm.Documents.Contains(doc))
                {
                    RefreshPanes(doc);
                }
            };
            doc.ActivePaneChanged += (_, _) =>
            {
                if (!Vm.Documents.Contains(doc))
                {
                    return;
                }

                PaneHostOf(doc)?.ShowActive(doc.ActivePane);
                if (doc == Vm.Selected)
                {
                    AfterActiveEditorChanged();
                }
            };
        }

        // 分割したペインの Hex ビューでは、フォーカスを受けたペインを操作中にする (VIEW-37 の仕様 6)。
        view.GotFocus -= PaneView_GotFocus;
        view.GotFocus += PaneView_GotFocus;
        if (HostOf(view) is { } host)
        {
            host.RatioChanged -= Host_RatioChanged;
            host.RatioChanged += Host_RatioChanged;
            if (doc.IsSplit && host.Second is null)
            {
                RefreshPanes(doc);
            }
        }
    }

    private void Host_RatioChanged(object? sender, double ratio)
    {
        if (sender is PaneHost { First: { } first } && DocumentOfView(first) is { } doc)
        {
            doc.SplitRatio = ratio;
        }
    }

    private void PaneView_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is HexView { Editor: { } editor } view && DocumentOfView(view) is { IsSplit: true } doc)
        {
            doc.ActivePane = ReferenceEquals(editor, doc.SecondaryEditor) ? 1 : 0;
        }
    }

    private PaneHost? PaneHostOf(DocumentViewModel doc) =>
        _views.Where(v => v.Editor is { } e && doc.Panes.Contains(e) && v.IsLoaded).Select(HostOf).FirstOrDefault(h => h is not null);

    /// <summary>タブの中の Hex ビューを、分割の状態に合わせる。</summary>
    private void RefreshPanes(DocumentViewModel doc)
    {
        if (PaneHostOf(doc) is not { } host)
        {
            return;
        }

        host.Update(doc.PrimaryEditor, doc.SecondaryEditor, doc.SplitSideBySide, doc.SplitRatio, doc.ActivePane, CreatePaneView);
        if (doc == Vm.Selected)
        {
            AfterActiveEditorChanged();
        }
    }

    /// <summary>2 つ目のペインの Hex ビューを作る (XAML の 1 つ目と同じイベントをつなぐ)。</summary>
    private HexView CreatePaneView()
    {
        var view = new HexView();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(view, "HexView2");
        view.CommandRequested += HexView_CommandRequested;
        view.EditRejected += HexView_EditRejected;
        view.Loaded += HexView_Loaded;
        return view;
    }

    /// <summary>操作中のビューが替わった: メニュー・ステータスバー・パネルの対象を替える。</summary>
    private void AfterActiveEditorChanged()
    {
        if (FindBar.IsOpen && Editor is { } editor)
        {
            FindBar.Editor = editor;
            UpdateMatchHighlights();
        }

        UpdateViewMenu();
        UpdateCommandStates();
        QueueStatusBarLayout();
        QueueInspectorRefresh();
        UpdateSyncStatus();
    }

    // ---- コマンド ----

    /// <summary>分割できるか (2 ペインの最小の大きさを確保できなければ無効。VIEW-37 の「エラー」)。</summary>
    private CommandState SplitState()
    {
        if (Vm.Selected is null)
        {
            return NeedsDocument();
        }

        if (SelectedView() is { ActualHeight: > 0 } view && view.ActualHeight < 2 * (5 * view.RowHeight) + PaneHost.SplitterSize
            && view.ActualWidth < 2 * 120 + PaneHost.SplitterSize)
        {
            return CommandState.Unavailable(Loc.Get("Command_SplitTooSmall"));
        }

        return CommandState.Available;
    }

    private CommandState SplitToggleState() => Vm.Selected is { IsSplit: true } ? CommandState.Available : SplitState();

    /// <summary>Ctrl+\ (VIEW-37 の仕様 1): 分割していなければ前回の向き (既定は上下) で分割し、分割していれば解除する。</summary>
    private void ToggleSplit()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        if (doc.IsSplit)
        {
            UnsplitSelected();
        }
        else
        {
            SplitSelected(doc.LastSplitSideBySide ? Orientation.Horizontal : Orientation.Vertical);
        }
    }

    /// <summary>分割する。<paramref name="orientation"/> が Vertical なら上下、Horizontal なら左右。</summary>
    private void SplitSelected(Orientation orientation)
    {
        if (Vm.Selected is not { } doc || !SplitState().Enabled)
        {
            return;
        }

        doc.Split(orientation == Orientation.Horizontal);
        ApplyEditorSettings();
        RefreshPanes(doc);
        HookPaneSettings(doc);
    }

    private void UnsplitSelected()
    {
        if (Vm.Selected is { IsSplit: true } doc)
        {
            doc.Unsplit();
            RefreshPanes(doc);
            FocusEditor();
        }
    }

    private void ToggleSplitSync()
    {
        if (Vm.Selected is { IsSplit: true } doc)
        {
            doc.SetPaneSync(!doc.PaneSyncEnabled);
            RefreshCommandUi();
            QueueStatusBarLayout();
        }
    }

    /// <summary>「表示: 次のペインへ」(VIEW-37 の仕様 7)。</summary>
    private void FocusNextPane()
    {
        if (Vm.Selected is { IsSplit: true } doc)
        {
            FocusPane(doc.ActivePane == 0 ? 1 : 0);
        }
    }

    /// <summary>ペインの Hex ビューにフォーカスを置く (分割していなければ 1 つ目)。</summary>
    private void FocusPane(int pane)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState target = pane == 1 && doc.SecondaryEditor is { } second ? second : doc.PrimaryEditor;
        if (_views.FirstOrDefault(v => ReferenceEquals(v.Editor, target)) is { } view)
        {
            view.Focus(FocusState.Keyboard);
            doc.ActivePane = pane == 1 && doc.IsSplit ? 1 : 0;
        }
    }

    /// <summary>
    /// 設定「分割したペインの表示設定をそろえる」(VIEW-37 の仕様 3。既定オフ): 一方の表示設定の変更をもう一方にも反映する。
    /// </summary>
    private void HookPaneSettings(DocumentViewModel doc)
    {
        if (doc.SecondaryEditor is not { } second)
        {
            return;
        }

        EditorState first = doc.PrimaryEditor;
        bool applying = false;
        void Mirror(EditorState from, EditorState to)
        {
            if (applying || !App.Settings.GetBool("view.split.syncSettings", false) || !doc.Panes.Contains(to) || !doc.Panes.Contains(from))
            {
                return;
            }

            applying = true;
            try
            {
                to.ApplyView(from.View);
            }
            finally
            {
                applying = false;
            }
        }

        first.ViewChanged += (_, _) => Mirror(first, second);
        second.ViewChanged += (_, _) => Mirror(second, first);
    }

    // ---- 新しいビュー (VIEW-38) ----

    private CommandState NewViewState() => Vm.Selected is not { } doc ? NeedsDocument()
        : doc.Share.Views.Count >= DocumentViewModel.MaxViews ? CommandState.Unavailable(Loc.Format("Command_ViewLimit", DocumentViewModel.MaxViews))
        : CommandState.Available;

    /// <summary>「新しいビューで開く」(VIEW-38 の仕様 1): 同じドキュメントを表示するタブを、今のタブの右に作る。</summary>
    private void OpenNewView()
    {
        if (Vm.Selected is not { } doc || doc.CreateView() is not { } view)
        {
            return;
        }

        int index = Vm.Documents.IndexOf(doc) + 1;
        view.Owner = Vm;
        Vm.Documents.Insert(index, view);
        Vm.Selected = view;
        RefreshCommandUi();
    }

    /// <summary>タブの選択に合わせて、選ばれていないタブのビューを、ほかのビューの編集に追従させる (VIEW-38 の仕様 5)。</summary>
    private void UpdateSelectedViews()
    {
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            doc.IsSelectedView = doc == Vm.Selected;
        }
    }
}
