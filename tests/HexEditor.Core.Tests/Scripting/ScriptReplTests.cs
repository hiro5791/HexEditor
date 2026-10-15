using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Repl;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// スクリプトコンソール (AUTO-23) の実行の部分: 入力ごとの実行 (Undo の単位・許可)、セッションの変数、結果の表示用の値。
/// 入力欄・履歴・画面は UI のテストで確かめる。
/// </summary>
public sealed class ScriptReplTests : IDisposable
{
    private readonly ScriptTestHost _host = new();
    private readonly JsReplSession _repl = new();

    public void Dispose()
    {
        _repl.Dispose();
        _host.Dispose();
    }

    private async Task<(ScriptRunResult Result, ReplValue? Value)> InputAsync(string code)
    {
        ReplValue? value = null;
        ScriptRunResult result = await _host.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Code = code,
            Origin = ScriptOrigin.Console,
            DisplayName = "console",
            Repl = _repl,
            OnReplResult = v => value = v,
        }, _host.Env).WaitAsync(TimeSpan.FromSeconds(60));
        return (result, value);
    }

    private static byte[] Sequence() => [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    [Fact]
    [Trait("TC", "TC-AUTO-23-01")]
    public async Task Bytes_are_shown_as_a_hex_dump_and_variables_persist_between_inputs()
    {
        _host.Open(Sequence());

        (ScriptRunResult result, ReplValue? bytes) = await InputAsync("doc.readBytes(0, 16)");
        ScriptTestHost.AssertSucceeded(result);
        Assert.Equal(ReplValueKind.Bytes, bytes!.Kind);
        Assert.Contains("00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F", bytes.Text);

        ScriptTestHost.AssertSucceeded((await InputAsync("let x = 40;")).Result);
        (result, ReplValue? sum) = await InputAsync("x + 2");
        ScriptTestHost.AssertSucceeded(result);
        Assert.Equal("42", sum!.Text);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-23-01")]
    public async Task A_large_byte_array_is_cut_at_256_bytes_with_the_length()
    {
        (_, ReplValue? value) = await InputAsync("new Uint8Array(1000)");
        Assert.Equal(1000, value!.ByteLength);
        Assert.Equal(16, value.Text.Split('\n').Count(l => l.Length > 0 && char.IsAsciiHexDigit(l[0])));
        Assert.Contains("1,000", value.Text.Split('\n')[^1].Replace(" ", ",").Replace("1000", "1,000"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-23-01")]
    public async Task BigInt_is_shown_in_decimal_and_hex_and_objects_are_expandable()
    {
        (_, ReplValue? big) = await InputAsync("255n");
        Assert.Equal("255n", big!.Text);
        Assert.Equal("0xFF", big.Hex);
        Assert.Equal("255n (0xFF)", big.Display);

        (_, ReplValue? obj) = await InputAsync("({ a: 1, b: [1, 2], c: { d: 'x' } })");
        Assert.Equal(ReplValueKind.Object, obj!.Kind);
        Assert.True(obj.Expandable);
        Assert.Equal(["a", "b", "c"], obj.Children.Select(c => c.Name));
        Assert.Equal(2, obj.Children[1].Value.Children.Count);
        Assert.Equal("\"x\"", obj.Children[2].Value.Children[0].Value.Text);
    }

    [Fact]
    public async Task An_exception_is_reported_and_the_session_continues()
    {
        ScriptTestHost.AssertSucceeded((await InputAsync("var kept = 7;")).Result);
        (ScriptRunResult failed, _) = await InputAsync("throw new Error('boom')");
        Assert.Equal(ScriptRunStatus.Failed, failed.Status);
        Assert.Contains("boom", failed.Message);
        (ScriptRunResult result, ReplValue? value) = await InputAsync("kept * 2");
        ScriptTestHost.AssertSucceeded(result);
        Assert.Equal("14", value!.Text);
    }

    [Fact]
    public async Task A_syntax_error_does_not_end_the_session()
    {
        (ScriptRunResult failed, _) = await InputAsync("let = ;");
        Assert.Equal(ScriptRunStatus.Failed, failed.Status);
        ScriptTestHost.AssertSucceeded((await InputAsync("1 + 1")).Result);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-23-03")]
    public async Task Each_input_is_one_undo_unit_and_fill_is_awaited()
    {
        byte[] original = Sequence();
        CoreDocumentHost doc = _host.Open(original);

        (ScriptRunResult result, ReplValue? value) = await InputAsync("doc.fill({start: 0, length: 0x80}, \"FF\")");
        ScriptTestHost.AssertSucceeded(result);
        Assert.Equal("undefined", value!.Text);
        Assert.Equal(0xFF, _host.OnUi(() => ReadByte(doc, 0)));
        Assert.Equal(0xFF, _host.OnUi(() => ReadByte(doc, 0x7F)));

        // 1 回の Undo で戻る (入力 1 回 = 1 つの Undo の単位)。
        _host.OnUi(() => doc.Document.Undo());
        Assert.Equal(0, _host.OnUi(() => ReadByte(doc, 0)));
        Assert.Equal(0x7F, _host.OnUi(() => ReadByte(doc, 0x7F)));
        Assert.False(_host.OnUi(() => doc.Document.History.CanUndo));
        Assert.Empty(_host.Ui.TrustPrompts);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-23-01")]
    public async Task Doc_follows_the_active_document_for_each_input()
    {
        _host.Open([1, 2, 3], "a.bin");
        (_, ReplValue? first) = await InputAsync("doc.length");
        Assert.Equal("3", first!.Text);
        _host.Open([1, 2, 3, 4, 5], "b.bin");
        (_, ReplValue? second) = await InputAsync("doc.length");
        Assert.Equal("5", second!.Text);
    }

    [Fact]
    public async Task Top_level_await_in_an_expression_is_supported()
    {
        _host.Open(Sequence());
        (ScriptRunResult result, ReplValue? value) = await InputAsync("await Promise.resolve(5)");
        ScriptTestHost.AssertSucceeded(result);
        Assert.Equal("5", value!.Text);
    }

    [Fact]
    public async Task The_execution_limit_is_counted_per_input()
    {
        _host.Limits = new ScriptLimits { Time = TimeSpan.FromSeconds(2) };
        for (int i = 0; i < 3; i++)
        {
            // 1 回ごとに 1 秒に満たない計算を 3 回。合計が上限を超えても失敗しない。
            (ScriptRunResult result, _) = await InputAsync("(() => { let s = 0; for (let i = 0; i < 300000; i++) s += i; return s; })()");
            ScriptTestHost.AssertSucceeded(result);
        }
    }

    [Fact]
    public async Task Reset_gives_a_new_session_without_the_old_variables()
    {
        ScriptTestHost.AssertSucceeded((await InputAsync("var old = 1;")).Result);
        _repl.Dispose();
        var fresh = new JsReplSession();
        try
        {
            ReplValue? value = null;
            ScriptRunResult result = await _host.Host.RunAndWaitAsync(new ScriptRunRequest
            {
                Code = "typeof old",
                Origin = ScriptOrigin.Console,
                DisplayName = "console",
                Repl = fresh,
                OnReplResult = v => value = v,
            }, _host.Env).WaitAsync(TimeSpan.FromSeconds(60));
            ScriptTestHost.AssertSucceeded(result);
            Assert.Equal("\"undefined\"", value!.Text);
        }
        finally
        {
            fresh.Dispose();
        }
    }

    private static int ReadByte(CoreDocumentHost doc, long offset)
    {
        return CSharpScriptTests.ReadAll(doc)[offset];
    }
}
