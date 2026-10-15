using System.Globalization;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Scripting.Macros;

/// <summary>繰り返し再生の条件 (AUTO-28 の仕様 2)。</summary>
public enum MacroRepeatMode
{
    /// <summary>回数を指定 (1〜1,000,000)。</summary>
    Count,

    /// <summary>ファイルの末尾に達するまで (カーソルが末尾に達した、または移動できなくなった時点で止める)。</summary>
    UntilEnd,

    /// <summary>検索結果ごと (結果一覧の各行の位置にカーソルを置いて 1 回ずつ実行する)。</summary>
    PerResult,

    /// <summary>選択範囲の中で (カーソルが選択範囲の外に出たら止める)。</summary>
    InSelection,
}

/// <summary>再生の指定。</summary>
public sealed record MacroPlayback(MacroRepeatMode Mode = MacroRepeatMode.Count, int Count = 1, IReadOnlyList<long>? ResultStarts = null)
{
    public const int MaxCount = 1_000_000;

    /// <summary>1 回だけ再生する。</summary>
    public static MacroPlayback Once { get; } = new();

    /// <summary>範囲外の回数を直す (1〜1,000,000)。</summary>
    public int ClampedCount => Math.Clamp(Count, 1, MaxCount);
}

/// <summary>
/// マクロの再生 (AUTO-28)。マクロはスクリプトと同じ仕組み (AUTO-01) で実行するので、繰り返し再生は、マクロの本文を関数に包んで
/// 繰り返し呼ぶスクリプトにして 1 回の実行にする (全体が 1 つの Undo 単位・処理センターの項目・停止ボタンになる。仕様 3・4)。
/// 包んだ後も行番号は変わらない。各操作の先頭に「何番目の操作か」を足すので、失敗したときに何回目のどの操作かを示せる (仕様 5)。
/// </summary>
public static partial class MacroPlayer
{
    /// <summary>
    /// マクロの本文を繰り返しのスクリプトにする。信頼の確認 (AUTO-34) は元のマクロの本文で行い、その後にこの変換を適用する
    /// (<see cref="ScriptRunRequest.CodeWrapper"/>)。
    /// </summary>
    public static string Wrap(string macro) =>
        // 関数の開き括弧を 1 行目の先頭に置く: 1 行目はコメント (メタデータの開始) なので、行番号がずれない。
        "let __op = 0; const __macro = async (__index) => { " + Instrument(macro.TrimEnd('\r', '\n')) + "\n};\n" + Driver;

    /// <summary>操作のコメント行 (メタデータ以外の <c>//</c> の行) の次のコード行に <c>__op = n;</c> を足す (同じ行の中なので行番号は変わらない)。</summary>
    private static string Instrument(string macro)
    {
        string[] lines = macro.Split('\n');
        bool header = macro.Contains("==/HexEditorScript==", StringComparison.Ordinal);
        int op = 0;
        bool pending = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (header)
            {
                header = !t.Contains("==/HexEditorScript==", StringComparison.Ordinal);
                continue;
            }

            if (t.StartsWith("//", StringComparison.Ordinal))
            {
                op++;
                pending = true;
            }
            else if (t.Length > 0 && pending)
            {
                lines[i] = "__op = " + op.ToString(CultureInfo.InvariantCulture) + "; " + lines[i];
                pending = false;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>再生の指定を <c>script.args.macro</c> にする。</summary>
    public static IReadOnlyDictionary<string, object?> Arguments(MacroPlayback playback) => new Dictionary<string, object?>
    {
        ["macro"] = new Dictionary<string, object?>
        {
            ["mode"] = playback.Mode switch
            {
                MacroRepeatMode.UntilEnd => "end",
                MacroRepeatMode.PerResult => "results",
                MacroRepeatMode.InSelection => "selection",
                _ => "count",
            },
            ["count"] = playback.ClampedCount,
            ["starts"] = playback.ResultStarts?.ToArray() ?? [],
        },
    };

    /// <summary>
    /// 失敗した操作の説明 (仕様 5: 何回目のどの操作で止まったか)。<paramref name="operation"/> は 1 から数えた操作の番号
    /// (エラーメッセージの <c>[macro 回:操作]</c>)。操作のコメントを名前にする。見つからなければ null。
    /// </summary>
    public static string? DescribeOperation(string macro, int operation)
    {
        bool header = macro.Contains("==/HexEditorScript==", StringComparison.Ordinal);
        int n = 0;
        foreach (string line in macro.Split('\n'))
        {
            string t = line.Trim();
            if (header)
            {
                header = !t.Contains("==/HexEditorScript==", StringComparison.Ordinal);
                continue;
            }

            if (t.StartsWith("//", StringComparison.Ordinal) && ++n == operation)
            {
                return t[2..].Trim();
            }
        }

        return null;
    }

    /// <summary>エラーメッセージに付いた回と操作の番号 (<c>[macro 3:2]</c>)。なければ (0, 0)。</summary>
    public static (int Iteration, int Operation) LocationOf(string? message)
    {
        Match m = LocationPattern().Match(message ?? "");
        return m.Success
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    [GeneratedRegex(@"\[macro (\d+):(\d+)\]")]
    private static partial Regex LocationPattern();

    /// <summary>繰り返しの本体。マクロの本文の関数 (<c>__macro</c>) を条件に従って呼ぶ。</summary>
    private const string Driver = """
        {
            const cfg = script.args.macro ?? { mode: "count", count: 1, starts: [] };
            const current = () => app.activeDocument;
            const report = (i, total) => {
                if (total > 0 && (i % 16 === 0 || i === total)) ui.progress.report(i / total, `${i} / ${total}`);
            };
            const run = async (i) => {
                __op = 0;
                try {
                    await __macro(i);
                } catch (e) {
                    if (e && e.name === "CanceledError") throw e;
                    if (e && typeof e.message === "string") e.message = `[macro ${i + 1}:${__op}] ` + e.message;
                    throw e;
                }
            };
            const stopped = () => script.signal.aborted;
            if (cfg.mode === "count") {
                for (let i = 0; i < cfg.count && !stopped(); i++) {
                    await run(i);
                    report(i + 1, cfg.count);
                }
            } else if (cfg.mode === "end") {
                let i = 0;
                for (;;) {
                    const d = current();
                    const before = d.cursor;
                    if (stopped() || d.length === 0 || (i > 0 && before >= d.length - 1)) break;
                    await run(i++);
                    const after = current().cursor;
                    ui.progress.report(Math.min(1, after / Math.max(1, current().length)), `${i}`);
                    if (after <= before || after >= current().length - 1) break;
                }
            } else if (cfg.mode === "results") {
                const total = cfg.starts.length;
                for (let i = 0; i < total && !stopped(); i++) {
                    current().cursor = cfg.starts[i];
                    await run(i);
                    report(i + 1, total);
                }
            } else if (cfg.mode === "selection") {
                const d0 = current();
                const start = d0.selection.start;
                const end = d0.selection.end;
                if (end > start) {
                    d0.cursor = start;
                    let i = 0;
                    for (;;) {
                        const d = current();
                        const before = d.cursor;
                        if (stopped() || before < start || before >= end) break;
                        await run(i++);
                        const after = current().cursor;
                        ui.progress.report(Math.min(1, (after - start) / Math.max(1, end - start)), `${i}`);
                        if (after <= before) break;
                    }
                }
            }
        }
        """;
}
