using System.Globalization;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Api;

/// <summary>
/// <c>ui</c> (AUTO-16)。アプリの共通部品で表示し、タイトルにスクリプト名を付ける (仕様 3)。ヘッドレス実行では入力の API は
/// <c>--arg</c> の値を使い、なければ <see cref="Sdk.ArgumentError"/> (仕様 4)。利用者の入力待ちの時間は実行時間に数えない。
/// </summary>
internal sealed class ScriptUi(ScriptContext ctx) : IUi
{
    private IScriptUiService Ui => ctx.Environment.Ui;

    private async Task<T> WaitUserAsync<T>(Func<Task<T>> show)
    {
        ctx.ThrowIfStopped();
        using (ctx.Clock.Pause())
        {
            return await ctx.Dispatcher.InvokeAsync(show).ConfigureAwait(false);
        }
    }

    private string HeadlessValue(string name) =>
        ctx.Request.CommandLineArgs.TryGetValue(name, out string? value)
            ? value
            : throw new Sdk.ArgumentError(Msg.T($"ヘッドレスでは入力できません: {name}", $"Input is not available in headless runs: {name}"));

    private IExpressionContext Expressions() =>
        ctx.Dispatcher.Invoke(() => ctx.Environment.ActiveDocument is { } d ? (IExpressionContext)new View.EditorExpressionContext(d.Editor) : EmptyExpressionContext.Instance);

