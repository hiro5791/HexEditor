#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services.Audio;
using HexEditor.Core.Templates.Visualization;
using Microsoft.UI.Xaml.Automation;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、フェーズ 4 のテンプレートと自動化の命令 (テスト方針 7.2)。
/// <list type="bullet">
/// <item><c>tplvis</c>: テンプレートのビジュアライザ (07 の TPL-16)。<c>action</c> が <c>state</c> (既定)・<c>show</c> (<c>path</c> のノードを選んで表示)・
/// <c>save</c> (<c>path</c> に保存)・<c>audio</c> (<c>op</c>: <c>fake</c> (仮想の出力を使う)・<c>play</c>・<c>stop</c>・<c>advance</c> (<c>ms</c>)・
/// <c>received</c> (仮想の出力が受け取ったサンプル。<c>count</c> 個まで))・<c>row</c> (表の <c>index</c> 行目)。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleTplAutoTestCommandsAsync(string cmd, JsonObject request) => cmd switch
    {
        "tplvis" => await TestTemplateVisualizerAsync(request),
        _ => await HandleTplAutoTestCommands2Async(cmd, request),
    };

    private async Task<JsonObject> TestTemplateVisualizerAsync(JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        switch (action)
        {
            case "show":
            {
                string path = request["path"]!.GetValue<string>();
                bool ok = await TemplateVm.GoToPathAsync(path);
                if (ok && SelectedVisualizerNode() is { } node)
                {
                    ShowPanel(TemplateVisualizerPanelId);
                    await TemplateVisVm.ShowAsync(node);
                }

                return TemplateVisualizerState(ok);
            }

            case "save":
                return new JsonObject { ["saved"] = await SaveTemplateVisualAsync(request["path"]!.GetValue<string>()) };
            case "audio":
                return TemplateVisualizerAudio(request);
            case "row":
            {
                int index = request["index"]!.GetValue<int>();
                var list = TemplateVisualizerView?.Table;
                var rows = list?.ItemsSource as System.Collections.IList;
                var row = rows is not null && index < rows.Count ? rows[index] as Controls.TemplateVisualizerPanel.TableRow : null;
                return new JsonObject
                {
                    ["values"] = row is null ? null : new JsonArray([.. row.Values.Select(v => (JsonNode?)v)]),
                    ["name"] = row?.ToString(),
                };
            }

            default:
                return TemplateVisualizerState(true);
        }
    }

    private JsonObject TemplateVisualizerState(bool selected)
    {
        var vm = TemplateVisVm;
        var view = TemplateVisualizerView;
        var result = new JsonObject
        {
            ["selected"] = selected,
            ["visible"] = IsPanelShown(TemplateVisualizerPanelId),
            ["kind"] = vm.Visual?.Kind.ToString(),
            ["node"] = vm.Node?.Path,
            ["loading"] = vm.IsLoading,
            ["header"] = vm.HeaderText,
            ["message"] = vm.MessageText,
            ["altText"] = vm.AltText,
            ["contentName"] = view?.ContentElement is { } e ? AutomationProperties.GetName(e) : null,
            ["canSave"] = vm.CanSave,
            ["playing"] = vm.IsPlaying,
            ["positionMs"] = vm.PlaybackPosition.TotalMilliseconds,
            ["waveform"] = view?.WaveformBuckets ?? 0,
        };
        switch (vm.Visual)
        {
            case BitmapVisual b:
                result["width"] = b.Width;
                result["height"] = b.Height;
                result["pixels"] = new JsonArray([.. b.Pixels.Select(p => (JsonNode?)p.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))]);
                break;
            case EncodedImageVisual when vm.Decoded is { } d:
                result["width"] = d.Width;
                result["height"] = d.Height;
                break;
            case TableVisual t:
                result["columns"] = new JsonArray([.. t.Columns.Select(c => (JsonNode?)c)]);
                result["rows"] = t.RowCount;
                result["listRows"] = (view?.Table?.ItemsSource as System.Collections.IList)?.Count ?? -1;
                break;
            case AudioVisual a:
                result["sampleRate"] = a.SampleRate;
                result["durationMs"] = a.Duration.TotalMilliseconds;
                break;
        }

        return result;
    }

    private JsonObject TemplateVisualizerAudio(JsonObject request)
    {
        string op = request["op"]!.GetValue<string>();
        switch (op)
        {
            case "fake":
                AudioOutputs.Fake ??= new FakeAudioOutput();
                break;
            case "play":
                TemplateVisVm.Play();
                break;
            case "stop":
                TemplateVisVm.StopAudio();
                break;
            case "advance":
                AudioOutputs.Fake?.Advance(TimeSpan.FromMilliseconds(request["ms"]!.GetValue<double>()));
                break;
            case "received":
            {
                int count = request["count"]?.GetValue<int>() ?? int.MaxValue;
                short[] received = AudioOutputs.Fake?.Received ?? [];
                short[] part = received[..Math.Min(count, received.Length)];
                byte[] bytes = new byte[part.Length * 2];
                Buffer.BlockCopy(part, 0, bytes, 0, bytes.Length);
                return new JsonObject
                {
                    ["count"] = received.Length,
                    ["samples"] = Convert.ToBase64String(bytes),
                    ["sampleRate"] = AudioOutputs.Fake?.SampleRate ?? 0,
                    ["plays"] = AudioOutputs.Fake?.PlayCount ?? 0,
                };
            }
        }

        return TemplateVisualizerState(true);
    }
}
#endif
