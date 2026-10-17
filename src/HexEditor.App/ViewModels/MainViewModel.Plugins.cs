using HexEditor.Core.Engine;
using HexEditor.Core.Plugins;

namespace HexEditor.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>
    /// プラグインのデータソース (08 の AUTO-31 の IByteSourceProvider) をタブで開く。読み取り専用と宣言したものは読み取り専用にする
    /// (書き戻す先がないものと同じ扱い)。長さを変えられるかはデータソースの宣言に従う。
    /// </summary>
    public DocumentViewModel OpenPluginSource(PluginByteSource source)
    {
        var doc = new Document(source, CurrentOptions);
        DocumentViewModel vm = Add(doc, null, source.DisplayName, recovery: false);
        if (!source.Capabilities.HasFlag(Core.Sources.SourceCapabilities.CanWrite))
        {
            doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        }

        return vm;
    }
}
