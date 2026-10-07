using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Recovery;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>メインウィンドウの状態: 開いているドキュメントと、ファイル操作 (ENG-10、ENG-11、ENG-20〜ENG-22)。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private int _untitledCount;

    private readonly DocumentOptions _options;
    private readonly string _journalDirectory;

    public MainViewModel(OperationCenter operations, EngineMemory memory, DocumentOptions options, string journalDirectory)
    {
        Operations = operations;
        Memory = memory;
        _options = options;
        _journalDirectory = journalDirectory;
    }

    public OperationCenter Operations { get; }

    public EngineMemory Memory { get; }

    /// <summary>通知 (UI-36)。</summary>
    public Core.Notifications.NotificationCenter Notifications { get; } = new();

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    public partial DocumentViewModel? Selected { get; set; }

    /// <summary>新規作成 (ENG-10)。長さ 0 の「無題 N」を開く。</summary>
    public DocumentViewModel NewDocument()
    {
        string name = Loc.Format("Untitled_Name", ++_untitledCount);
        var doc = new Document(MemoryByteSource.CreateEmpty(name), _options);
        return Add(doc, null, name);
    }

    /// <summary>ファイルを開く (ENG-11)。同じファイルが開いていればそのタブを選ぶ。</summary>
    public DocumentViewModel Open(string path)
    {
        string full = Path.GetFullPath(path);
        DocumentViewModel? existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Selected = existing;
            return existing;
        }

        var doc = new Document(FileByteSource.Open(full), _options);
        return Add(doc, full, Path.GetFileName(full));
    }

    /// <summary>
    /// 保存 (ENG-20〜ENG-23)。長さが変わらず元のファイルに保存する場合は変更箇所だけを書き込むその場保存、それ以外は
    /// 一時ファイルと置き換える安全な保存を選ぶ。長時間処理として実行し、保存中は編集を受け付けない。完了後は UI スレッドで
    /// 保存したファイルを新しい元データにする。
    /// </summary>
    public async Task SaveAsync(DocumentViewModel vm, string path)
    {
        Document doc = vm.Document;
        DocumentSnapshot snapshot = doc.Current;
        string name = Loc.Format("Operation_Save", Path.GetFileName(path));
        if (InPlaceSaver.CanSaveInPlace(snapshot, path))
        {
            try
            {
                InPlaceSaveResult result = await Operations.RunAsync(
                    name, OperationKind.WritesExternal, doc, null,
                    op => Task.FromResult(InPlaceSaver.Save(snapshot, JournalDirectory, InPlaceSaver.DefaultJournalLimit, op)),
                    locked => doc.SetEditLock(locked));
                doc.CompleteInPlaceSave(result);
                vm.SetSavedPath(path);
                vm.OnSaved();
                return;
            }
            catch (JournalLimitException)
            {
                // 変更量がジャーナルの上限を超える場合は、安全な保存に切り替える (ENG-23 の仕様 3)。
            }
        }

        FileByteSource saved = await Operations.RunAsync(
            name,
            OperationKind.WritesExternal,
            doc,
            snapshot.Length,
            op => Task.FromResult(DocumentSaver.Save(snapshot, path, op)),
            locked => doc.SetEditLock(locked));
        doc.CompleteSave(saved);
        vm.SetSavedPath(saved.Path);
        vm.OnSaved();
    }

    /// <summary>その場保存のジャーナルの置き場所 (ENG-23。復旧用フォルダ。PKG-13)。</summary>
    private string JournalDirectory => _journalDirectory;

    public void Close(DocumentViewModel vm)
    {
        Documents.Remove(vm);
        Notifications.DismissOwnedBy(vm);
        Memory.Unregister(vm.Document);
        vm.Dispose();
        if (Selected == vm)
        {
            Selected = Documents.LastOrDefault();
        }
    }

    /// <summary>ドキュメントを作るときの設定。</summary>
    public DocumentOptions DocumentOptions => _options;

    /// <summary>復旧用データのフォルダ (ドキュメントの一時ファイルと同じ。ENG-27 の仕様 2)。</summary>
    public string RecoveryRoot => _options.TempDirectory;

    /// <summary>復旧用データから開く (ENG-27 の仕様 6)。元のファイルが変わっていた場合は読み取り専用にする。</summary>
    public DocumentViewModel AddRestored(RestoredDocument restored)
    {
        RecoveryRecord record = restored.Record;
        string name = record.Path is null ? record.DisplayName : Path.GetFileName(record.Path);
        var vm = new DocumentViewModel(restored.Document, record.Path, name) { Recovery = restored.Recovery, Notifications = Notifications };
        vm.Editor.ReadOnly = restored.SourceChanged;
        long length = restored.Document.Length;
        if (record.SelectionLength > 0 && record.SelectionStart + record.SelectionLength <= length)
        {
            vm.Editor.Select(record.SelectionStart, record.SelectionLength);
        }
        else
        {
            vm.Editor.GoTo(Math.Clamp(record.Cursor, 0, length));
        }

        return AddViewModel(vm);
    }

    /// <summary>
    /// 復旧用データの定期の書き出し (ENG-27 の仕様 1、3)。内容を UI スレッドで取り、書き込みはバックグラウンドで行う。
    /// 失敗したドキュメントがあれば <paramref name="onError"/> を呼ぶ。
    /// </summary>
    public async Task WriteRecoveryAsync(Action<Exception> onError)
    {
        var work = Documents.Select(vm => (vm, capture: vm.CaptureRecoveryIfChanged()))
            .Where(w => w.capture is not null)
            .ToList();
        foreach ((DocumentViewModel vm, RecoveryCapture? capture) in work)
        {
            try
            {
                await Task.Run(() => vm.Recovery!.Write(capture!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                vm.ForgetRecorded();
                onError(ex);
            }
        }
    }

    /// <summary>
    /// 異常終了の直前に、全ドキュメントの復旧用データを書き出す (PKG-30 の仕様 1 の 1)。<paramref name="timeout"/> で打ち切る。
    /// </summary>
    public void WriteRecoveryNow(TimeSpan timeout)
    {
        var captures = new List<(DocumentViewModel Vm, RecoveryCapture Capture)>();
        foreach (DocumentViewModel vm in Documents.ToList())
        {
            try
            {
                if (vm.CaptureRecoveryIfChanged() is { } capture)
                {
                    captures.Add((vm, capture));
                }
            }
            catch (Exception)
            {
                // 異常終了の途中なので、書けるものだけ書く。
            }
        }

        Task all = Task.Run(() =>
        {
            foreach ((DocumentViewModel vm, RecoveryCapture capture) in captures)
            {
                try
                {
                    vm.Recovery!.Write(capture);
                }
                catch (Exception)
                {
                }
            }
        });
        all.Wait(timeout);
    }

    private DocumentViewModel Add(Document doc, string? path, string name)
    {
        DocumentRecovery? recovery = null;
        try
        {
            recovery = new DocumentRecovery(RecoveryRoot, doc.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 復旧用データを作れなくても編集はできる。書き出しのときに通知する。
            AppLog.Warning($"Recovery folder unavailable: {ex.Message}");
        }

        return AddViewModel(new DocumentViewModel(doc, path, name) { Recovery = recovery, Notifications = Notifications });
    }

    private DocumentViewModel AddViewModel(DocumentViewModel vm)
    {
        Document doc = vm.Document;
        Memory.Register(doc);
        Documents.Add(vm);
        Selected = vm;
        return vm;
    }
}
