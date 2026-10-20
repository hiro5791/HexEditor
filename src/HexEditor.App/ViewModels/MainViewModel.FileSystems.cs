using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>ファイルシステムのファイル・未割り当て領域のタブ (FOR-09 の仕様 6、FOR-16 の仕様 5)。</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// ファイルシステムの中身を読み取り専用のタブで開く。書き戻す先がないので保存はできず (名前を付けて保存はできる)、復旧用データにも記録しない。
    /// 同じ内容のタブがあればそれを選ぶ。
    /// </summary>
    public DocumentViewModel AddFileSystemContent(IByteSource source, string name)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, CurrentOptions);
        doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        return Add(doc, null, name, recovery: false);
    }
}
