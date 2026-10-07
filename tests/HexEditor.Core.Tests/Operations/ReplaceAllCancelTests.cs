using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Operations;

/// <summary>ENG-09 の受け入れ基準 4: 「すべて置換」をキャンセルした後、内容が開始前と同じ。</summary>
public sealed class ReplaceAllCancelTests
{
    [Fact(Skip = "すべて置換 (FIND-23、フェーズ 1) が未実装。テストケースのフェーズは 1 に直した")]
    [Trait(TC, "TC-ENG-09-04")]
    public void CancellingReplaceAllRestoresTheContent()
    {
    }
}
