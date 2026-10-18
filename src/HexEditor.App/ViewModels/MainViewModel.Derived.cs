using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>派生ドキュメント (EDIT-46) のタブを作る。</summary>
public sealed partial class MainViewModel
{
    /// <summary>派生ドキュメントの内容を入れる空のドキュメント (結果の一時ファイルはこのドキュメントの一時フォルダに作る)。</summary>
    public Document CreateDerivedDocument(string name) => new(MemoryByteSource.CreateEmpty(name), CurrentOptions);

    /// <summary>元のタブ <paramref name="parent"/> を閉じる: 派生ドキュメントを通常の新規ドキュメントにし、InfoBar で知らせる (EDIT-46 の仕様 7)。</summary>
    private static void DetachDerivedTabsOf(DocumentViewModel parent)
    {
        foreach (DocumentViewModel child in WindowManager.Windows.SelectMany(w => w.Vm.Documents).Where(d => d.DerivedParent == parent).ToList())
        {
            child.DetachDerived();
            child.DerivedParent = null;
            child.Notifications?.Show(Core.Notifications.NotificationScope.Document, Core.Notifications.NotificationSeverity.Warning,
                Services.Loc.Get("Derived_ParentClosed"), child, null, null);
        }
    }

    /// <summary>派生ドキュメントのタブを、元のタブの直後に加える (無題のドキュメント。保存は「名前を付けて保存」)。</summary>
    public DocumentViewModel AddDerived(Document doc, string name, DocumentViewModel parent)
    {
        int at = Documents.IndexOf(parent);
        return Add(doc, null, name, at < 0 ? null : at + 1);
    }
}
