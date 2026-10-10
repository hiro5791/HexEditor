using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 特別な開き方: 範囲を指定して開く (ENG-13)、エンコード形式をデコードして開く (ENG-38)、インポートした内容を新しいドキュメントにする (TOOL-04)、
/// 選択範囲を新しいタブで開く (ENG-39)。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// ファイルの範囲を開く (ENG-13)。オフセット 0 は開始位置、ベースアドレスは開始位置。既定は長さ固定 (上書きのみ)。
    /// </summary>
    public DocumentViewModel OpenRange(string path, long start, long length, bool resizable, bool readOnly = false)
    {
        string full = Path.GetFullPath(path);
        FileByteSource source = FileByteSource.OpenRange(full, start, length, resizable);
        var doc = new Document(source, _options);
        DocumentViewModel vm = Add(doc, full, Path.GetFileName(full), recovery: false);
        vm.RangeLabel = DocumentViewModel.FormatRange(start, source.Length);
        doc.SetReadOnly(readOnly ? ReadOnlyReason.OpenedReadOnly
            : FileWriteProbe.Probe(full, source.HasReadOnlyAttribute, TestHooks.Volumes ?? Core.Saving.SystemVolumeInfoProvider.Instance, TestHooks.OpenForWrite));
        StartWatching(vm);
        if (_files is not null)
        {
            Recent.Record(full, vm.DisplayName, _files.UtcNow());
        }

        return vm;
    }

    /// <summary>同じファイルを開いているタブ (範囲を含む。ENG-13 の仕様 6 の重なりの確認)。</summary>
    public IEnumerable<DocumentViewModel> TabsOfFile(string path)
    {
        string full = Path.GetFullPath(path);
        return Documents.Where(d => string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase) && d.Encoded is null);
    }

    /// <summary>
    /// デコードした内容のタブを加える (ENG-38)。保存先は元のテキストのファイル (元の形式で保存する。TOOL-11)。
    /// </summary>
    public DocumentViewModel AddDecoded(ImportResult result, string path, string format)
    {
        string full = Path.GetFullPath(path);
        EncodedFileSettings settings = result.Settings ?? new EncodedFileSettings { Format = format };
        long baseAddress = result.BaseAddress;
        IReadOnlyList<ImportIssue> issues = result.Issues.Items;
        Document doc = EncodedFile.CreateDocument(result, format, _options);
        DocumentViewModel vm = Add(doc, full, Path.GetFileName(full), recovery: false);
        vm.Encoded = settings;
        vm.EncodedBaseAddress = baseAddress;
        vm.FormatIssues = issues;
        if (_files is not null)
        {
            Recent.Record(full, vm.DisplayName, _files.UtcNow());
        }

        return vm;
    }

    /// <summary>インポートした内容を新しい (無題の) ドキュメントにする (TOOL-04 の仕様 2 の 4)。長さを変えられる。</summary>
    public DocumentViewModel AddImported(SparseImage image, string name)
    {
        // 無題のドキュメントに、デコードした内容 (一時ファイル) を参照のピースとして入れる (復旧用データにも書き出せる)。
        var doc = new Document(MemoryByteSource.CreateEmpty(name), _options);
        doc.InsertContent(0, EditContent.FromSource(image, 0, image.Length, owns: true), Loc.Get("Import_Title"));
        doc.ClearHistory();
        return Add(doc, null, name);
    }

    /// <summary>
    /// 選択範囲を連動ビューで開く (ENG-39 の仕様 1)。タブの名前は <c>data.bin [0x1000–0x1FFF]</c> (ブックマークから開く場合はブックマークの名前)。
    /// </summary>
    public DocumentViewModel OpenLinkedView(DocumentViewModel parent, long offset, long length, string? name = null)
    {
        DocumentViewModel root = parent.LinkParent ?? parent;
        Document child = parent.Document.CreateLinkedView(offset, length);
        long start = child.LinkStart;
        var vm = new DocumentViewModel(child, null, name ?? root.DisplayName)
        {
            Notifications = Notifications,
            LinkParent = root,
            RangeLabel = DocumentViewModel.FormatRange(start, length),
        };
        return AddViewModel(vm, Documents.IndexOf(parent) + 1);
    }

    /// <summary>選択範囲をコピーとして開く (ENG-39 の仕様 2)。データをコピーせず、ピースの参照で作る。</summary>
    public DocumentViewModel OpenAsCopy(DocumentViewModel parent, long offset, long length, string? name = null)
    {
        string title = name ?? Loc.Format("Untitled_Name", ++_app.UntitledCount);
        Document copy = Document.CreateCopy(parent.Document.Current, offset, length, title, _options);
        return Add(copy, null, title);
    }

    /// <summary>連動ビューのタブ (親を閉じるときに一緒に閉じる。ENG-39 の仕様 1)。</summary>
    public static IEnumerable<DocumentViewModel> LinkedTabsOf(DocumentViewModel parent) =>
        WindowManager.Windows.SelectMany(w => w.Vm.Documents).Where(d => d.LinkParent == parent).ToList();
}
