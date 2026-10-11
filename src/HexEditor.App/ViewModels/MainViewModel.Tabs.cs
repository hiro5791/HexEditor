using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Notifications;
using HexEditor.Core.Sources;
using HexEditor.Core.Tabs;

namespace HexEditor.App.ViewModels;

/// <summary>
/// タブの並び (UI-09、UI-10)、ウィンドウの間のタブの移動 (UI-11)、遅延して開くタブ (UI-31 の仕様 6)。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>並び替え・移動の途中 (TabView が一時的に選択を外す。最近使った順の記録などはこの間の選択の変化を無視する)。</summary>
    public bool IsRearranging { get; private set; }

    /// <summary>ピン留めの規則に収めて挿入する (ピン留めしたタブは左端にまとめる。UI-10 の仕様 2・4)。</summary>
    private void InsertDocument(DocumentViewModel vm, int? insertAt)
    {
        int pinned = PinnedCount;
        int index = insertAt is int requested && requested >= 0 && requested <= Documents.Count ? requested : Documents.Count;
        index = vm.IsPinned ? Math.Min(index, pinned) : Math.Max(index, pinned);
        Documents.Insert(index, vm);
    }

    /// <summary>ピン留めしたタブの数。</summary>
    public int PinnedCount => Documents.TakeWhile(d => d.IsPinned).Count();

    private bool[] PinnedFlags => [.. Documents.Select(d => d.IsPinned)];

    /// <summary>
    /// 同じファイルを開いているタブ (UI-09 の仕様 1)。パスの表記揺れは完全なパスで、ハードリンク・シンボリックリンクは
    /// ファイル ID (ボリュームのシリアル番号とファイルの番号) で同一とみなす。まだ開いていない復元したタブはパスで比べる。
    /// </summary>
    public DocumentViewModel? FindSameFile(string fullPath)
    {
        // 範囲を指定して開いたタブは同じファイルとしない (重なりの確認は開く側で行う。ENG-13 の仕様 6)。
        DocumentViewModel? byPath = Documents.FirstOrDefault(d => !d.IsRangeDocument && (
            string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(d.PendingRecord?.Path, fullPath, StringComparison.OrdinalIgnoreCase)));
        if (byPath is not null)
        {
            return byPath;
        }

        string? id = FileStamp.FromPath(fullPath)?.FileId;
        return string.IsNullOrEmpty(id) ? null : Documents.FirstOrDefault(d => !d.IsRangeDocument && d.OpenedStamp?.FileId == id);
    }

    /// <summary>タブを動かす (UI-10 の仕様 1)。ピン留めの規則に収める。動いたら true。</summary>
    public bool MoveDocument(DocumentViewModel vm, int to)
    {
        int from = Documents.IndexOf(vm);
        if (from < 0)
        {
            return false;
        }

        int target = TabStripRules.ClampMove(PinnedFlags, from, to);
        if (target == from)
        {
            return false;
        }

        Rearrange(() => Documents.Move(from, target));
        return true;
    }

    /// <summary>ピン留めを切り替える (UI-10 の仕様 2)。ピン留めの並びの末尾 / ピン留めしていない並びの先頭に移る。</summary>
    public void SetPinned(DocumentViewModel vm, bool pinned)
    {
        int index = Documents.IndexOf(vm);
        if (index < 0 || vm.IsPinned == pinned)
        {
            return;
        }

        int target = TabStripRules.PinTarget(PinnedFlags, index, pinned);
        vm.IsPinned = pinned;
        if (target != index)
        {
            Rearrange(() => Documents.Move(index, target));
        }
    }

    /// <summary>
    /// 並びを変える。TabView は項目を動かすと選択を一時的に外すため、終わったら選択を戻す。
    /// </summary>
    private void Rearrange(Action change)
    {
        DocumentViewModel? selected = Selected;
        IsRearranging = true;
        try
        {
            change();
        }
        finally
        {
            IsRearranging = false;
        }

        if (selected is not null && Documents.Contains(selected))
        {
            Selected = selected;
        }
    }

    /// <summary>
    /// 別のウィンドウに移すために外す (UI-11)。文書は閉じない (未保存の編集・Undo 履歴・実行中の処理はそのまま。仕様 3)。
    /// </summary>
    public void Detach(DocumentViewModel vm)
    {
        int index = Documents.IndexOf(vm);
        if (index < 0)
        {
            return;
        }

        bool wasSelected = Selected == vm;
        IsRearranging = true;
        try
        {
            Documents.RemoveAt(index);
        }
        finally
        {
            IsRearranging = false;
        }

        if (wasSelected || Selected == vm || Selected is null)
        {
            Selected = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        }
    }

    /// <summary>
    /// 別のウィンドウから移したタブを受け取る (UI-11)。<paramref name="insertAt"/> は挿入する位置 (null なら末尾)。文書の範囲の通知も
    /// このウィンドウに移す。
    /// </summary>
    public void Attach(DocumentViewModel vm, int? insertAt)
    {
        NotificationCenter? from = vm.Notifications;
        vm.Owner = this;
        vm.Notifications = Notifications;
        if (from is not null && from != Notifications)
        {
            foreach (Notification n in from.Open.Where(n => ReferenceEquals(n.Owner, vm)).ToList())
            {
                Notifications.Show(n.Scope, n.Severity, n.Message, vm, n.Undo, n.Actions);
            }

            from.DismissOwnedBy(vm);
        }

        InsertDocument(vm, insertAt);
        Selected = vm;
    }

    /// <summary>
    /// セッションから復元するまだ開いていないタブ (UI-31 の仕様 6)。見出しは保存された名前ですぐに出し、内容は初めて表示したときに開く。
    /// </summary>
    public DocumentViewModel AddPending(SessionTab tab)
    {
        var doc = new Document(MemoryByteSource.CreateEmpty(tab.DisplayName), _options);
        var vm = new DocumentViewModel(doc, null, tab.DisplayName) { PendingRecord = tab, Notifications = Notifications, IsPinned = tab.Pinned };
        vm.Editor.ReadOnly = true;

        // 追加しただけでは表示しない (表示すると開くため)。選択は元に戻す。
        DocumentViewModel? selected = Selected;
        IsRearranging = true;
        try
        {
            AddViewModel(vm);
            Selected = selected;
        }
        finally
        {
            IsRearranging = false;
        }

        return vm;
    }

    /// <summary>遅延して開くタブを外す (呼び出し側が同じ位置に開いた文書を入れる)。記録には残さない。</summary>
    public void RemovePending(DocumentViewModel pending)
    {
        IsRearranging = true;
        try
        {
            Documents.Remove(pending);
        }
        finally
        {
            IsRearranging = false;
        }

        Memory.Unregister(pending.Document);
        pending.Dispose();
    }
}
