#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;

namespace HexEditor.App;

/// <summary>スクリーンリーダー対応 (UI-51) と言語ごとのスクリーンショット (UI-47) のテスト用の命令 (テスト方針 7.2)。</summary>
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

            case "closeToolPage":
                // 設定画面などのページのタブを閉じる (言語ごとのスクリーンショット UI-47 で、設定画面の後に文書のタブへ戻す)。
                CloseToolPage(request["id"]!.GetValue<string>());
                return new JsonObject { ["pages"] = new JsonArray([.. ToolTabIds.Select(p => (JsonNode?)p)]) };

            default:
                return null;
        }
    }

    private const uint WmGetMinMaxInfo = 0x0024;
    private SubclassProc? _largeWindowSubclass;

    /// <summary>
    /// ウィンドウを画面より大きくできるようにする (テスト用。言語ごとのスクリーンショット UI-47 の 1920 × 1080 と表示倍率 200% の再現を、
    /// 画面の小さい CI のランナーでも撮るため)。WM_GETMINMAXINFO の最大の大きさを広げる。
    /// </summary>
    private void AllowWindowLargerThanScreen()
    {
        if (_largeWindowSubclass is not null)
        {
            return;
        }

        _largeWindowSubclass = (hwnd, msg, wParam, lParam, id, data) =>
        {
            nint result = DefSubclassProc(hwnd, msg, wParam, lParam);
            if (msg == WmGetMinMaxInfo && lParam != 0)
            {
                // MINMAXINFO の ptMaxTrackSize (先頭から POINT 4 つ分の後ろ)。
                System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 32, 16384);
                System.Runtime.InteropServices.Marshal.WriteInt32(lParam, 36, 16384);
            }

            return result;
        };
        SetWindowSubclass(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id), _largeWindowSubclass, 0x4846, 0);
    }
}
#endif
