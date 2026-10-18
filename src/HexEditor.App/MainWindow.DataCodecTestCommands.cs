#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;

namespace HexEditor.App;

/// <summary>
/// 暗号化・圧縮 (EDIT-40・EDIT-41)、派生ドキュメント (EDIT-46)、YARA (FIND-33) のテスト用の命令。ダイアログの欄は codecDialogSet で入れ
/// (鍵の欄はパスワードの入力欄のため)、ボタンは dialogButton で押す。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleDataCodecTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "codecDialog":
                return CodecDialogTestModel();
            case "codecDialogSet":
                CodecDialogTestSet(request["field"]!.GetValue<string>(), request["value"]!.GetValue<string>());
                return CodecDialogTestModel();
            case "derived":
                return DerivedTestState();
            case "selectCompressed":
                return LastSelectCompressed is { } r
                    ? new JsonObject { ["format"] = r.Format.ToString(), ["consumed"] = r.ConsumedInput, ["output"] = r.OutputLength }
                    : new JsonObject { ["format"] = null };
            case "yaraPanel":
                return Yara.TestState();
            case "yaraSet":
                Yara.TestSet(request["files"]?.AsArray().Select(n => n!.GetValue<string>()).ToList(), request["externals"]?.GetValue<string>(),
                    request["scope"]?.GetValue<int>(), request["folder"]?.GetValue<string>());
                return Yara.TestState();
            case "yaraScan":
                _ = Yara.ScanAsync();
                return new JsonObject();
            case "yaraSaveSet":
                await Yara.SaveSetAsync(request["name"]!.GetValue<string>());
                return Yara.TestState();
            default:
                return null;
        }
    }

    private JsonObject DerivedTestState()
    {
        if (Vm.Selected is not { } doc)
        {
            return new JsonObject { ["derived"] = false };
        }

        return new JsonObject
        {
            ["derived"] = doc.IsDerived,
            ["canWriteBack"] = doc.CanWriteBack,
            ["band"] = doc.DerivedBandText,
            ["title"] = doc.TabTitle,
            ["offset"] = doc.Derived?.Offset,
            ["length"] = doc.Derived?.Length,
            ["depth"] = doc.Derived?.Depth,
        };
    }
}
#endif
