using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>テストデータの生成 (TestDataCatalog) の確認。10 GiB のものは同じ作り方を小さな長さで確かめる。</summary>
public sealed class TestDataGeneratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-testdata").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void FindRandomDataHasNoMatchesExceptTheTail()
    {
        // TD-FIND-RANDOM-10G の作り方で 3 MiB を作る。乱数ではめったに一致しないため、4 つの並びをチャンクの境目を含む位置に埋め込む。
        const long Length = 3 * TestDataCatalog.MiB;
        long[] at = [0x100, TestDataCatalog.MiB - 3, 2 * TestDataCatalog.MiB - 1, 2 * TestDataCatalog.MiB + 0x40, Length - 20];
        byte[][] planted =
        [
            [0x48, 0x45, 0x58, 0x45, 0x4E, 0x44, 0x21, 0x21],
            [0x48, 0x45, 0x99, 0x45, 0x4E, 0x44],
            [0x4A, 0x45, 0x58, 0x45, 0x4E, 0x4B],
            [0x13, 0x45, 0x58, 0x77, 0x4E, 0x44],
            [0x00, 0x45, 0x58, 0x00, 0x4E, 0x44],
        ];
        string path = Path.Combine(_dir, "find.bin");
        TestDataCatalog.WriteFindRandom(path, Length, (offset, chunk) =>
        {
            for (int k = 0; k < at.Length; k++)
            {
                for (int b = 0; b < planted[k].Length; b++)
                {
                    long p = at[k] + b - offset;
                    if (p >= 0 && p < chunk.Length)
                    {
                        chunk[(int)p] = planted[k][b];
                    }
                }
            }
        });

        using var doc = new Document(FileByteSource.Open(path), Options());
        Assert.Equal(TestDataCatalog.FindRandomTail, Read(doc.Current, Length - 8, 8));
        foreach (string hex in new[] { "48 45 58 45 4E 44 21 21", "48 45 ?? 45 4E 44", "4? 45 58 45 4E 4?", "?? 45 58 ?? 4E 44" })
        {
            SearchResults found = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex(hex), new SearchOptions { IncludeOverlapping = true });
            Assert.All(found.Matches, m => Assert.True(m.Offset >= Length - 8, $"{hex}: 0x{m.Offset:X} に一致が残っています"));
        }
    }
}
