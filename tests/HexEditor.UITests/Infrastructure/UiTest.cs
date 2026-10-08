// UI テストは実際のアプリを起動するため、同時に 1 つずつ実行する (ウィンドウ・単一インスタンスのキーの衝突を避ける)。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HexEditor.UITests.Infrastructure;

/// <summary>テストの属性の名前。</summary>
public static class UiTest
{
    /// <summary>テストケース ID の属性 (テスト方針 6.2)。<c>[Trait(UiTest.TC, "TC-...")]</c>。</summary>
    public const string TC = "TC";

    /// <summary>CI で分けて実行するための分類。<c>[Trait(UiTest.Category, UiTest.UI)]</c>。</summary>
    public const string Category = "Category";

    public const string UI = "UI";

    /// <summary>
    /// 待つ時間の倍率 (環境変数 HEXEDITOR_UITEST_TIMEOUT_SCALE。既定 1)。混んだ CI のランナーでは状態が変わるまでに時間がかかるので、
    /// 待つ上限 (WaitUntilAsync、命令の答え) だけを延ばす。性能の判定の時間は変えない。
    /// </summary>
    public static double TimeoutScale { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("HEXEDITOR_UITEST_TIMEOUT_SCALE"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double scale) && scale >= 1 ? scale : 1;

    /// <summary>待つ上限に <see cref="TimeoutScale"/> を掛ける。</summary>
    public static TimeSpan Scaled(TimeSpan timeout) => timeout * TimeoutScale;
}
