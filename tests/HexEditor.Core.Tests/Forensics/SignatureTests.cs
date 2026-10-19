using System.Text;
using System.Text.Json;
using HexEditor.Core.Forensics.Carving;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>FOR-18 カービング用のシグネチャ定義。</summary>
public sealed class SignatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-sig").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    [Trait(TC, "TC-FOR-18-01")]
    public void ANewDefinitionIsSavedAndSurvivesARestart()
    {
        Assert.True(SizeInput.TryParse("1M", out long max));
        Assert.Equal(1_048_576, max);

        var store = new SignatureStore(_dir);
        store.Load();
        store.Add(new CarvingSignature { Name = "Java class", Extension = "class", Headers = ["CA FE BA BE"], MaxSize = max });

        // 再起動に相当: 新しい SignatureStore が同じフォルダを読む。
        var restarted = new SignatureStore(_dir);
        Assert.Empty(restarted.Load().Rejected);
        CarvingSignature loaded = restarted.Find("Java class")!;
        Assert.Equal(["CA FE BA BE"], loaded.Headers);
        Assert.Equal(1_048_576, loaded.MaxSize);
        Assert.False(loaded.BuiltIn);
        Assert.Equal("java-class", loaded.Folder);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_dir, "carving-signatures.json")));
        JsonElement saved = json.RootElement.GetProperty("signatures")[0];
        Assert.Equal("Java class", saved.GetProperty("name").GetString());
        Assert.Equal("CA FE BA BE", saved.GetProperty("headers")[0].GetString());
        Assert.Equal(1_048_576, saved.GetProperty("maxSize").GetInt64());
    }

    [Fact]
    [Trait(TC, "TC-FOR-18-02")]
    public void BuiltInDefinitionsCannotBeEditedButCanBeDisabledAndDuplicated()
    {
        var store = new SignatureStore(_dir);
        CarvingSignature jpeg = store.Find("JPEG")!;
        Assert.True(jpeg.BuiltIn);
        long original = jpeg.MaxSize;

        // 編集は拒否する。
        Assert.Throws<InvalidOperationException>(() => store.Update("JPEG", jpeg with { MaxSize = 5 }));
        Assert.False(store.Remove("JPEG"));
        Assert.NotNull(store.Find("JPEG"));

        // 有効・無効の切り替えだけはできる。再起動後も保持する。
        store.SetEnabled("JPEG", false);
        Assert.DoesNotContain(store.Enabled, s => s.Name == "JPEG");
        var restarted = new SignatureStore(_dir);
        restarted.Load();
        Assert.False(restarted.Find("JPEG")!.Enabled);
        restarted.SetEnabled("JPEG", true);
        Assert.True(restarted.Find("JPEG")!.Enabled);

        // 複製すると編集できる。元の定義は変わらない。
        CarvingSignature copy = restarted.Duplicate("JPEG");
        Assert.Equal("JPEG (copy)", copy.Name);
        Assert.False(copy.BuiltIn);
        restarted.Update(copy.Name, copy with { MaxSize = 20L * 1024 * 1024 });
        Assert.Equal(20L * 1024 * 1024, restarted.Find("JPEG (copy)")!.MaxSize);
        Assert.Equal(original, restarted.Find("JPEG")!.MaxSize);
        Assert.Equal("JPEG (copy 2)", restarted.Duplicate("JPEG").Name);
    }

    [Fact]
    [Trait(TC, "TC-FOR-18-03")]
    public void ALoadedFileWithInvalidDefinitionsRejectsOnlyThoseWithReasons()
    {
        string file = TestDataCatalog.Generate("TD-FOR-SIG-MIXED", _dir);
        var store = new SignatureStore(_dir);
        SignatureImportResult result = store.Import(File.ReadAllText(file));

        Assert.Equal(["good1", "good2"], result.Accepted.Select(s => s.Name));
        Assert.Equal(["bad-empty", "bad-hex", "bad-size"], result.Rejected.Select(r => r.Name));
        Assert.Contains(result.Rejected.Single(r => r.Name == "bad-empty").Errors, e => e.Code == "HeaderEmpty");
        Assert.Contains(result.Rejected.Single(r => r.Name == "bad-hex").Errors, e => e.Code == "HeaderFormat");
        Assert.Contains(result.Rejected.Single(r => r.Name == "bad-size").Errors, e => e.Code == "MaxSizeBelowMin");

        // 取り込んだものだけが残る。
        Assert.NotNull(store.Find("good1"));
        Assert.Null(store.Find("bad-hex"));

        // 同じ名前をもう一度取り込むと、名前の重複として除外する。
        SignatureImportResult again = store.Import(File.ReadAllText(file));
        Assert.Empty(again.Accepted);
        Assert.Contains(again.Rejected, r => r.Name == "good1" && r.Errors.Any(e => e.Code == "NameInUse"));
    }

    [Fact]
    public void ImportRejectsBrokenJsonAndWrongTypesWithoutThrowingPerDefinition()
    {
        var store = new SignatureStore(null);
        Assert.Throws<SignatureFileException>(() => store.Import("{ not json"));
        Assert.Throws<SignatureFileException>(() => store.Import("{\"x\":1}"));
        SignatureImportResult r = store.Import("""
            [ 1, { "name": 5, "extension": "x", "headers": ["AA"] }, { "name": "ok", "extension": ".bin", "headers": "AA BB", "maxSize": "2M", "sizeField": { "offset": 4, "bits": 24, "endian": "big", "add": 8 } },
              { "name": "bad-bits", "extension": "x", "headers": ["AA"], "sizeField": { "offset": 4, "bits": 12 } } ]
            """);
        Assert.Equal(["ok"], r.Accepted.Select(s => s.Name));
        Assert.Equal(2 * 1024 * 1024, r.Accepted[0].MaxSize);
        Assert.Equal("bin", r.Accepted[0].Extension);
        Assert.Equal(new SizeFieldRule { Offset = 4, Bits = 24, BigEndian = true, Add = 8 }, r.Accepted[0].SizeField);
        Assert.Equal(3, r.Rejected.Count);
    }

    [Fact]
    public void ExportThenImportRoundTripsEveryField()
    {
        var a = new SignatureStore(null);
        CarvingSignature full = new()
        {
            Name = "Full",
            Extension = "ful",
            Headers = ["AA ?? CC", "DD E?"],
            HeaderOffset = 4,
            Footer = "FF FE",
            FooterInclude = 6,
            MaxSize = 123_456,
            MinSize = 100,
            SizeField = new SizeFieldRule { Offset = 8, Bits = 16, BigEndian = true, Add = -2 },
            Structure = "png",
            SectorAligned = false,
            Enabled = false,
        };
        a.Add(full);
        string json = a.Export();
        var b = new SignatureStore(null);
        Assert.Empty(b.Import(json).Rejected);
        CarvingSignature loaded = b.Find("Full")!;
        Assert.Equal(full.Headers, loaded.Headers);
        Assert.Equal(full, loaded with { Headers = full.Headers });

        // 組み込みの定義も名前を指定すれば書き出せる (取り込むと名前が重複するので、名前を変えて取り込む)。
        string builtin = a.Export(["PNG"]);
        Assert.Contains("89 50 4E 47", builtin, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CA FE BA BE", 4, true)]
    [InlineData("0xCA,0xFE", 2, true)]
    [InlineData("\\xCA\\xFE\\xBA", 3, true)]
    [InlineData("CAFEBABE", 4, true)]
    [InlineData("CA ?? BA", 3, true)]
    [InlineData("C? FE", 2, true)]
    [InlineData("CA F", 0, false)]
    [InlineData("ZZ", 0, false)]
    [InlineData("", 0, false)]
    [InlineData("?? ??", 0, false)]
    public void HexPatternsFollowTheInputRules(string text, int length, bool ok)
    {
        HexPatternError error = HexMaskPattern.TryParse(text, out HexMaskPattern? pattern);
        Assert.Equal(ok, error == HexPatternError.None);
        if (ok)
        {
            Assert.Equal(length, pattern!.Length);
        }
    }

    [Fact]
    public void WildcardsMatchAnyByteOrNibble()
    {
        HexMaskPattern p = HexMaskPattern.Parse("CA ?? B?");
        Assert.True(p.MatchesAt([0xCA, 0x00, 0xB7]));
        Assert.True(p.MatchesAt([0xCA, 0xFF, 0xBF, 0x11]));
        Assert.False(p.MatchesAt([0xCA, 0x00, 0xC7]));
        Assert.False(p.MatchesAt([0xCA, 0x00]));
        Assert.Equal("CA ?? B?", p.ToString());
        Assert.Equal(1, p.AnchorLength);
    }

    [Fact]
    public void ValidationReportsEveryProblemOfADefinition()
    {
        var s = new CarvingSignature { Name = " ", Extension = "a/b", Headers = ["AA"], HeaderOffset = -1, MaxSize = 0, MinSize = -1, Footer = "Z", FooterInclude = -1 };
        string[] codes = [.. s.Validate().Select(e => e.Code)];
        Assert.Contains("NameEmpty", codes);
        Assert.Contains("ExtensionInvalid", codes);
        Assert.Contains("HeaderOffsetNegative", codes);
        Assert.Contains("MaxSizeInvalid", codes);
        Assert.Contains("MinSizeInvalid", codes);
        Assert.Contains("FooterFormat", codes);
        Assert.Contains("FooterIncludeNegative", codes);
        Assert.Empty(new CarvingSignature { Name = "ok", Extension = "x", Headers = ["AA"] }.Validate());
    }

    [Fact]
    public void BuiltInDefinitionsCoverTheListedFormatsAndAreValid()
    {
        string[] expected =
        [
            "JPEG", "PNG", "GIF", "BMP", "TIFF", "PDF", "ZIP", "RAR", "7z", "GZIP", "BZIP2", "MP3", "MP4", "AVI", "WAV", "SQLite", "PE", "ELF", "OLE", "Registry hive",
            "EVTX", "LNK", "RTF", "HTML", "XML",
        ];
        Assert.Equal(expected.Length, BuiltInSignatures.All.Count);
        foreach (string name in expected)
        {
            CarvingSignature s = Assert.Single(BuiltInSignatures.All, b => b.Name == name);
            Assert.Empty(s.Validate());
            Assert.True(s.BuiltIn);

            // 各定義はフッターだけに頼らない (構造の検証かサイズの取得を使う)。
            Assert.True(s.Structure is not null || s.SizeField is not null, name);
            if (s.Structure is not null)
            {
                Assert.NotNull(StructureValidators.Find(s.Structure));
            }
        }

        Assert.Equal(4, Assert.Single(BuiltInSignatures.All, b => b.Name == "MP4").HeaderOffset);
        Assert.Equal(StructureValidators.Names.Count, StructureValidators.Names.Distinct().Count());
        Assert.Equal("mp4", Assert.Single(BuiltInSignatures.All, b => b.Name == "MP4").Folder);
        Assert.Equal("registry-hive", Assert.Single(BuiltInSignatures.All, b => b.Name == "Registry hive").Folder);
        _ = Encoding.UTF8;
    }

    [Theory]
    [InlineData("1M", 1_048_576)]
    [InlineData("10 MiB", 10_485_760)]
    [InlineData("4096", 4096)]
    [InlineData("0x100", 256)]
    [InlineData("1.5G", 1_610_612_736)]
    [InlineData("2K+1", 2049)]
    public void SizesAcceptTheInputExpressions(string text, long expected)
    {
        Assert.True(SizeInput.TryParse(text, out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1/0")]
    public void BadSizesAreRejected(string text) => Assert.False(SizeInput.TryParse(text, out _));
}
