using HexEditor.Core.Engine;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Raid;

namespace HexEditor.App.ViewModels;

/// <summary>ディスクイメージ (分割 raw・E01・VHD・VHDX・VMDK) と RAID の論理ディスクのタブ (12 の FOR-21、FOR-26)。</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// イメージをディスクとして開いたタブ (FOR-21 の仕様 3・4)。書き戻す先がないため読み取り専用 (E01 などは常に読み取り専用。分割 raw も
    /// 読み取り専用で開く)。最近使ったファイルには元のパスで記録し、開き直すと形式を判定して同じように開く。
    /// </summary>
    public DocumentViewModel OpenImageSource(IImageSource source, string path)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, CurrentOptions);
        DocumentViewModel vm = Add(doc, null, source.DisplayName, recovery: false);
        doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        if (_files is not null)
        {
            Recent.Record(Path.GetFullPath(path), source.DisplayName, _files.UtcNow(), Core.Files.RecentItemKind.File);
        }

        return vm;
    }

    /// <summary>RAID の論理ディスクのタブ (FOR-26 の仕様 4。読み取り専用)。</summary>
    public DocumentViewModel OpenRaidSource(RaidByteSource source)
    {
        var doc = new Document(source, CurrentOptions);
        DocumentViewModel vm = Add(doc, null, source.DisplayName, recovery: false);
        doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        return vm;
    }
}
