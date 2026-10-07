using HexEditor.Core.Saving;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-25 の仕様 2: 空き容量は、ユーザーごとのクォータを考慮した値 (呼び出し元が使える容量) で判断する。</summary>
public sealed class QuotaTests
{
    [Fact(Skip = "クォータを有効にした仮想ディスクとテスト用のローカルユーザーの作成に管理者権限が要る (テストケースの環境: 管理者の CI ランナー)。" +
        "作業中の PC では OS の設定を変えない。判断に使う値が呼び出し元の使える容量であることは下のテストで確かめている")]
    [Trait(TC, "TC-ENG-25-02")]
    public void QuotaRemainingIsUsedInsteadOfVolumeFreeSpace()
    {
    }

    [Fact]
    public void FreeSpaceIsTheAmountAvailableToTheCaller()
    {
        // GetDiskFreeSpaceEx の lpFreeBytesAvailableToCaller (クォータを考慮した値) を使う。DriveInfo.AvailableFreeSpace も同じ値を返す。
        string folder = Path.GetTempPath();
        VolumeInfo volume = SystemVolumeInfoProvider.Instance.GetVolume(folder)!;
        long expected = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        Assert.NotNull(volume.AvailableFreeSpace);
        Assert.InRange(volume.AvailableFreeSpace!.Value, expected - 256L * 1024 * 1024, expected + 256L * 1024 * 1024);
    }
}
