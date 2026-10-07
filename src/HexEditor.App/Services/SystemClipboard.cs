using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App.Services;

/// <summary>
/// システムのクリップボードの読み書き。アプリはクリップボードをこのクラス経由で使う。
/// テスト用のビルドを --test-hooks で起動したときは、利用者のクリップボードに触れないよう、アプリの中だけの代わりを使う
/// (テスト方針 7.2。中身はテスト用の命令の通り道で読み書きする)。
/// </summary>
public static class SystemClipboard
{
#if HEX_TEST_HOOKS
    private static DataPackage? _testContent;

    /// <summary>true なら、システムのクリップボードの代わりを使う。</summary>
    private static bool UseTestClipboard => TestHooks.Active;
#endif

    /// <summary>クリップボードに入れる。</summary>
    public static void SetContent(DataPackage package)
    {
#if HEX_TEST_HOOKS
        if (UseTestClipboard)
        {
            _testContent = package;
            return;
        }
#endif
        Clipboard.SetContent(package);
    }

    /// <summary>クリップボードの中身。</summary>
    public static DataPackageView GetContent()
    {
#if HEX_TEST_HOOKS
        if (UseTestClipboard)
        {
            return (_testContent ?? new DataPackage()).GetView();
        }
#endif
        return Clipboard.GetContent();
    }
}
