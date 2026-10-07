using HexEditor.Core.Operations;

namespace HexEditor.Core.Tests.Operations;

/// <summary>ENG-09 長時間処理の基盤 (Core の部分)。</summary>
public sealed class OperationCenterTests
{
    [Fact]
    public async Task CancellationStopsWorkQuickly()
    {
        var center = new OperationCenter();
        LongRunningOperation? captured = null;
        Task task = center.RunAsync("検索", OperationKind.ReadOnly, null, 1_000_000, op =>
        {
            captured = op;
            for (long i = 0; ; i += 1024)
            {
                Thread.Sleep(1);
                op.Report(i);
            }
        });

        while (captured is null || captured.State != OperationState.Running)
        {
            await Task.Delay(5);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        captured.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(OperationState.Cancelled, captured.State);
        Assert.Empty(center.Active);
        Assert.Single(center.History);
    }

    [Fact]
    public async Task ModifyingOperationsOnSameDocumentRunOneAtATime()
    {
        var center = new OperationCenter();
        object document = new();
        int running = 0;
        int maxRunning = 0;
        var locks = new List<bool>();

        async Task Work(LongRunningOperation op)
        {
            int now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
        }

        await Task.WhenAll(
            center.RunAsync("置換 1", OperationKind.ModifiesDocument, document, null, Work, l => { lock (locks) { locks.Add(l); } }),
            center.RunAsync("保存", OperationKind.WritesExternal, document, null, Work, l => { lock (locks) { locks.Add(l); } }));

        Assert.Equal(1, maxRunning);
        Assert.Equal([true, false, true, false], locks);
    }

    [Fact]
    public async Task FailureIsRecorded()
    {
        var center = new OperationCenter();
        await Assert.ThrowsAsync<IOException>(() => center.RunAsync("保存", OperationKind.WritesExternal, null, null,
            _ => throw new IOException("ディスクがいっぱいです")));
        LongRunningOperation op = Assert.Single(center.History);
        Assert.Equal(OperationState.Failed, op.State);
        Assert.IsType<IOException>(op.Error);
    }

    [Fact]
    public async Task FractionAndRemainingTimeNeedTotal()
    {
        var center = new OperationCenter();
        LongRunningOperation? op = null;
        await center.RunAsync("ハッシュ", OperationKind.ReadOnly, null, null, o => { op = o; return Task.CompletedTask; });
        Assert.NotNull(op);
        Assert.Null(op.Fraction);
        Assert.Null(op.EstimatedRemaining);
    }
}
