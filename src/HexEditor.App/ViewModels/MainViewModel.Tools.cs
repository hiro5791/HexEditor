using HexEditor.Core.Engine;

namespace HexEditor.App.ViewModels;

/// <summary>ファイルツールが作ったドキュメントを新しいタブにする (11 の TOOL-14 の結合、TOOL-15 のインターリーブ)。</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 作った (保存先のない) ドキュメントを、無題のタブとして加えて選ぶ。タブの名前は <paramref name="name"/>。保存は「名前を付けて保存」になる。
    /// </summary>
    public DocumentViewModel AddGenerated(Document doc, string name)
    {
        DocumentViewModel vm = Add(doc, null, name);
        Selected = vm;
        return vm;
    }
}
