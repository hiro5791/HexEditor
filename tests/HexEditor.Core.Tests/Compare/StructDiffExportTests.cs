using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Compare.Structure;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Compare.StructCompareTests;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ANA-32 構造の差分パネル (表示の計算とエクスポート)。</summary>
public sealed class StructDiffExportTests
{
    private const string TC = "TC";

    private static StructCompareResult Png() => StructCompareTests.Compare(Standard("png"), TestDataCatalog.StructPngA(), TestDataCatalog.StructPngB());

    [Fact]
    [Trait(TC, "TC-ANA-32-05")]
    public void JsonExportMatchesTheRows()
    {
        StructCompareResult r = Png();
        var view = new StructDiffView();
        view.Rebuild(r);
        List<StructDiffNode> rows = [.. Enumerable.Range(0, view.RowCount).Select(i => view.RowAt(i).Node!).Where(n => n.IsDiff)];

        using var stream = new MemoryStream();
        StructDiffExport.WriteJson(r, r.Diffs(), stream);
        JsonObject json = JsonNode.Parse(stream.ToArray())!.AsObject();
        JsonArray diffs = json["diffs"]!.AsArray();
        Assert.Equal(7, diffs.Count);
        Assert.Equal(rows.Count, diffs.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            StructDiffNode n = rows[i];
            JsonObject d = diffs[i]!.AsObject();
            Assert.Equal(StructDiffExport.KindName(n.Kind), d["kind"]!.GetValue<string>());
            Assert.Equal(n.DisplayPath, d["path"]!.GetValue<string>());
            Assert.Equal(n.LeftOffset, d["left"]!["offset"]!.GetValue<long>());
            Assert.Equal(n.RightOffset, d["right"]!["offset"]!.GetValue<long>());
            if (n.Kind == StructDiffKind.Added)
            {
                Assert.Equal(0, d["left"]!["size"]!.GetValue<long>());
            }
            else
            {
                Assert.Equal(n.LeftDisplay ?? string.Empty, d["left"]!["display"]!.GetValue<string>());
            }

            if (n.Kind == StructDiffKind.Removed)
            {
                Assert.Equal(0, d["right"]!["size"]!.GetValue<long>());
            }
            else
            {
                Assert.Equal(n.RightDisplay ?? string.Empty, d["right"]!["display"]!.GetValue<string>());
            }

            Assert.Contains(d["kind"]!.GetValue<string>(), new[] { "changed", "added", "removed" });
        }

        JsonObject width = diffs.Select(d => d!.AsObject()).Single(d => d["leftPath"]?.GetValue<string>() == "chunks[0].header.width");
        Assert.Equal(16, width["left"]!["value"]!.GetValue<long>());
        Assert.Equal(32, width["right"]!["value"]!.GetValue<long>());

        Assert.False(string.IsNullOrEmpty(json["template"]!.GetValue<string>()));
        Assert.EndsWith("png.hxt", json["templatePath"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonObject counts = json["summary"]!["counts"]!.AsObject();
        Assert.Equal((5, 1, 1), (counts["changed"]!.GetValue<long>(), counts["added"]!.GetValue<long>(), counts["removed"]!.GetValue<long>()));

        // 表示言語によらない (kind は英語の語。日本語の表示でも同じ)
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ja-JP");
            using var ja = new MemoryStream();
            StructDiffExport.WriteJson(r, r.Diffs(), ja);
            JsonArray jaDiffs = JsonNode.Parse(ja.ToArray())!["diffs"]!.AsArray();
            Assert.Equal(diffs.Select(d => d!["kind"]!.GetValue<string>()), jaDiffs.Select(d => d!["kind"]!.GetValue<string>()));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = saved;
        }

        // 16 進のオフセット (TPL-25 の仕様 4)
        using var hex = new MemoryStream();
        StructDiffExport.WriteJson(r, [rows.Single(n => n.LeftPath == "chunks[0].header.width")], hex, hexOffsets: true);
        Assert.Equal("0x10", JsonNode.Parse(hex.ToArray())!["diffs"]![0]!["left"]!["offset"]!.GetValue<string>());
    }

    [Fact]
    [Trait(TC, "TC-ANA-32-06")]
    public void CsvAndTextReportFormats()
    {
        StructCompareResult r = Png();
        using var csv = new MemoryStream();
        StructDiffExport.WriteCsv(r, r.Diffs(), csv);
        byte[] bytes = csv.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith(StructDiffExport.CsvHeader + "\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal));

        List<List<string>> records = ParseRfc4180(text);
        Assert.Equal(8, records.Count); // 見出し + 7 行
        Assert.All(records, rec => Assert.Equal(12, rec.Count));
        Assert.Contains(records, rec => rec[1].Contains("(type = \"tEXt\")", StringComparison.Ordinal));
        Assert.Contains("\"chunks[1] (type = \"\"tEXt\"\").text\"", text, StringComparison.Ordinal); // 引用符を含む値は RFC 4180 で引用する

        // NUL・カンマ・引用符・改行を含む値の引用
        Assert.Equal("\"a,b\"", StructDiffExport.Csv("a,b"));
        Assert.Equal("\"x\"\"y\"", StructDiffExport.Csv("x\"y"));
        Assert.Equal("\"Author\0Alice\"", StructDiffExport.Csv("Author\0Alice"));
        Assert.Equal("plain", StructDiffExport.Csv("plain"));

        using var report = new MemoryStream();
        StructDiffExport.WriteReport(r, ["Template: PNG"], r.Diffs(), StructDiffExport.KindName, "(left {0}, right {1})", report);
        byte[] rb = report.ToArray();
        Assert.NotEqual(0xEF, rb[0]);
        string[] lines = Encoding.UTF8.GetString(rb).Split("\r\n");
        int start = Array.IndexOf(lines, string.Empty) + 1;
        string[] diffLines = [.. lines[start..].Where(l => l.Length > 0)];
        Assert.Equal(7, diffLines.Length);
        Assert.Contains(diffLines, l => l.StartsWith("changed  chunks[0] (type = \"IHDR\").header.width  16 → 32  (left 0x10, right 0x10)", StringComparison.Ordinal));
    }

    /// <summary>RFC 4180 の CSV を読む (引用符の中のカンマ・改行・二重の引用符)。</summary>
    private static List<List<string>> ParseRfc4180(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    i++;
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }

    [Fact]
    public void ViewShowsOnlyDiffLeavesAndAncestorsAndNavigatesWithWrap()
    {
        StructCompareResult r = Png();
        var view = new StructDiffView();
        view.Rebuild(r);
        StructDiffRow first = view.RowAt(0);
        Assert.Equal("chunks", first.Node!.Name);
        Assert.Equal(new Dictionary<StructDiffKind, long> { [StructDiffKind.Changed] = 5, [StructDiffKind.Added] = 1, [StructDiffKind.Removed] = 1 },
            view.CountsOf(first.Node));
        Assert.Equal(7, view.LeafCount);

        StructDiffNode? at = null;
        for (int i = 0; i < 7; i++)
        {
            (StructDiffNode node, bool wrapped) = view.Step(at, next: true)!.Value;
            Assert.False(wrapped);
            at = node;
        }

        (StructDiffNode back, bool wrap) = view.Step(at, next: true)!.Value;
        Assert.True(wrap);
        Assert.Equal(view.LeafAt(0), back);

        view.Rebuild(r, new StructDiffFilter { Kinds = StructDiffFilterKinds.Added });
        Assert.Equal(1, view.LeafCount);
        view.Rebuild(r, new StructDiffFilter { Text = ".text" });
        Assert.Equal(["chunks[1] (type = \"tEXt\").text"], view.FilteredDiffs().Select(n => n.DisplayPath));

        // 閉じると子の行が消え、開くと戻る
        view.Rebuild(r, new StructDiffFilter());
        int all = view.RowCount;
        view.SetExpanded(view.RowAt(0).Node!, false);
        Assert.Equal(1, view.RowCount);
        view.SetExpanded(view.RowAt(0).Node!, true);
        Assert.Equal(all, view.RowCount);
    }

    [Fact]
    public void OutsideRowIsLastWhenThereAreOutsideDiffs()
    {
        byte[] a = TestDataCatalog.StructPngA();
        StructCompareResult r = CompareWithBytes(Standard("png"), [.. a, .. new byte[16]], [.. a, .. Enumerable.Repeat((byte)0xFF, 16)]);
        var view = new StructDiffView();
        view.Rebuild(r);
        Assert.True(view.HasOutsideRow);
        Assert.Null(view.RowAt(view.RowCount - 1).Node);
    }
}
