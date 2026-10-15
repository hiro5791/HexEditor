using HexEditor.Core.Scripting;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// メモリの上限のテスト。上限はプロセス全体のマネージドヒープの増加で数えるため、他のテストと並列に動かさない。
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class ScriptMemoryTests
{
    private static async Task<IReadOnlyList<string>> RunOk(ScriptTestHost t, string code)
    {
        ScriptRunResult r = await t.RunCodeAsync(code);
        ScriptTestHost.AssertSucceeded(r);
        return t.OutputText;
    }

    [Theory]
    [Trait("TC", "TC-AUTO-02-04")]
    [InlineData("const keep = []; for (let i = 0; i < 1024; i++) { const a = new Uint8Array(1024 * 1024); a.fill(1); keep.push(a); } console.log('not reached', keep.length);")]
    [InlineData("const a = new Uint8Array(1024 * 1024 * 1024); a.fill(7); console.log('not reached', a[5]);")]
    [InlineData("const a = []; for (let i = 0; i < 1e9; i++) { a.push({ i: i, s: 'x' + i }); } console.log('not reached');")]
    public async Task Allocating_one_gigabyte_ends_with_the_memory_limit(string code)
    {
        using var t = new ScriptTestHost(new ScriptLimits { MemoryBytes = 512L * 1024 * 1024 });
        ScriptRunResult r = await t.RunCodeAsync(code);
        Assert.True(r.Status == ScriptRunStatus.Failed, string.Join("\n", t.OutputText));
        Assert.Equal(ScriptFailure.Limit, r.Failure);
        Assert.Contains("メモリの上限 512 MB", r.Message);
        Assert.DoesNotContain("not reached", string.Join("\n", t.OutputText));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-09-03")]
    [Trait("Category", "Nightly")]
    public async Task Chunks_read_10_gb_within_the_memory_limit()
    {
        using var t = new ScriptTestHost(new ScriptLimits { MemoryBytes = 512L * 1024 * 1024, Time = null });
        t.Open(new VirtualByteSource(10L << 30), "sparse10g.bin");
        IReadOnlyList<string> output = await RunOk(t, """
            let total = 0, marks = 0;
            for await (const { offset, data } of doc.chunks({ start: 0, length: doc.length }, 16 * 1024 * 1024)) { total += data.length; if (data[0] === 0) marks++; }
            console.log("total=" + total);
            """);
        Assert.Contains("total=10737418240", output);
    }
}
