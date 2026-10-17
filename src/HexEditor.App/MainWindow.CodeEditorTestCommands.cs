#if HEX_TEST_HOOKS
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;

namespace HexEditor.App;

/// <summary>
/// コードエディタ (Monaco。08 の AUTO-20、07 の TPL-31) のテスト用の命令 ("codeEditor")。表示中のスクリプトエディタ・テンプレートエディタの
/// Monaco の状態を、ページで JavaScript を評価して読む (WebView2 の ExecuteScriptAsync。画面全体のキー入力・マウスは使わない)。
/// </summary>
/// <remarks>
/// action: state / wait (準備ができるまで待つ) / type (text を入力したのと同じ。補完のきっかけの文字も効く) / suggest (補完の一覧を出して読む) /
/// jsCompletions (TypeScript の言語サービスの候補と、name の説明) / markers / direction / resources / key (keydown をページの中で起こす) /
/// decorations (line の余白の印) / focus / position / simulateFailure (次に作るエディタの WebView2 を失敗させる) / eval (script)。
/// </remarks>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleCodeEditorTestCommandAsync(string cmd, JsonObject request)
    {
        if (cmd != "codeEditor")
        {
            return null;
        }

        string action = request["action"]?.GetValue<string>() ?? "state";
        if (action == "simulateFailure")
        {
            MonacoCodeEditor.SimulateFailure = request["value"]?.GetValue<bool>() ?? true;
            return new JsonObject { ["simulateFailure"] = MonacoCodeEditor.SimulateFailure };
        }

        IScriptCodeEditor? editor = (IScriptCodeEditor?)ActiveScriptEditor?.Editor ?? ActiveTemplateEditor?.CodeEditor;
        if (editor is null)
        {
            return new JsonObject { ["open"] = false };
        }

        var monaco = editor as MonacoCodeEditor;
        switch (action)
        {
            case "state":
                return State();
            case "wait":
            {
                await WaitUntilForTestAsync(() => monaco is null || monaco.IsReady || !monaco.IsMonaco, TimeSpan.FromSeconds(request["timeoutSeconds"]?.GetValue<int>() ?? 60));
                return State();
            }

            case "focus":
                editor.FocusEditor();
                await Task.Delay(50);
                return State();
            case "eval":
                return new JsonObject { ["result"] = await EvalAsync(request["script"]!.GetValue<string>()) };
            case "type":
            {
                string text = JsonSerializer.Serialize(request["text"]!.GetValue<string>());
                await EvalAsync($"(() => {{ const e = hexEditor.editor; e.focus(); e.trigger('keyboard', 'type', {{ text: {text} }}); return true; }})()");
                return State();
            }

            case "position":
            {
                int line = request["line"]?.GetValue<int>() ?? 1;
                int column = request["column"]?.GetValue<int>() ?? 1;
                await EvalAsync($"(() => {{ const e = hexEditor.editor; e.setPosition({{ lineNumber: {line}, column: {column} }}); e.focus(); return true; }})()");
                return State();
            }

            case "suggest":
            {
                // 補完の一覧を出し、見えている行 (ラベルと説明) を読む。focus を指定すると、その候補を選んで説明の欄を開く
                string focus = JsonSerializer.Serialize(request["focus"]?.GetValue<string>() ?? string.Empty);
                if (request["trigger"]?.GetValue<bool>() != false)
                {
                    await EvalAsync("(() => { hexEditor.editor.focus(); hexEditor.editor.trigger('test', 'editor.action.triggerSuggest', {}); return true; })()");
                }

                return new JsonObject
                {
                    ["result"] = await EvalAsync(SuggestScript(focus)),
                };
            }

            case "jsCompletions":
            {
                string name = JsonSerializer.Serialize(request["name"]?.GetValue<string>() ?? string.Empty);
                return new JsonObject { ["result"] = await EvalAsync(JsCompletionsScript(name)) };
            }

            case "markers":
                return new JsonObject
                {
                    ["result"] = await EvalAsync("JSON.stringify(hexEditor.monaco.editor.getModelMarkers({ resource: hexEditor.model.uri }).map(m => ({ owner: m.owner, line: m.startLineNumber, column: m.startColumn, endLine: m.endLineNumber, endColumn: m.endColumn, severity: m.severity, message: typeof m.message === 'string' ? m.message : String(m.message && m.message.value) })))"),
                };
            case "direction":
                return new JsonObject
                {
                    ["webViewFlowDirection"] = monaco?.WebView?.FlowDirection.ToString(),
                    ["editorFlowDirection"] = editor.View.FlowDirection.ToString(),
                    ["windowFlowDirection"] = Root.FlowDirection.ToString(),
                    ["result"] = await EvalAsync("(() => { const e = hexEditor.editor; const m = hexEditor.model; const dom = e.getDomNode(); const first = e.getScrolledVisiblePosition({ lineNumber: 1, column: 1 }); const last = e.getScrolledVisiblePosition({ lineNumber: 1, column: Math.max(1, m.getLineMaxColumn(1) - 1) }); return JSON.stringify({ direction: getComputedStyle(dom).direction, bodyDirection: getComputedStyle(document.body).direction, linesDirection: getComputedStyle(dom.querySelector('.view-lines')).direction, firstX: first ? first.left : null, lastX: last ? last.left : null, line: m.getLineContent(1) }); })()"),
                };
            case "resources":
                return new JsonObject
                {
                    ["page"] = await EvalAsync("JSON.stringify({ href: location.href, entries: performance.getEntriesByType('resource').map(e => e.name) })"),
                    ["requested"] = new JsonArray([.. (monaco?.RequestedUris ?? []).Select(u => (JsonNode?)u)]),
                };
            case "key":
            {
                // ページの中で keydown を起こす (Monaco の入力欄に届くのと同じ。アプリに渡すキーは MonacoCodeEditor.KeyForwarded に届く)
                int keyCode = request["keyCode"]!.GetValue<int>();
                string key = JsonSerializer.Serialize(request["key"]?.GetValue<string>() ?? string.Empty);
                string flags = $"ctrlKey: {Js(request["ctrl"])}, shiftKey: {Js(request["shift"])}, altKey: {Js(request["alt"])}";
                LastForwardedKeyCommand = null;
                await EvalAsync($"(() => {{ const t = document.activeElement || document.body; const ev = new KeyboardEvent('keydown', {{ key: {key}, keyCode: {keyCode}, which: {keyCode}, bubbles: true, cancelable: true, {flags} }}); t.dispatchEvent(ev); return ev.defaultPrevented; }})()");
                await Task.Delay(100);
                JsonObject state = State();
                state["forwardedCommand"] = LastForwardedKeyCommand;
                return state;
            }

            case "decorations":
            {
                int line = request["line"]?.GetValue<int>() ?? 1;
                return new JsonObject
                {
                    ["result"] = await EvalAsync($"JSON.stringify((hexEditor.editor.getLineDecorations({line}) || []).map(d => ({{ glyph: d.options.glyphMarginClassName || null, className: d.options.className || null, lines: d.options.linesDecorationsClassName || null, hover: d.options.glyphMarginHoverMessage ? d.options.glyphMarginHoverMessage.value : null }})))"),
                };
            }

            default:
                throw new ArgumentException("Unknown codeEditor action: " + action);
        }

        JsonObject State() => new()
        {
            ["open"] = true,
            ["isMonaco"] = editor.IsMonaco,
            ["ready"] = monaco?.IsReady ?? false,
            ["fallbackReason"] = monaco?.FallbackReason,
            ["hasFocus"] = editor.HasFocus,
            ["text"] = editor.Text.Length <= 1024 * 1024 ? editor.Text : null,
            ["length"] = editor.Text.Length,
            ["caret"] = editor.CaretIndex,
            ["line"] = editor.CaretPosition.Line,
            ["column"] = editor.CaretPosition.Column,
            ["automationName"] = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(monaco?.WebView ?? (Microsoft.UI.Xaml.DependencyObject)editor.View),
            ["lastCompletion"] = monaco?.LastCompletion is { } c ? new JsonArray([.. c.Items.Select(i => (JsonNode?)new JsonObject { ["label"] = i.Label, ["detail"] = i.Detail })]) : null,
        };

        async Task<JsonNode?> EvalAsync(string script)
        {
            if (monaco is null)
            {
                return null;
            }

            await WaitUntilForTestAsync(() => monaco.IsReady || !monaco.IsMonaco, TimeSpan.FromSeconds(60));
            string? json = await monaco.EvaluateAsync(script);
            if (json is null)
            {
                return null;
            }

            JsonNode? node = JsonNode.Parse(json);

            // JSON.stringify の結果 (文字列) はもう一度読む
            return node is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 && s[0] is '{' or '[' ? JsonNode.Parse(s) : node;
        }
    }

    private static string Js(JsonNode? flag) => flag?.GetValue<bool>() == true ? "true" : "false";

    /// <summary>補完の一覧 (見えている行) と、選んだ候補の説明。</summary>
    private static string SuggestScript(string focus) => $$"""
        (async () => {
          const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
          const rows = () => [...document.querySelectorAll('.suggest-widget .monaco-list-row')];
          // 一覧の行が出るまで待つ (最初は言語サービスの worker の起動を待つ)
          for (let i = 0; i < 400 && rows().length === 0; i++) { await sleep(50); }
          const want = {{focus}};
          let details = null;
          if (want) {
            const e = hexEditor.editor;
            for (let i = 0; i < 200; i++) {
              const f = document.querySelector('.suggest-widget .monaco-list-row.focused .label-name');
              if (f && f.textContent === want) { break; }
              e.trigger('test', 'selectNextSuggestion', {});
              await sleep(10);
            }
            e.trigger('test', 'toggleSuggestionDetails', {});
            for (let i = 0; i < 100; i++) {
              const d = document.querySelector('.suggest-details');
              if (d && d.textContent && d.textContent.trim().length > 0 && getComputedStyle(d).display !== 'none') { details = d.textContent; break; }
              await sleep(50);
            }
          }
          const widget = document.querySelector('.suggest-widget');
          return JSON.stringify({
            visible: !!widget && getComputedStyle(widget).display !== 'none' && getComputedStyle(widget).visibility !== 'hidden' && rows().length > 0,
            rows: rows().map((r) => ({
              label: (r.querySelector('.label-name') || {}).textContent || '',
              detail: (r.querySelector('.details-label') || {}).textContent || '',
              aria: r.getAttribute('aria-label') || '',
            })),
            focused: (document.querySelector('.suggest-widget .monaco-list-row.focused .label-name') || {}).textContent || null,
            details,
          });
        })()
        """;

    /// <summary>JavaScript の補完の候補 (TypeScript の言語サービス。Monaco の一覧と同じ元) と、候補の説明。</summary>
    private static string JsCompletionsScript(string name) => $$"""
        (async () => {
          const ts = hexEditor.monaco.typescript || hexEditor.monaco.languages.typescript;
          const m = hexEditor.model;
          const offset = m.getOffsetAt(hexEditor.editor.getPosition());
          const worker = await (await ts.getJavaScriptWorker())(m.uri);
          const list = await worker.getCompletionsAtPosition(m.uri.toString(), offset);
          const names = list ? list.entries.map((e) => e.name) : [];
          let detail = null;
          const want = {{name}};
          if (want) {
            const d = await worker.getCompletionEntryDetails(m.uri.toString(), offset, want);
            if (d) {
              detail = {
                signature: (d.displayParts || []).map((p) => p.text).join(''),
                documentation: (d.documentation || []).map((p) => p.text).join(''),
              };
            }
          }
          return JSON.stringify({ names, detail });
        })()
        """;
}
#endif
