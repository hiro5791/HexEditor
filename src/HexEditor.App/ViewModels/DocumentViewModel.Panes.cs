using HexEditor.Core.Engine;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 画面分割 (VIEW-37) と同じドキュメントの複数ビュー (VIEW-38)。タブ 1 つは最大 2 つのペイン (<see cref="PrimaryEditor"/>・
/// <see cref="SecondaryEditor"/>) を持ち、操作中のペインのビューが <see cref="Editor"/>。同じドキュメントの別のタブ (新しいビュー) は
/// <see cref="Share"/> を共有し、最後のビューを閉じるときだけドキュメントを閉じる。
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>1 ドキュメントのビュー (タブ) の数の上限 (VIEW-38 の仕様 2)。</summary>
    public const int MaxViews = 16;

    /// <summary>1 つ目のペインのビュー (分割していないときの唯一のビュー)。</summary>
    public EditorState PrimaryEditor { get; private set; }

    /// <summary>分割したもう一方のペインのビュー。分割していなければ null。</summary>
    public EditorState? SecondaryEditor { get; private set; }

    /// <summary>ペインのビュー (分割していなければ 1 つ)。</summary>
    public IReadOnlyList<EditorState> Panes => SecondaryEditor is { } second ? [PrimaryEditor, second] : [PrimaryEditor];

    public bool IsSplit => SecondaryEditor is not null;

    /// <summary>左右に分割しているか (false なら上下)。</summary>
    public bool SplitSideBySide { get; private set; }

    /// <summary>前回分割した向き (Ctrl+\ で分割するときに使う。既定は上下。VIEW-37 の仕様 1)。</summary>
    public bool LastSplitSideBySide { get; set; }

    /// <summary>分割の比率 (1 つ目のペインの割合。0〜1。分割バーのダブルクリックで 0.5)。</summary>
    public double SplitRatio { get; set; } = 0.5;

    /// <summary>操作中のペイン (0 か 1。VIEW-37 の仕様 6)。</summary>
    public int ActivePane
    {
        get => _activePane;
        set
        {
            int pane = SecondaryEditor is null ? 0 : Math.Clamp(value, 0, 1);
            if (pane == _activePane)
            {
                return;
            }

            _activePane = pane;
            UpdateFollowers();
            ActivePaneChanged?.Invoke(this, EventArgs.Empty);
            EditorChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(string.Empty);
        }
    }

    private int _activePane;

    /// <summary>操作中のペインが替わった (メニュー・パネルの対象を替える)。</summary>
    public event EventHandler? ActivePaneChanged;

    /// <summary>分割・分割の解除をした (タブの中の Hex ビューを作り直す)。</summary>
    public event EventHandler? PanesChanged;

    /// <summary>分割したペインのスクロールの同期 (VIEW-37 の仕様 8)。</summary>
    public ViewSync? PaneSync { get; private set; }

    public bool PaneSyncEnabled => PaneSync is { Mode: SyncMode.ScrollRows };

    /// <summary>
    /// 分割する (VIEW-37 の仕様 1〜3)。新しいペインは今のペインの表示設定・位置を複製する。すでに分割していれば向きだけ変える。
    /// </summary>
    public EditorState Split(bool sideBySide)
    {
        SplitSideBySide = sideBySide;
        LastSplitSideBySide = sideBySide;
        if (SecondaryEditor is { } existing)
        {
            PanesChanged?.Invoke(this, EventArgs.Empty);
            return existing;
        }

        EditorState source = Editor;
        var second = new EditorState(Document, source.BytesPerRow)
        {
            Options = source.Options,
            NibbleArrowKeys = source.NibbleArrowKeys,
            CursorMargin = source.CursorMargin,
            JumpPlacement = source.JumpPlacement,
            KeepChangesAfterSave = source.KeepChangesAfterSave,
            VisibleRows = source.VisibleRows,
        };
        second.ApplyView(source.View);
        second.SyncTo(source.TopOffset, source.Cursor);
        SecondaryEditor = second;
        AttachPane(second);
        SplitRatio = 0.5;
        UpdateFollowers();
        PanesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(string.Empty);
        return second;
    }

    /// <summary>分割を解除する。操作中のペインを残す (VIEW-37 の仕様 9)。</summary>
    public void Unsplit()
    {
        if (SecondaryEditor is not { } second)
        {
            return;
        }

        SetPaneSync(false);
        if (_activePane == 1)
        {
            // 操作中の 2 つ目のペインを残す: 1 つ目にする。
            DetachPane(PrimaryEditor);
            PrimaryEditor = second;
        }
        else
        {
            DetachPane(second);
        }

        SecondaryEditor = null;
        _activePane = 0;
        UpdateFollowers();
        PanesChanged?.Invoke(this, EventArgs.Empty);
        ActivePaneChanged?.Invoke(this, EventArgs.Empty);
        EditorChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>「スクロールを同期」(VIEW-37 の仕様 8)。オンにした時点の 2 つのペインの位置の差を保つ。</summary>
    public void SetPaneSync(bool on)
    {
        PaneSync?.Dispose();
        PaneSync = on && SecondaryEditor is { } second ? new ViewSync([PrimaryEditor, second], SyncMode.ScrollRows) : null;
        OnPropertyChanged(nameof(PaneSyncEnabled));
    }

    /// <summary>操作中でないペイン・ビューは、ほかのビューの編集に表示を追従させる (VIEW-37 の仕様 4、VIEW-38 の仕様 5)。</summary>
    private void UpdateFollowers()
    {
        foreach (EditorState pane in Panes)
        {
            pane.FollowsEdits = !ReferenceEquals(pane, Editor) || !IsSelectedView;
        }
    }

    // ---- 同じドキュメントの複数ビュー (VIEW-38) ----

    /// <summary>同じドキュメントのビュー (タブ) の一覧。ウィンドウをまたいで共有する。</summary>
    public sealed class DocumentShare
    {
        public List<DocumentViewModel> Views { get; } = [];

        /// <summary>次のビューの番号 (タブの見出しの「: 2」)。</summary>
        public int NextNumber { get; set; } = 1;
    }

    /// <summary>このドキュメントのビューの一覧 (最初のビューを作ったときに作る)。</summary>
    public DocumentShare Share
    {
        get
        {
            if (_share is null)
            {
                _share = new DocumentShare();
                _share.Views.Add(this);
                ViewNumber = _share.NextNumber++;
            }

            return _share;
        }
    }

    private DocumentShare? _share;

    /// <summary>ビューの番号 (1 から。VIEW-38 の仕様 1)。</summary>
    public int ViewNumber { get; private set; } = 1;

    /// <summary>同じドキュメントのビューがほかにもあるか。</summary>
    public bool HasOtherViews => _share is { Views.Count: > 1 };

    /// <summary><paramref name="closing"/> を閉じても、同じドキュメントのビューが残るか (未保存の確認を出さない。VIEW-38 の仕様 6)。</summary>
    public bool KeepsDocumentAfterClosing(IEnumerable<DocumentViewModel> closing) =>
        _share is { } share && share.Views.Any(v => v != this && !closing.Contains(v));

    /// <summary>
    /// このタブが選ばれている (操作中のビュー) か。選ばれていないタブのビューは、ほかのビューの編集に表示を追従させる。ウィンドウが設定する。
    /// </summary>
    public bool IsSelectedView
    {
        get => _isSelectedView;
        set
        {
            if (_isSelectedView != value)
            {
                _isSelectedView = value;
                UpdateFollowers();
            }
        }
    }

    private bool _isSelectedView = true;

    /// <summary>
    /// 同じドキュメントの新しいビューを作る (VIEW-38 の仕様 1・3): ドキュメントとデータエンジンを共有し、カーソル・選択範囲・スクロール位置・
    /// 表示設定は別に持つ。上限 (16) に達していれば null。
    /// </summary>
    public DocumentViewModel? CreateView()
    {
        DocumentShare share = Share;
        if (share.Views.Count >= MaxViews)
        {
            return null;
        }

        var view = new DocumentViewModel(Document, FilePath, DisplayName) { Notifications = Notifications };
        view._share = share;
        view.ViewNumber = share.NextNumber++;
        share.Views.Add(view);
        view.PrimaryEditor.Options = Editor.Options;
        view.PrimaryEditor.VisibleRows = Editor.VisibleRows;
        view.PrimaryEditor.ApplyView(Editor.View);
        view.PrimaryEditor.SyncTo(Editor.TopOffset, Editor.Cursor);
        foreach (DocumentViewModel v in share.Views)
        {
            v.OnPropertyChanged(nameof(TabTitle));
            v.OnPropertyChanged(nameof(Header));
        }

        return view;
    }

    /// <summary>ビューの一覧から外す。ほかのビューが残れば true (ドキュメントは閉じない)。</summary>
    private bool LeaveShare()
    {
        if (_share is not { } share || !share.Views.Remove(this) || share.Views.Count == 0)
        {
            return false;
        }

        // 復旧用データ・一時ファイルは残るビューに引き継ぐ。
        DocumentViewModel heir = share.Views[0];
        heir.Recovery ??= Recovery;
        heir.TemporaryFile ??= TemporaryFile;
        foreach (DocumentViewModel v in share.Views)
        {
            v.OnPropertyChanged(nameof(TabTitle));
            v.OnPropertyChanged(nameof(Header));
        }

        return true;
    }

    /// <summary>タブの見出しの番号 (ビューが 2 つ以上のときだけ「 : 2」)。</summary>
    private string ViewSuffix => _share is { Views.Count: > 1 } ? " : " + ViewNumber : string.Empty;
}
