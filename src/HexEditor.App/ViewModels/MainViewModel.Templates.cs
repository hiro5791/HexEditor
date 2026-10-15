using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>テンプレートのセクションを新しいタブで開く (TPL-15 の仕様 5、TPL-24 の「セクションを新しいタブで開く」)。</summary>
public sealed partial class MainViewModel
{
    /// <summary>セクションの内容を読み取り専用のタブで開く (書き戻す先がないので保存の対象にしない)。</summary>
    public DocumentViewModel AddTemplateSection(IByteSource source, string name, DocumentViewModel parent)
    {
        var doc = new Document(source, _options);
        doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        return Add(doc, null, name, Documents.IndexOf(parent) + 1, recovery: false);
    }
}
