#if HEX_TEST_HOOKS
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の命令のうち、ポインタ・ホイール・スクロールバー・Windows の設定の差し替え・クリップボードの代わり
/// (テスト方針 7.2)。実際のマウス・キーボード・クリップボード・Windows の設定は使わない。
/// </summary>
public sealed partial class MainWindow
{
    private JsonObject TestView(Action<HexView> action)
    {
        HexView view = CurrentView() ?? throw new InvalidOperationException("No hex view.");
        action(view);
        view.RenderNow();
        return new JsonObject();
    }

    /// <summary>
    /// ポインタ: {action: down / move / up / click, x, y (描画面の座標), device: mouse / touch, button: left / right, shift, clicks}。
    /// click は押して離すことを clicks 回続けて行う (2 ならダブルクリック)。
    /// </summary>
    private JsonObject TestPointer(JsonObject request) => TestView(view =>
    {
        var point = new Point(request["x"]?.GetValue<double>() ?? 0, request["y"]?.GetValue<double>() ?? 0);
        string device = request["device"]?.GetValue<string>() ?? "mouse";
        bool right = request["button"]?.GetValue<string>() == "right";
        bool shift = request["shift"]?.GetValue<bool>() ?? false;

        // Ctrl・Alt (マルチ選択・矩形選択・マルチカーソル。EDIT-06〜EDIT-08)。ドラッグ中に変えるときは move で ctrl / shift を指定する (EDIT-18)。
        bool ctrl = request["ctrl"]?.GetValue<bool>() ?? false;
        bool alt = request["alt"]?.GetValue<bool>() ?? false;
        switch (request["action"]?.GetValue<string>() ?? "click")
        {
            case "down":
                view.InjectPointerDown(point, device, right, shift, ctrl, alt);
                break;
            case "move":
                if (request["ctrl"] is not null || request["shift"] is not null)
                {
                    view.InjectDropModifiers(ctrl, shift);
                }

                view.InjectPointerMove(point);
                break;
            case "up":
                view.InjectPointerUp(point, ctrl, shift);
                break;
            default:
                int clicks = (int)TestHookSettings.ReadLong(request["clicks"], 1);
                for (int i = 0; i < clicks; i++)
                {
                    view.InjectPointerDown(point, device, right, shift, ctrl, alt);
                    if (!right)
                    {
                        view.InjectPointerUp(point, ctrl, shift);
                    }
                }

                break;
        }
    });

    /// <summary>ホイール: {delta (1 ノッチ 120、下・左は負), horizontal, shift, count}。</summary>
    private JsonObject TestWheel(JsonObject request) => TestView(view =>
    {
        int delta = (int)TestHookSettings.ReadLong(request["delta"], -120);
        bool horizontal = request["horizontal"]?.GetValue<bool>() ?? false;
        bool shift = request["shift"]?.GetValue<bool>() ?? false;
        long count = TestHookSettings.ReadLong(request["count"], 1);
        for (long i = 0; i < count; i++)
        {
            view.InjectWheel(delta, horizontal, shift);
        }
    });

    /// <summary>縦スクロールバー: {part: テンプレートの部品の名前} で矢印ボタンなどを押す。{type: ScrollEventType, value} で Scroll イベントと同じ処理。</summary>
    private JsonObject TestScrollBar(JsonObject request) => TestView(view =>
    {
        if (request["part"]?.GetValue<string>() is { } part)
        {
            view.InvokeScrollBarPart(part);
        }
        else
        {
            view.InjectVerticalScroll(Enum.Parse<ScrollEventType>(request["type"]!.GetValue<string>(), ignoreCase: true),
                request["value"]?.GetValue<double>() ?? 0);
        }
    });

