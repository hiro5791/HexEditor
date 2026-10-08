#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;

namespace HexEditor.App;

/// <summary>スクリーンリーダー対応のテスト用の命令 (UI-51。テスト方針 7.2)。</summary>
public sealed partial class MainWindow
{
    /// <summary>アクセシビリティの命令。知らない命令なら null。</summary>
    private JsonObject? HandleAccessibilityTestCommand(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "windowAnnouncements":
            {
                // ウィンドウから送った読み上げ文 (Hex ビューの読み上げは "announcements")。clear なら読んだ後に消す。
                var items = new JsonArray([.. _windowAnnouncements.Select(a => (JsonNode?)new JsonObject
                {
                    ["time"] = a.Ms,
                    ["id"] = a.Id,
                    ["text"] = a.Text,
                })]);
                if (request["clear"]?.GetValue<bool>() == true)
                {
                    _windowAnnouncements.Clear();
                }

                return new JsonObject { ["items"] = items };
            }

            default:
                return null;
        }
    }
}
#endif
