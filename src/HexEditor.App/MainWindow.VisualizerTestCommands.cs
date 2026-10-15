#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.ViewModels;
using HexEditor.Core.View;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、画像表示 (VIEW-36) の命令 (テスト方針 7.2)。
/// <list type="bullet">
/// <item><c>visualizer</c>: 設定・描いた範囲・拡大率・マウスを合わせた画素の表示・枠の数。<c>x</c>・<c>y</c>・<c>width</c>・<c>height</c> を
/// 指定すると、その範囲の画素 (拡大前、0xAARRGGBB の 16 進の文字列の配列) も返す。</item>
/// <item><c>visualizerSet</c>: 設定の欄で選んだ・入れたのと同じように設定する (<c>format</c>、<c>width</c>、<c>stride</c>、<c>start</c>
/// (follow / selection / fixed)、<c>fixed</c>、<c>bottomUp</c>、<c>zoom</c>)。</item>
/// <item><c>visualizerClick</c> / <c>visualizerHover</c> / <c>visualizerKey</c>: 画素 (x, y) をクリックした・マウスを合わせた・キーを押したのと同じ処理。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private JsonObject? HandleVisualizerTestCommand(string cmd, JsonObject request) => cmd switch
    {
        "visualizer" => TestVisualizerState(request),
        "visualizerSet" => TestVisualizerSet(request),
        "visualizerClick" => new JsonObject
        {
            ["clicked"] = VisualizerVm().Click(request["x"]!.GetValue<int>(), request["y"]!.GetValue<int>()),
        },
        "visualizerHover" => TestVisualizerHover(request),
        "visualizerKey" => new JsonObject
        {
            ["handled"] = VisualizerView?.HandleImageKey(Enum.Parse<Windows.System.VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true)) ?? false,
            ["hover"] = VisualizerVm().HoverText,
        },
        _ => null,
    };

    private JsonObject TestVisualizerHover(JsonObject request)
    {
        VisualizerViewModel vm = VisualizerVm();
        vm.Hover(request["x"]!.GetValue<int>(), request["y"]!.GetValue<int>());
        return new JsonObject { ["hover"] = vm.HoverText };
    }

    private JsonObject TestVisualizerState(JsonObject request)
    {
        VisualizerViewModel vm = VisualizerVm();
        var result = new JsonObject
        {
            ["visible"] = IsPanelShown(VisualizerPanelId),
            ["active"] = vm.IsActive,
            ["format"] = vm.Format.ToString(),
            ["width"] = vm.Width,
            ["stride"] = vm.Stride,
            ["startMode"] = vm.StartMode.ToString(),
            ["bottomUp"] = vm.BottomUp,
            ["zoom"] = vm.EffectiveZoom,
            ["hover"] = vm.HoverText,
            ["status"] = vm.StatusText,
            ["scrollMaximum"] = vm.ScrollMaximum,
            ["frames"] = VisualizerView?.FrameCount ?? 0,
            ["renderMs"] = vm.LastRenderMs + (VisualizerView?.LastDrawMs ?? 0),
        };
        if (vm.Layout is { } layout)
        {
            result["layout"] = new JsonObject
            {
                ["start"] = layout.Start,
                ["stride"] = layout.Stride,
                ["width"] = layout.Width,
                ["rows"] = layout.Rows,
                ["truncated"] = layout.Truncated,
            };
            if (request["width"] is not null)
            {
                int x0 = request["x"]?.GetValue<int>() ?? 0, y0 = request["y"]?.GetValue<int>() ?? 0;
                int w = request["width"]!.GetValue<int>(), h = request["height"]!.GetValue<int>();
                var pixels = new JsonArray();
                for (int y = y0; y < Math.Min(layout.Rows, y0 + h); y++)
                {
                    for (int x = x0; x < Math.Min(layout.Width, x0 + w); x++)
                    {
                        pixels.Add(vm.Pixels[y * layout.Width + x].ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                result["pixels"] = pixels;
            }
        }

        return result;
    }

    private JsonObject TestVisualizerSet(JsonObject request)
    {
        VisualizerViewModel vm = VisualizerVm();
        if (request["format"]?.GetValue<string>() is { } format)
        {
            vm.Format = Enum.Parse<BytePixelFormat>(format, ignoreCase: true);
        }

        if (request["width"]?.GetValue<int>() is { } width)
        {
            vm.Width = width;
        }

        if (request["stride"]?.GetValue<long>() is { } stride)
        {
            vm.Stride = stride;
        }

        if (request["fixed"]?.GetValue<string>() is { } text)
        {
            vm.FixedStartText = text;
        }

        if (request["start"]?.GetValue<string>() is { } start)
        {
            vm.StartMode = start switch { "selection" => ByteImageStart.SelectionStart, "fixed" => ByteImageStart.Fixed, _ => ByteImageStart.FollowCursor };
        }

        if (request["bottomUp"]?.GetValue<bool>() is { } bottomUp)
        {
            vm.BottomUp = bottomUp;
        }

        if (request["zoom"]?.GetValue<int>() is { } zoom)
        {
            vm.Zoom = zoom;
        }

        vm.Render();
        return TestVisualizerState(new JsonObject());
    }
}
#endif