    /// <summary>
    /// Windows の設定の差し替え: {wheelScrollLines: 行数 / "page" / null, textScaleFactor, rasterizationScale}。
    /// 利用者の PC の設定は変えず、アプリが読む値だけを変えて、設定の変更の通知と同じ処理を行う。
    /// </summary>
    private JsonObject TestSetSystem(JsonObject request)
    {
        if (request.ContainsKey("wheelScrollLines"))
        {
            HexView.TestWheelScrollLines = request["wheelScrollLines"] switch
            {
                null => null,
                JsonValue v when v.TryGetValue(out string? s) && s == "page" => uint.MaxValue,
                JsonNode n => (uint)TestHookSettings.ReadLong(n, 3),
            };
        }

        if (request.ContainsKey("textScaleFactor"))
        {
            HexView.TestTextScaleFactor = request["textScaleFactor"]?.GetValue<double>();
            foreach (HexView view in _views)
            {
                view.SimulateTextScaleChanged();
            }
        }

        if (request["rasterizationScale"]?.GetValue<double>() is { } scale)
        {
            foreach (HexView view in _views)
            {
                view.SimulateRasterizationScale(scale);
            }
        }

        CurrentView()?.RenderNow();
        return new JsonObject();
    }

    /// <summary>移動バーの入力欄でキーを押す: {key, shift}。</summary>
    private JsonObject TestGoToKey(JsonObject request)
    {
        bool handled = GoToBar.InjectKey(Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true),
            request["shift"]?.GetValue<bool>() ?? false);
        CurrentView()?.RenderNow();
        return new JsonObject { ["handled"] = handled };
    }

    /// <summary>ウィンドウの大きさを変える: {width, height} (表示倍率 100% 換算の epx)。アクティブにはしない。</summary>
    private JsonObject TestResize(JsonObject request)
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(request["width"]!.GetValue<double>() * scale),
            (int)Math.Round(request["height"]?.GetValue<double>() is { } h ? h * scale : AppWindow.Size.Height)));
        return new JsonObject();
    }

    /// <summary>クリップボード (テストでは代わりのもの) の中身: 形式の一覧、テキスト、HexEditor.Binary (Hex)、HexEditor.Meta。</summary>
    private static async Task<JsonObject> TestClipboardAsync()
    {
        DataPackageView view = SystemClipboard.GetContent();
        var result = new JsonObject
        {
            ["formats"] = new JsonArray([.. view.AvailableFormats.Select(f => (JsonNode?)f)]),
        };
        if (view.Contains(StandardDataFormats.Text))
        {
            result["text"] = await view.GetTextAsync();
        }

        if (view.Contains("HexEditor.Binary") && await view.GetDataAsync("HexEditor.Binary") is IRandomAccessStream stream)
        {
            byte[] bytes = new byte[stream.Size];
            await stream.GetInputStreamAt(0).ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            result["binary"] = Convert.ToHexString(bytes);
        }

        if (view.Contains("HexEditor.Meta") && await view.GetDataAsync("HexEditor.Meta") is string meta)
        {
            result["meta"] = meta;
        }

        return result;
    }

    /// <summary>他のアプリがクリップボードに入れたのと同じ状態にする: {text} はテキスト、{binary: Hex} は HexEditor.Binary。</summary>
    private static async Task<JsonObject> TestSetClipboardAsync(JsonObject request)
    {
        var package = new DataPackage();
        if (request["text"]?.GetValue<string>() is { } text)
        {
            package.SetText(text);
        }

        if (request["binary"]?.GetValue<string>() is { } hex)
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(Convert.FromHexString(hex));
                await writer.StoreAsync();
                await writer.FlushAsync();
            }

            package.SetData("HexEditor.Binary", stream);
        }

        SystemClipboard.SetContent(package);
        return new JsonObject();
    }

    /// <summary>要素の外接矩形 (ウィンドウの内容の左上からの epx)。</summary>
    private JsonObject BoundsInRoot(FrameworkElement element)
    {
        try
        {
            Rect r = element.TransformToVisual(Root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new JsonObject { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };
        }
        catch (ArgumentException)
        {
            // ポップアップの中の要素はウィンドウの内容の木にない。
            return new JsonObject();
        }
    }
}
#endif
