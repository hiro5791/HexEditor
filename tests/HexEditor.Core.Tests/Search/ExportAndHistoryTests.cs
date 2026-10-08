using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Settings;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>結果のエクスポート (FIND-21) と検索履歴 (FIND-28)。</summary>
public sealed class ExportAndHistoryTests
{
    /// <summary>TD-FIND-JA-UTF8: 0x10〜0x27 に UTF-8 の「日本語のテキスト」。</summary>
    private static byte[] JaUtf8()
    {
        byte[] data = new byte[64];
        Encoding.UTF8.GetBytes("日本語のテキスト").CopyTo(data, 0x10);
        return data;
    }

    /// <summary>TD-FIND-HITS-100: k × 0x20 (k = 0〜99) に `AB CD`。</summary>
    private static byte[] Hits100()
    {
        byte[] data = new byte[4096];
        for (int k = 0; k < 100; k++)
        {
            data[k * 0x20] = 0xAB;
            data[(k * 0x20) + 1] = 0xCD;
        }

        return data;
    }

    private static string TempFile(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "HexEditorTests", "export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    [Fact]
    [Trait(TC, "TC-FIND-21-03")]
    public void CsvIsUtf8WithBomAndBothOffsetColumns()
    {
        using Document doc = Doc(JaUtf8());
        Encoding utf8 = TextEncodings.Get(TextEncodingId.Utf8);
        using SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromText("日本", utf8));

        // 手順 1: CSV に書き出す。
        string path = TempFile("r.csv");
        using (FileStream stream = File.Create(path))
        {
            SearchResultsExporter.Export(new SearchResultRowFactory(results, doc.Current, utf8), stream, ExportFormat.Csv);
        }

        // 手順 2: 先頭は BOM。全体が正しい UTF-8 で、テキストの列に「日本」。
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes[3..]);
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        // 手順 3: カンマ区切り。オフセットは 10 進 (16) と 16 進 (0x10)。
        string[] header = lines[0].Split(',');
        string[] row = lines[1].Split(',');
        Assert.Equal(header.Length, row.Length);
        Assert.Equal("16", row[Array.IndexOf(header, "Offset")]);
        Assert.Equal("0x10", row[Array.IndexOf(header, "Offset (hex)")]);
        Assert.Equal("6", row[Array.IndexOf(header, "Length")]);
        Assert.Equal("日本", row[Array.IndexOf(header, "Data (text)")]);
        Assert.Equal("E6 97 A5 E6 9C AC", row[Array.IndexOf(header, "Data (hex)")]);
    }

    [Fact]
    [Trait(TC, "TC-FIND-21-05")]
    public void JsonContainsOffsetsLengthAndHex()
    {
        using Document doc = Doc(Hits100());
        using SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("AB CD"));
        string path = TempFile("r.json");
        using (FileStream stream = File.Create(path))
        {
            SearchResultsExporter.Export(new SearchResultRowFactory(results, doc.Current, Encoding.ASCII), stream, ExportFormat.Json);
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement[] items = [.. json.RootElement.GetProperty("results").EnumerateArray()];
        Assert.Equal(100, items.Length);
        for (int k = 0; k < 100; k++)
        {
            Assert.Equal(k * 0x20, items[k].GetProperty("offset").GetInt64());
            Assert.Equal($"0x{k * 0x20:X}", items[k].GetProperty("offsetHex").GetString());
            Assert.Equal(2, items[k].GetProperty("length").GetInt64());
            Assert.Equal("AB CD", items[k].GetProperty("hex").GetString());
        }
    }

    [Fact]
    public void RowsTrackEditsAndShowContextAndNumericValues()
    {
        using Document doc = Doc(Hits100());
        using SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("AB CD"));
        doc.Insert(0, new byte[100]);
        doc.Overwrite(100 + 0x21, [0xFF]);
        var rows = new SearchResultRowFactory(results, doc.Current, Encoding.ASCII);
        SearchResultRow first = rows.Row(0);
        Assert.Equal(100, first.Offset);
        Assert.Equal(MatchStatus.Unchanged, first.Status);
        Assert.Equal("AB CD", first.Hex);
        Assert.Equal(string.Join(' ', Enumerable.Repeat("00", 16)), first.Before);
        Assert.Equal(MatchStatus.Modified, rows.Row(1).Status);

        // 数値の検索の値とエンディアンの列。
        byte[] data = new byte[16];
        data[2] = 0x34;
        data[3] = 0x12;
        data[8] = 0x12;
        data[9] = 0x34;
        using Document numbers = Doc(data);
        SearchPattern both = NumericSearch.Integer("0x1234", new IntegerSearchOptions { Bits = 16, Endian = SearchEndian.Both });
        using SearchResults found = SearchEngine.FindAll(numbers.Current, both);
        var numericRows = new SearchResultRowFactory(found, numbers.Current, Encoding.ASCII);
        Assert.Equal(("LE", 2L), (numericRows.Row(0).Variant, numericRows.Row(0).Offset));
        Assert.Equal(("BE", 8L), (numericRows.Row(1).Variant, numericRows.Row(1).Offset));
        Assert.Contains("0x1234", numericRows.Row(1).Value, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryKeepsNewestFirstAndRemembersConditions()
    {
        var history = new SearchHistory();
        var hex = new SearchConditions { Kind = SearchKind.Hex };
        var text = new SearchConditions { Kind = SearchKind.Text, Encoding = TextEncodingId.Utf16LE, CaseSensitive = true };
        var integer = new SearchConditions { Kind = SearchKind.Integer, IntegerBits = 16 };
        history.Add(HistoryList.Find, new SearchHistoryEntry("10 11", hex));
        history.Add(HistoryList.Find, new SearchHistoryEntry("ab", text));
        history.Add(HistoryList.Find, new SearchHistoryEntry("0x1234", integer));

        // ↑ 2 回で 2 つ前の検索語と条件。↓ 1 回で 1 つ新しい方。
        var cursor = new HistoryCursor();
        Assert.Equal("0x1234", cursor.Older(history.Find)!.Text);
        SearchHistoryEntry two = cursor.Older(history.Find)!;
        Assert.Equal(("ab", text), (two.Text, two.Conditions));
        Assert.Equal(("0x1234", integer), (cursor.Newer(history.Find)!.Text, history.Find[0].Conditions));
        Assert.Null(cursor.Newer(history.Find));

        // 同じ項目は先頭に移す。置換欄は別の履歴。
        history.Add(HistoryList.Find, new SearchHistoryEntry("10 11", hex));
        Assert.Equal(["10 11", "0x1234", "ab"], history.Find.Select(e => e.Text));
        history.Add(HistoryList.Replace, new SearchHistoryEntry("FF FF", hex));
        Assert.Equal(["FF FF"], history.Replace.Select(e => e.Text));

        // 4 KiB を超える検索語は保存しない。
        Assert.False(history.Add(HistoryList.Find, new SearchHistoryEntry(new string('A', 4097), hex)));
        Assert.True(history.Add(HistoryList.Find, new SearchHistoryEntry(new string('A', 4096), hex)));

        // 保存と読み込み。
        var restored = new SearchHistory();
        restored.Load(JsonNode.Parse(history.ToJson().ToJsonString()));
        Assert.Equal(history.Find, restored.Find);
        Assert.Equal(history.Replace, restored.Replace);

        // 項目の削除と消去、上限。
        restored.RemoveAt(HistoryList.Find, 1);
        Assert.Equal(3, restored.Find.Count);
        restored.Limit = 1;
        Assert.Single(restored.Find);
        restored.Clear();
        Assert.Empty(restored.Find);
        Assert.Empty(restored.Replace);
        restored.Limit = 0;
        Assert.False(restored.Add(HistoryList.Find, new SearchHistoryEntry("10 11", hex)));
        Assert.Empty(restored.Find);
        Assert.Equal(SearchHistory.DefaultLimit, new SearchHistory().Limit);
    }

    [Fact]
    public void HistoryIsStoredInTheStateFile()
    {
        // 検索履歴は設定ではなくアプリの状態 (state.json。UI-23) に置く (FIND-28 の仕様 5)。
        string folder = Path.Combine(Path.GetTempPath(), "HexEditorTests", "state", Guid.NewGuid().ToString("N"));
        using (var store = new StateStore(folder))
        {
            store.Load();
            var history = new SearchHistory();
            history.Add(HistoryList.Find, new SearchHistoryEntry("DE AD", new SearchConditions()));
            store.Set("search.history", history.ToJson());
            store.Flush();
        }

        Assert.Contains("DE AD", File.ReadAllText(Path.Combine(folder, StateStore.FileName)), StringComparison.Ordinal);
        using var reopened = new StateStore(folder);
        reopened.Load();
        var loaded = new SearchHistory();
        loaded.Load(reopened.Get("search.history"));
        Assert.Equal("DE AD", Assert.Single(loaded.Find).Text);
    }
}
