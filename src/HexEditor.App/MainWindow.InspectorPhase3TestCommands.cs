#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、フェーズ 3 のインスペクタ・ブックマーク・画像表示の命令 (テスト方針 7.2)。
/// <list type="bullet">
/// <item><c>inspectorNav</c>: 配列モードの状態 (INSP-21) と、最後に使った「ポインタとして移動」の方法 (INSP-20)。</item>
/// <item><c>inspectorArrayInput</c>: 配列モードの要素番号・間隔の欄に文字列を入れて Enter (UI オートメーションの入力と同じ処理)。</item>
/// <item><c>inspectorPointerBase</c>: 「基準を指定して相対」のフライアウトの基準を入れて確定する。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private JsonObject? HandleInspectorPhase3TestCommand(string cmd, JsonObject request) => cmd switch
    {
        "inspectorNav" => new JsonObject
        {
            ["arrayMode"] = _inspectorVm.IsArrayMode,
            ["barShown"] = InspectorView?.ArrayBarShown ?? false,
            ["index"] = _inspectorVm.ArrayIndexText,
            ["stride"] = _inspectorVm.ArrayStrideText,
            ["canPrevious"] = _inspectorVm.CanPreviousElement,
            ["canNext"] = _inspectorVm.CanNextElement,
            ["arrayType"] = _inspectorVm.ArrayTypeId,
            ["pointerMode"] = ViewModels.InspectorViewModel.LastPointerMode.ToString(),
        },
        "inspectorArrayInput" => TestInspectorArrayInput(request),
        "inspectorPointerBase" => TestInspectorPointerBase(request),
        _ => HandleBookmarkPhase3TestCommand(cmd, request),
    };

    private JsonObject TestInspectorArrayInput(JsonObject request)
    {
        string field = request["field"]!.GetValue<string>();
        string text = request["text"]!.GetValue<string>();
        TextBox box = (TextBox)(FindElement(field == "stride" ? "Inspector_ArrayStride" : "Inspector_ArrayIndex")
            ?? throw new InvalidOperationException("The array bar is not shown."));
        box.Text = text;
        bool ok = field == "stride" ? _inspectorVm.SetStride(text) : _inspectorVm.GoToElement(text);
        return new JsonObject { ["ok"] = ok };
    }

    private JsonObject TestInspectorPointerBase(JsonObject request)
    {
        InspectorPanel panel = InspectorView ?? throw new InvalidOperationException("The inspector is not shown.");
        if (request["text"]?.GetValue<string>() is { } text)
        {
            foreach (Microsoft.UI.Xaml.Controls.Primitives.Popup popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            {
                if (popup.Child is not null && FindIn(popup.Child, "Inspector_PointerBase") is TextBox box)
                {
                    box.Text = text;
                }
            }
        }

        return new JsonObject { ["submitted"] = panel.SubmitBaseFlyout() };
    }

    private static Microsoft.UI.Xaml.DependencyObject? FindIn(Microsoft.UI.Xaml.DependencyObject root, string automationId)
    {
        if (root is Microsoft.UI.Xaml.FrameworkElement fe && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(fe) == automationId)
        {
            return root;
        }

        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindIn(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
#endif
