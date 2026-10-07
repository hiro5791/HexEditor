using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.I18n;

/// <summary>地域設定に依存しない比較と解釈 (UI-45 の仕様 4・5)。</summary>
public sealed class RegionalFormatTests
{
    /// <summary>
    /// TC-UI-45-03 の Core 側: 地域設定がトルコ (tr-TR。i の大文字は İ) でも、大文字・小文字を区別しない比較が同じ結果になる。
    /// コマンドパレット (UI-17、フェーズ 1) はまだないため、今ある大文字・小文字を区別しない比較 (テキストの検索の
    /// 「大文字・小文字を区別しない」と、入力式の名前・単位) で確かめる。コマンドパレットを作ったら UI テストを加える。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-UI-45-03")]
    public void Case_insensitive_comparisons_do_not_depend_on_the_turkish_culture()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("İNFO", "info".ToUpper(CultureInfo.CurrentCulture)); // 前提: 地域設定の大文字化では I にならない。

            // テキストの検索 (ASCII、大文字・小文字を区別しない): INFO と info の結果が同じで、空ではない。
            byte[] data = Encoding.ASCII.GetBytes("..INFO..info..Info..iNfO..");
            using var doc = new Document(new MemoryByteSource(data), Options());
            var options = new TextSearchOptions { CaseSensitive = false };
            long[] upper = Find(doc, SearchPattern.FromText("INFO", Encoding.ASCII, options));
            long[] lower = Find(doc, SearchPattern.FromText("info", Encoding.ASCII, options));
            Assert.NotEmpty(upper);
            Assert.Equal([2, 8, 14, 20], upper);
            Assert.Equal(upper, lower);

            // 入力式の名前と単位: 大文字・小文字のどちらでも同じ値。
            var context = new Context();
            foreach ((string a, string b) in new[] { ("END-1", "end-1"), ("SEL.LEN", "sel.len"), ("CUR+1K", "cur+1k"), ("SECTOR", "sector") })
            {
                Assert.Equal(ExpressionEvaluator.Evaluate(a, context), ExpressionEvaluator.Evaluate(b, context));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static long[] Find(Document doc, SearchPattern pattern) =>
        [.. SearchEngine.FindAll(doc.Current, pattern).Matches.Select(m => m.Offset)];

    private sealed class Context : IExpressionContext
    {
        public long Cursor => 0x40;

        public long Length => 0x1000;

        public long SelectionStart => 0x10;

        public long SelectionLength => 16;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}
