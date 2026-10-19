using System.Text;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.TestData;

/// <summary>パーティション・ファイルシステムのテスト用イメージ (cases/12-forensics.md の TD-FOR-*。FOR-06〜FOR-17)。</summary>
public static partial class TestDataCatalog
{
    /// <summary>TD-TEXT-ASCII の内容。</summary>
    public static byte[] TextAscii4K() => Text(AsciiLines, Encoding.ASCII, 4 * KiB, bom: false);

    /// <summary>TD-TEXT-UTF8 の内容。</summary>
    public static byte[] TextUtf8_4K() => Text(Utf8Lines, new UTF8Encoding(false), 4 * KiB, bom: false);

    private static IEnumerable<TestDataItem> FileSystemItems()
    {
        foreach (string id in FileSystemImageSet.Ids)
        {
            yield return new TestDataItem(id, FileSystemImageSet.LengthOf(id), $"{id} (FOR-06〜FOR-17。スパース)",
                path => FileSystemImageSet.Image(id).Save(path));
        }

        yield return new TestDataItem("TD-FOR-NTFS-1M", FileSystemImageSet.LengthOf("TD-FOR-NTFS-1M"),
            "NTFS: フォルダ many に長さ 0 のファイル 100 万個 (4 GiB のスパース。生成に約 1.1 GiB のディスクを使う)",
            path => FileSystemImageSet.WriteNtfs1M(path));
    }
}
