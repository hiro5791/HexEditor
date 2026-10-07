using System.Reflection;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-01 データソースの抽象化。</summary>
public sealed class SourceTests
{
    private static FakeByteSource Source4K(SourceCapabilities caps) =>
        new(4096, (o, s) => TestDataCatalog.Sequence(o, s), caps);

    [Fact]
    [Trait(TC, "TC-ENG-01-01")]
    public void CapabilityFlagsControlEditingAndSaving()
    {
        // 手順 1・2: CanResize が偽なら挿入・削除ができず、長さは変わらない。
        using (var fixedLength = new Document(Source4K(SourceCapabilities.CanWrite), Options()))
        {
            Assert.False(fixedLength.CanResize);
            Assert.Throws<FixedLengthException>(() => fixedLength.Insert(0x10, [0xAA]));
            Assert.Throws<FixedLengthException>(() => fixedLength.Delete(0x10, 1));
            Assert.Throws<FixedLengthException>(() => fixedLength.InsertCopy(0, 0x10, 4));
            Assert.Equal(4096, fixedLength.Length);
        }

        // 手順 3: CanWrite が偽なら保存できない (名前を付けて保存だけ)。
        using (var readOnly = new Document(Source4K(SourceCapabilities.CanResize), Options()))
        {
            readOnly.Overwrite(0, [0x11]);
            Assert.True(readOnly.CanResize);
            Assert.False(readOnly.CanSave);
        }

        // 手順 4: 両方真ならすべてできる。
        using var full = new Document(Source4K(SourceCapabilities.CanResize | SourceCapabilities.CanWrite), Options());
        Assert.True(full.CanResize);
        Assert.True(full.CanSave);
        full.Insert(0x10, [0xAA]);
        full.Delete(0x10, 1);
        Assert.Equal(4096, full.Length);
    }

    [Fact]
    [Trait(TC, "TC-ENG-01-02")]
    public void PartiallyUnreadableSourceReturnsReadableBytesAndRanges()
    {
        var source = new FakeByteSource(TestDataCatalog.MiB, (o, s) => TestDataCatalog.Sequence(o, s), SourceCapabilities.CanResize);
        source.BadRanges.Add(new UnreadableRange(0x1000, 0x1000, UnreadableReason.IoError, 23));
        source.BadRanges.Add(new UnreadableRange(0x3000, 0x200, UnreadableReason.AccessDenied, 5));
        using var doc = new Document(source, Options());

        // 手順 1
        byte[] buffer = new byte[0x4000];
        ReadResult r1 = doc.Current.Read(0, buffer);
        Assert.Equal(0x4000, r1.BytesReturned);
        Assert.Equal(
            [new UnreadableRange(0x1000, 0x1000, UnreadableReason.IoError, 23), new UnreadableRange(0x3000, 0x200, UnreadableReason.AccessDenied, 5)],
            r1.Unreadable);
        byte[] expected = new byte[0x4000];
        TestDataCatalog.Sequence(0, expected);
        foreach ((int from, int to) in new[] { (0, 0x1000), (0x2000, 0x3000), (0x3200, 0x4000) })
        {
            Assert.Equal(expected[from..to], buffer[from..to]);
        }

        // 手順 2: 全体が読めない範囲
        ReadResult r2 = doc.Current.Read(0x1800, new byte[0x100]);
        Assert.Equal([new UnreadableRange(0x1800, 0x100, UnreadableReason.IoError, 23)], r2.Unreadable);

        // 手順 3: 末尾を越える範囲
        byte[] tail = new byte[0x200];
        ReadResult r3 = doc.Current.Read(0xFFF00, tail);
        Assert.Equal(0x100, r3.BytesReturned);
        Assert.Empty(r3.Unreadable);
        TestDataCatalog.Sequence(0xFFF00, expected.AsSpan(0, 0x100));
        Assert.Equal(expected[..0x100], tail[..0x100]);
    }

    [Fact]
    [Trait(TC, "TC-ENG-01-03")]
    public void ConcurrentReadsFromEightThreadsAreCorrect()
    {
        string randomPath = TestDataCatalog.Get("TD-RANDOM-16M");
        byte[] reference = File.ReadAllBytes(randomPath);
        using FileByteSource random = FileByteSource.Open(randomPath);
        using FileByteSource sparse = FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G"));
        long[] positions = [(1L << 31) - 8, 1L << 31, (1L << 32) - 8, 1L << 32, sparse.Length - 16];

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, thread =>
        {
            var rng = new Random(1000 + thread);
            byte[] buffer = new byte[1024 * 1024];
            for (int i = 0; i < 10_000; i++)
            {
                long offset = rng.NextInt64(random.Length);
                int length = (int)Math.Min(rng.Next(1, buffer.Length + 1), random.Length - offset);
                ReadResult r = random.Read(offset, buffer.AsSpan(0, length));
                Assert.True(r.IsComplete);
                Assert.True(buffer.AsSpan(0, length).SequenceEqual(reference.AsSpan((int)offset, length)));
            }

            byte[] small = new byte[16];
            for (int i = 0; i < 1000; i++)
            {
                foreach (long p in positions)
                {
                    sparse.Read(p, small);
                    Assert.True(small.AsSpan().SequenceEqual(ExpectedSparse(p)));
                }
            }
        });
    }

    [Fact]
    [Trait(TC, "TC-ENG-01-04")]
    public void CoreDoesNotDependOnWinUI()
    {
        Assembly core = typeof(Document).Assembly;
        string[] forbidden = ["Microsoft.UI", "Microsoft.WindowsAppSDK", "Windows.UI.Xaml", "Microsoft.WinUI"];
        foreach (AssemblyName reference in core.GetReferencedAssemblies())
        {
            Assert.DoesNotContain(forbidden, f => reference.Name!.StartsWith(f, StringComparison.Ordinal));
        }

        foreach (Type type in core.GetTypes())
        {
            Assert.DoesNotContain(forbidden, f => type.Namespace?.StartsWith(f, StringComparison.Ordinal) == true);
        }

        string csproj = File.ReadAllText(FindRepoFile("src/HexEditor.Core/HexEditor.Core.csproj"));
        Assert.DoesNotContain("WindowsAppSDK", csproj);
        Assert.DoesNotContain("UseWinUI", csproj);
        Assert.DoesNotContain("HexEditor.App", csproj);
    }

    /// <summary>TD-SPARSE-100G の 16 バイトの期待値 (目印、または 00)。</summary>
    internal static byte[] ExpectedSparse(long position)
    {
        long length = 100 * TestDataCatalog.GiB;
        var markers = new SortedSet<long> { 0, length - TestDataCatalog.MarkerLength };
        for (long p = 0; p < length; p += TestDataCatalog.GiB)
        {
            markers.Add(p);
        }

        foreach (long a in new[] { 1L << 31, 1L << 32 })
        {
            markers.Add(a - TestDataCatalog.MarkerLength);
            markers.Add(a);
        }

        byte[] result = new byte[16];
        foreach (long m in markers.GetViewBetween(position - TestDataCatalog.MarkerLength, position + 16))
        {
            byte[] marker = TestDataCatalog.Marker(m);
            for (int i = 0; i < marker.Length; i++)
            {
                long at = m + i - position;
                if (at >= 0 && at < 16)
                {
                    result[at] = marker[i];
                }
            }
        }

        return result;
    }

    internal static string FindRepoFile(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }
}
