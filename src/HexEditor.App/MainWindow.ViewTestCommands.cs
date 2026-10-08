#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 表示設定のテスト用の命令 (テスト方針 7.2)。入力欄のフライアウトの操作、ハイコントラストの模擬、ズーム、付加情報 (ブックマークの代わり)、
/// 配色の編集 (設定画面の代わり) など。OS の設定は変えない。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>表示の命令。知らない命令なら null。</summary>
    private async Task<JsonObject?> HandleViewTestCommandsAsync(string cmd, JsonObject request)
    {
        await Task.CompletedTask;
        return cmd switch
        {
            "viewInput" => TestViewInput(request),
            "forceHighContrast" => TestEachView(v => v.ForcedHighContrast = request["on"]?.GetValue<bool>() ?? true),
            "zoom" => TestEachView(v => v.ZoomAt(request["zoom"]!.GetValue<double>(), request["pointerY"]?.GetValue<double>()), selectedOnly: true),
            "screenZoom" => TestEachView(v => v.ScreenZoom = request["zoom"]!.GetValue<double>()),
            "annotate" => TestAnnotate(request),
            "chooseRadix" => TestEachView(v => v.ChooseRadix(Enum.Parse<OffsetRadix>(request["radix"]!.GetValue<string>(), ignoreCase: true)), selectedOnly: true),
            "mouseHistory" => TestEachView(v => v.InjectHistoryButton(request["back"]?.GetValue<bool>() ?? true), selectedOnly: true),
            "fonts" => new JsonObject
            {
                ["items"] = new JsonArray([.. FontCatalog.ListForSettings(request["all"]?.GetValue<bool>() ?? false).Select(f => (JsonNode?)f)]),
                ["monospaced"] = new JsonArray([.. FontCatalog.Families().Where(f => f.Monospaced).Select(f => (JsonNode?)f.Name)]),
            },
            "hideFonts" => TestHideFonts(request),
            "viewSettings" => Editor is { } e ? new JsonObject
            {
                ["view"] = e.View.ToJson(),
                ["referencePoint"] = e.ReferencePoint,
                ["bytesPerRow"] = e.BytesPerRow,
                ["encoding"] = e.TextEncoding.Id,
            } : throw new InvalidOperationException("No document."),
            "colorScheme" => TestColorScheme(request),
            "menuItem" => TestMenuItem(request["id"]!.GetValue<string>()),
            "systemColor" => new JsonObject
            {
                ["color"] = Application.Current.Resources[request["key"]!.GetValue<string>()] is Windows.UI.Color c ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : null,
            },
            _ => null,
        };
    }

    private JsonObject TestEachView(Action<HexView> action, bool selectedOnly = false)
    {
        foreach (HexView view in selectedOnly ? (SelectedView() is { } v ? [v] : []) : _views)
        {
            action(view);
            view.RenderNow();
        }

        return new JsonObject();
    }

    /// <summary>入力欄のフライアウト: {text?, commit?}。入力欄の状態 (説明文・確定ボタン・赤枠) を返す。</summary>
    private JsonObject TestViewInput(JsonObject request)
    {
        if (_inputBox is null || _inputFlyout is null)
        {
            return new JsonObject { ["open"] = false };
        }

        if (request["text"]?.GetValue<string>() is { } text)
        {
            _inputBox.Text = text;
            ValidateInput();
        }

        if (request["commit"]?.GetValue<bool>() == true)
        {
            // Enter と同じ (確定できない値なら何もしない)。
            CommitInput();
        }

        SelectedView()?.RenderNow();
        return new JsonObject
        {
            ["open"] = _inputOpen,
            ["text"] = _inputBox.Text,
            ["error"] = _inputError?.Visibility == Visibility.Visible ? _inputError.Text : null,
            ["okEnabled"] = _inputOk?.IsEnabled ?? false,
            ["invalid"] = _inputBox.BorderBrush is not null,
        };
    }

    /// <summary>付加情報 (ブックマークの名前の代わり): {offset, length, name}。範囲に重なるバイトでその名前を返す。</summary>
    private JsonObject TestAnnotate(JsonObject request)
    {
        long offset = TestHookSettings.ReadLong(request["offset"], 0);
        long length = TestHookSettings.ReadLong(request["length"], 1);
        string name = request["name"]!.GetValue<string>();
        _testAnnotations.Add((offset, length, name));
        foreach (HexView view in _views)
        {
            view.AnnotationNames = at => [.. _testAnnotations.Where(a => at >= a.Offset && at < a.Offset + a.Length).Select(a => a.Name)];
        }

        return new JsonObject();
    }

    private readonly List<(long Offset, long Length, string Name)> _testAnnotations = [];

    /// <summary>メニューの項目の状態 (有効・チェック・サブメニューの項目数)。メニューを開かずに読む。</summary>
    private JsonObject TestMenuItem(string id)
    {
        var pending = new Stack<MenuFlyoutItemBase>(MainMenu.Items.SelectMany(m => m.Items));
        while (pending.Count > 0)
        {
            MenuFlyoutItemBase item = pending.Pop();
            if (AutomationProperties.GetAutomationId(item) == id)
            {
                return new JsonObject
                {
                    ["found"] = true,
                    ["enabled"] = item.IsEnabled,
                    ["checked"] = item switch
                    {
                        ToggleMenuFlyoutItem t => t.IsChecked,
                        RadioMenuFlyoutItem r => r.IsChecked,
                        _ => null,
                    },
                    ["text"] = (item as MenuFlyoutItem)?.Text ?? (item as MenuFlyoutSubItem)?.Text,
                    ["items"] = item is MenuFlyoutSubItem sub
                        ? new JsonArray([.. sub.Items.OfType<MenuFlyoutItem>().Select(i => (JsonNode?)i.Text)])
                        : null,
                };
            }

            if (item is MenuFlyoutSubItem children)
            {
                foreach (MenuFlyoutItemBase child in children.Items)
                {
                    pending.Push(child);
                }
            }
        }

        return new JsonObject { ["found"] = false };
    }

    private JsonObject TestHideFonts(JsonObject request)
    {
        FontCatalog.HiddenForTest.Clear();
        foreach (JsonNode? name in request["names"]?.AsArray() ?? [])
        {
            FontCatalog.HiddenForTest.Add(name!.GetValue<string>());
        }

        ApplyViewOptions();
        return new JsonObject { ["font"] = SelectedView()?.HexFontFamily };
    }

    /// <summary>
    /// 配色の操作 (設定画面の「外観 > 配色」の代わり): {action: "duplicate" | "set" | "save" | "export" | "import" | "select" | "warnings", ...}。
    /// </summary>
    private JsonObject TestColorScheme(JsonObject request)
    {
        var store = new ColorSchemeStore(App.Settings.Folder);
        string action = request["action"]!.GetValue<string>();
        switch (action)
        {
            case "duplicate":
                {
                    ColorScheme copy = store.Find(request["from"]?.GetValue<string>()).Duplicate(request["name"]!.GetValue<string>());
                    _editingScheme = copy;
                    return new JsonObject { ["name"] = copy.Name };
                }

            case "set":
                {
                    ColorScheme scheme = _editingScheme ?? throw new InvalidOperationException("No scheme is being edited.");
                    var element = Enum.Parse<SchemeElement>(request["element"]!.GetValue<string>(), ignoreCase: true);
                    SchemeColor.TryParse(request["color"]!.GetValue<string>(), out SchemeColor color);
                    bool? dark = request["dark"]?.GetValue<bool>();
                    if (dark is null or false)
                    {
                        scheme.Set(element, false, color);
                    }

                    if (dark is null or true)
                    {
                        scheme.Set(element, true, color);
                    }

                    return WarningsOf(scheme);
                }

            case "warnings":
                return WarningsOf(_editingScheme ?? throw new InvalidOperationException("No scheme is being edited."));
            case "save":
                return new JsonObject { ["path"] = store.Save(_editingScheme ?? throw new InvalidOperationException("No scheme is being edited.")) };
            case "export":
                ColorSchemeStore.Export(store.Find(request["name"]!.GetValue<string>()), request["path"]!.GetValue<string>());
                return new JsonObject();
            case "import":
                return new JsonObject { ["name"] = store.Import(request["path"]!.GetValue<string>()).Name };
            case "select":
                App.Settings.SetString(ViewOptions.ColorSchemeKey, request["name"]!.GetValue<string>(), ColorScheme.DefaultName);
                ApplyViewOptions();
                SelectedView()?.RenderNow();
                return new JsonObject();
            default:
                throw new ArgumentException($"Unknown color scheme action: {action}");
        }

        static JsonObject WarningsOf(ColorScheme scheme) => new()
        {
            ["warnings"] = new JsonArray([.. scheme.ContrastWarnings().Select(w => (JsonNode?)new JsonObject
            {
                ["foreground"] = w.Foreground.ToString(),
                ["background"] = w.Background.ToString(),
                ["dark"] = w.Dark,
                ["ratio"] = w.Ratio,
            })]),
        };
    }

    private ColorScheme? _editingScheme;
}
#endif
