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
}