    public Task MessageAsync(string text, MessageOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        string kind = options?.Kind ?? "info";
        if (kind is not ("info" or "warning" or "error"))
        {
            throw new Sdk.ArgumentError(Msg.T($"kind は info・warning・error です: {kind}", $"kind must be info, warning or error: {kind}"));
        }

        bool modal = options?.Modal ?? false;
        await WaitUserAsync(async () =>
        {
            await Ui.MessageAsync(ctx.Name, text, kind, modal && !ctx.Headless).ConfigureAwait(true);
            return true;
        }).ConfigureAwait(false);
    });

    public Task<int> ConfirmAsync(string text, IReadOnlyList<string> buttons) => ScriptErrorMap.GuardAsync(async () =>
    {
        if (buttons.Count == 0)
        {
            throw new Sdk.ArgumentError(Msg.T("buttons を 1 つ以上指定してください。", "Specify at least one button in buttons."));
        }

        if (ctx.Headless)
        {
            string answer = HeadlessValue(text);
            int index = int.TryParse(answer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : IndexOf(buttons, answer);
            return index >= -1 && index < buttons.Count ? index : throw new Sdk.ArgumentError(Msg.T($"ボタンの番号か名前ではありません: {answer}", $"Not a button index or name: {answer}"));
        }

        return await WaitUserAsync(() => Ui.ConfirmAsync(ctx.Name, text, buttons)).ConfigureAwait(false);
    });

    private static int IndexOf(IReadOnlyList<string> buttons, string answer)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            if (string.Equals(buttons[i], answer, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -2;
    }

    private static readonly HashSet<string> Types = ["text", "number", "offset", "range", "hex", "choice", "checkbox"];

    private static string TypeOf(PromptOptions? o, bool form)
    {
        string type = o?.Type ?? "text";
        if (!Types.Contains(type) || (type == "checkbox" && !form))
        {
            throw new Sdk.ArgumentError(Msg.T($"入力欄の型が不明です: {type} (text number offset range hex choice{(form ? " checkbox" : "")})", $"Unknown field type: {type} (text number offset range hex choice{(form ? " checkbox" : "")})"));
        }

        if (type == "choice" && (o?.Choices is null || o.Choices.Count == 0))
        {
            throw new Sdk.ArgumentError(Msg.T("choice には choices を指定してください。", "Specify choices for choice."));
        }

        return type;
    }

    private static string? DefaultText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        Sdk.ByteRange r => $"0x{r.Start:X}:0x{r.Length:X}",
        byte[] bytes => Clipboard.HexText.Format(bytes),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public Task<object?> PromptAsync(string label, PromptOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        string type = TypeOf(options, form: false);
        IExpressionContext expressions = Expressions();
        if (ctx.Headless)
        {
            return ScriptParameters.ConvertValue(type, HeadlessValue(label), expressions, options?.Choices);
        }

        var field = new ScriptFormField("value", label, type, DefaultText(options?.Default), options?.Choices);
        IReadOnlyDictionary<string, string>? input = await WaitUserAsync(() => Ui.FormAsync(ctx.Name, null, [field],
            values => Validate(values, [field], expressions))).ConfigureAwait(false);
        return input is null ? null : ScriptParameters.ConvertValue(type, input["value"], expressions, options?.Choices);
    });

    public Task<IReadOnlyDictionary<string, object?>?> FormAsync(IReadOnlyList<FormField> fields) => ScriptErrorMap.GuardAsync(async () =>
    {
        if (fields.Count == 0)
        {
            throw new Sdk.ArgumentError(Msg.T("fields を 1 つ以上指定してください。", "Specify at least one entry in fields."));
        }

        var defs = new List<ScriptFormField>();
        foreach (FormField f in fields)
        {
            if (string.IsNullOrEmpty(f.Name))
            {
                throw new Sdk.ArgumentError(Msg.T("フォームの項目には name を指定してください。", "Specify name for each form field."));
            }

            if (defs.Any(d => d.Name == f.Name))
            {
                throw new Sdk.ArgumentError(Msg.T($"フォームの項目の name が重複しています: {f.Name}", $"Duplicate form field name: {f.Name}"));
            }

            defs.Add(new ScriptFormField(f.Name, f.Label ?? f.Name, TypeOf(f, form: true), DefaultText(f.Default), f.Choices));
        }

        IExpressionContext expressions = Expressions();
        IReadOnlyDictionary<string, string>? input;
        if (ctx.Headless)
        {
            input = defs.ToDictionary(d => d.Name, d => ctx.Request.CommandLineArgs.TryGetValue(d.Name, out string? v)
                ? v
                : d.Type == "checkbox" && d.Default is string def ? def : HeadlessValue(d.Name));
        }
        else
        {
            input = await WaitUserAsync(() => Ui.FormAsync(ctx.Name, null, defs, values => Validate(values, defs, expressions))).ConfigureAwait(false);
        }

        if (input is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (ScriptFormField d in defs)
        {
            result[d.Name] = ScriptParameters.ConvertValue(d.Type, input.TryGetValue(d.Name, out string? v) ? v : "", expressions, d.Choices);
        }

        return (IReadOnlyDictionary<string, object?>?)result;
    });

    private static (string Field, string Message)? Validate(IReadOnlyDictionary<string, string> values, IReadOnlyList<ScriptFormField> fields,
        IExpressionContext expressions)
    {
        foreach (ScriptFormField f in fields)
        {
            if (values.TryGetValue(f.Name, out string? text))
            {
                try
                {
                    _ = ScriptParameters.ConvertValue(f.Type, text, expressions, f.Choices);
                }
                catch (ScriptError ex)
                {
                    return (f.Name, ex.Message);
                }
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<string>?> PickCoreAsync(string kind, PickOptions? options, bool write)
    {
        if (ctx.Headless)
        {
            string value = HeadlessValue(kind);
            IReadOnlyList<string> paths = kind == "openMany" ? value.Split(';', StringSplitOptions.RemoveEmptyEntries) : [value];
            foreach (string p in paths)
            {
                ctx.AddPickedPath(p, write);
            }

            return paths;
        }

        IReadOnlyList<string>? picked = await WaitUserAsync(() => Ui.PickAsync(ctx.Name, kind, options ?? new PickOptions())).ConfigureAwait(false);
        if (picked is not null)
        {
            // 利用者が選んだパスは、許可がなくてもこの実行中に限り扱える (AUTO-34 の仕様 3)。
            foreach (string p in picked)
            {
                ctx.AddPickedPath(p, write);
            }
        }

        return picked;
    }

    public Task<string?> PickFileAsync(PickOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
        (await PickCoreAsync("open", options, write: false).ConfigureAwait(false))?.FirstOrDefault());

    public Task<IReadOnlyList<string>?> PickFilesAsync(PickOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
        PickCoreAsync("openMany", options, write: false));

    public Task<string?> SaveFileAsync(PickOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
        (await PickCoreAsync("save", options, write: true).ConfigureAwait(false))?.FirstOrDefault());

    public Task<string?> PickFolderAsync(PickOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
        (await PickCoreAsync("folder", options, write: true).ConfigureAwait(false))?.FirstOrDefault());

    public IUiProgress Progress { get; } = new ScriptUiProgress(ctx);

    public void Status(string text) => ctx.Dispatcher.Invoke(() => Ui.Status(text));

    public void ShowResults(string title, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string>? columns = null)
    {
        IReadOnlyList<string> cols = columns ?? [.. rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal)];
        ctx.Dispatcher.Invoke(() => Ui.ShowResults(ctx.Name, title, rows, cols, ctx.Environment.ActiveDocument));
    }

    public void Output(string text, string? kind = null) => ctx.Output(kind switch
    {
        null or "info" => ScriptOutputKind.Info,
        "warning" => ScriptOutputKind.Warning,
        "error" => ScriptOutputKind.Error,
        _ => throw new Sdk.ArgumentError(Msg.T($"kind は info・warning・error です: {kind}", $"kind must be info, warning or error: {kind}")),
    }, text);
}

/// <summary><c>ui.progress</c> (AUTO-05 の仕様 5)。処理センターの「スクリプト: 名前」の進捗を更新する。</summary>
internal sealed class ScriptUiProgress(ScriptContext ctx) : IUiProgress
{
    private const long Scale = 1_000_000;

    public void Report(double fraction, string? text = null)
    {
        if (double.IsNaN(fraction) || fraction < 0 || fraction > 1)
        {
            throw new Sdk.ArgumentError(Msg.T($"fraction は 0〜1 です: {fraction}", $"fraction must be 0 to 1: {fraction}"));
        }

        if (ctx.RunOperation is LongRunningOperation op)
        {
            op.SetTotal(Scale);
            try
            {
                op.Report((long)(fraction * Scale));
            }
            catch (OperationCanceledException)
            {
                // 停止はスクリプト全体のキャンセルで伝わる。
            }

            op.ReportDetail(text);
        }

        Reported?.Invoke(fraction, text);
    }

    /// <summary>報告を受けた (ヘッドレス実行の進捗表示・テスト)。</summary>
    public event Action<double, string?>? Reported;
}
