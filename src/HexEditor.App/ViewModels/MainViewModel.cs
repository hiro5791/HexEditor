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
    /// 保存 (ENG-20〜ENG-22)。長時間処理として実行し、保存中は編集を受け付けない。完了後は UI スレッドで
    /// 保存したファイルを新しい元データにする。
    /// </summary>
    public async Task SaveAsync(DocumentViewModel vm, string path)
    {
        Document doc = vm.Document;
        DocumentSnapshot snapshot = doc.Current;
        string name = Loc.Format("Operation_Save", Path.GetFileName(path));
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
