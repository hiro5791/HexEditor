using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>メインウィンドウの状態: 開いているドキュメントと、ファイル操作 (ENG-10、ENG-11、ENG-20〜ENG-22)。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private int _untitledCount;

    public MainViewModel(OperationCenter operations, EngineMemory memory)
    {
        Operations = operations;
        Memory = memory;
    }

    public OperationCenter Operations { get; }

    public EngineMemory Memory { get; }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    public partial DocumentViewModel? Selected { get; set; }

    /// <summary>新規作成 (ENG-10)。長さ 0 の「無題 N」を開く。</summary>
    public DocumentViewModel NewDocument()
    {
        string name = Loc.Format("Untitled_Name", ++_untitledCount);
        var doc = new Document(MemoryByteSource.CreateEmpty(name));
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

        var doc = new Document(FileByteSource.Open(full));
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
    }

    /// <summary>その場保存のジャーナルの置き場所 (ENG-23。復旧用フォルダ)。</summary>
    private static string JournalDirectory => Path.Combine(Path.GetTempPath(), "HexEditor", "recovery");

    public void Close(DocumentViewModel vm)
    {
        Documents.Remove(vm);
        Memory.Unregister(vm.Document);
        vm.Dispose();
        if (Selected == vm)
        {
            Selected = Documents.LastOrDefault();
        }
    }

    private DocumentViewModel Add(Document doc, string? path, string name)
    {
        var vm = new DocumentViewModel(doc, path, name);
        Memory.Register(doc);
        Documents.Add(vm);
        Selected = vm;
        return vm;
    }
}
