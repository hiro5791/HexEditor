using System.Diagnostics;
using HexEditor.Core.Disassembly;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// リモートのデータソース (ENG-40・ENG-41) の読み込みを UI スレッドで待たない (フェーズ 4 の監査。ENG-06 の受け入れ基準「UI スレッドが 50 ms 以上
/// ブロックされない」)。逆アセンブルのリスティングはキャッシュにない範囲で止まり、読み込みが届いたら作り直す。
/// </summary>
public sealed class RemoteReadTests
{
    /// <summary>許可するまで読み込みが終わらない、リモートのデータソース (読んだスレッドを記録する)。</summary>
    private sealed class GatedRemoteSource(byte[] data) : ByteSourceBase, IRemoteByteSource
    {
        public ManualResetEventSlim Gate { get; } = new(false);

        public HashSet<int> ReadThreads { get; } = [];

        public override string DisplayName => "remote";

        public override string Identity { get; } = "remote:" + Guid.NewGuid().ToString("N");

        public override long Length => data.Length;

        public override SourceCapabilities Capabilities => SourceCapabilities.IsVolatile;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            lock (ReadThreads)
            {
                ReadThreads.Add(Environment.CurrentManagedThreadId);
            }

            // テストが終わっても止まったままにならないよう、上限を付けて待つ。
            Gate.Wait(TimeSpan.FromSeconds(60));
            int n = ClampToLength(offset, buffer.Length);
            data.AsSpan((int)offset, n).CopyTo(buffer);
            return new ReadResult(n);
        }
    }

    [Fact]
    public async Task Disassembly_listing_does_not_wait_for_remote_reads()
    {
        byte[] code = [.. Enumerable.Repeat((byte)0x90, 4096)];
        var source = new GatedRemoteSource(code);
        using var doc = new Document(source, Options());
        using var listing = new DisassemblyListing(() => doc.Current, ExecutableImage.Empty, new DisasmViewOptions { ArchitectureId = DisasmArchitectures.X86_64 })
        {
            NonBlocking = true,
        };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        doc.DataLoaded += (_, _) => loaded.TrySetResult();

        // 読み込みが止まっていても、すぐに (行なしで) 戻り、読み込み待ちを示す。読み込みはこのスレッドでは行わない。
        long before = Stopwatch.GetTimestamp();
        IReadOnlyList<ListingLine> first = listing.Lines(0, 10);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(before);
        Assert.Empty(first);
        Assert.True(listing.HasPendingReads);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Lines waited {elapsed}");
        lock (source.ReadThreads)
        {
            Assert.DoesNotContain(Environment.CurrentManagedThreadId, source.ReadThreads);
        }

        // 読み込みが届いたら、作り直すと行ができる。
        source.Gate.Set();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        IReadOnlyList<ListingLine> lines = listing.Lines(0, 10);
        Assert.Equal(10, lines.Count);
        Assert.All(lines, l => Assert.Equal("nop", l.Instruction!.Mnemonic));
        Assert.False(listing.HasPendingReads);
    }
}
