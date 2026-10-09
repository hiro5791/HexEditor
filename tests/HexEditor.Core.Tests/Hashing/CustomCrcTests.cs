using System.Text.Json;
using HexEditor.Core.Hashing;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Hashing.HashFixtures;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>カスタム CRC の一覧 (<see cref="HashCatalog.Custom"/>) を変えるテストは同時に実行しない。</summary>
[CollectionDefinition(Name)]
public sealed class HashCatalogCustomCollection
{
    public const string Name = "HashCatalog.Custom";
}

/// <summary>ANA-20 カスタム CRC (定義の検証、check / residue、JSON のエクスポート・インポート、設定への保存)。</summary>
[Collection(HashCatalogCustomCollection.Name)]
public sealed class CustomCrcTests
{
    private static readonly CustomCrcDefinition MyCrc16 = new("MyCRC16", 16, 0x8005, 0, true, true, 0);

    [Fact]
    public void ArcParametersGiveCheckBb3dAndResidue0000()
    {
        // ANA-20 の受け入れ基準 1 (UI は TC-ANA-20-01)。
        Assert.Empty(MyCrc16.Validate([]));
        Assert.Equal(0xBB3DUL, MyCrc16.Check);
        Assert.Equal(0UL, MyCrc16.Residue);
        Assert.Equal("BB3D", MyCrc16.FormatValue(MyCrc16.Check!.Value));
        Assert.Equal("0000", MyCrc16.FormatValue(MyCrc16.Residue!.Value));
    }

    [Fact]
    public void PolyWiderThanTheWidthIsAnErrorCarryingTheWidth()
    {
        // 「多項式は 16 bit 以内で指定してください」(TC-ANA-20-01 の手順 4)。
        CustomCrcDefinition wide = MyCrc16 with { Poly = 0x18005 };
        Assert.Equal([new CustomCrcError(CustomCrcErrorCode.ValueTooWide, CustomCrcField.Poly, 16)], wide.Validate([]));
        Assert.Null(wide.Check);
        Assert.Null(wide.Residue);
        Assert.Throws<InvalidOperationException>(() => wide.ToAlgorithm());

        Assert.Equal(
            [new CustomCrcError(CustomCrcErrorCode.ValueTooWide, CustomCrcField.Init, 5), new CustomCrcError(CustomCrcErrorCode.ValueTooWide, CustomCrcField.XorOut, 5)],
            new CustomCrcDefinition("W5", 5, 0x05, 0x20, false, false, 0x3F).Validate([]));

        // 幅 64 は全ビットが使える。
        Assert.Empty(new CustomCrcDefinition("W64", 64, ulong.MaxValue, ulong.MaxValue, false, false, ulong.MaxValue).Validate([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(-1)]
    public void WidthOutsideOneTo64IsAnError(int width) =>
        Assert.Equal([new CustomCrcError(CustomCrcErrorCode.WidthOutOfRange, CustomCrcField.Width, width)],
            (MyCrc16 with { Width = width }).Validate([]));

    [Theory]
    [InlineData("CRC-16/ARC")]
    [InlineData("crc-16/arc")]
    [InlineData("crc-16/ccitt-false")]
    [InlineData(" CRC-32 ")]
    [InlineData("crc32c")]
    [InlineData("SHA-256")]
    [InlineData("mycrc16")]
    public void NameMustNotDuplicatePresetsAliasesOrOtherCustomCrcs(string name) =>
        Assert.Equal([new CustomCrcError(CustomCrcErrorCode.DuplicateName, CustomCrcField.Name)],
            (MyCrc16 with { Name = name }).Validate(["MyCRC16"]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NameIsRequired(string name) =>
        Assert.Equal([new CustomCrcError(CustomCrcErrorCode.NameRequired, CustomCrcField.Name)], (MyCrc16 with { Name = name }).Validate([]));

    [Fact]
    public void StoreAddReplaceRemove()
    {
        var store = new CustomCrcStore();
        int changes = 0;
        store.Changed += (_, _) => changes++;
        Assert.Empty(store.Add(MyCrc16 with { Name = " MyCRC16 " }));
        Assert.Equal("MyCRC16", store.Items[0].Name);
        Assert.Equal([CustomCrcErrorCode.DuplicateName], store.Add(MyCrc16 with { Name = "MYCRC16" }).Select(e => e.Code));

        // 編集中の項目自身との重複は誤りにしない。
        Assert.Empty(store.Replace("mycrc16", MyCrc16 with { Init = 0xFFFF }));
        Assert.Equal(0xFFFFUL, store.Find("MyCRC16")!.Init);
        Assert.Equal([CustomCrcErrorCode.ValueTooWide], store.Replace("MyCRC16", MyCrc16 with { Poly = 0x18005 }).Select(e => e.Code));
        Assert.True(store.Remove("MYCRC16"));
        Assert.False(store.Remove("MyCRC16"));
        Assert.Empty(store.Items);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void DuplicateAPresetForEditing()
    {
        HashAlgorithmInfo arc = HashCatalog.Get("crc16-arc");
        CustomCrcDefinition copy = CustomCrcDefinition.FromPreset(arc);
        Assert.Equal("CRC-16/ARC (2)", copy.Name);
        Assert.Equal((16, 0x8005UL, 0UL, true, true, 0UL), (copy.Width, copy.Poly, copy.Init, copy.RefIn, copy.RefOut, copy.XorOut));
        Assert.Equal(0xBB3DUL, copy.Check);
        Assert.Equal("CRC-16/ARC (3)", CustomCrcDefinition.FromPreset(arc, ["crc-16/arc (2)"]).Name);
        Assert.Throws<ArgumentException>(() => CustomCrcDefinition.FromPreset(HashCatalog.Get("adler32")));
    }

    [Fact]
    public void ApplyToCatalogAddsTheCustomCrcsAtTheEndOfTheCrcGroup()
    {
        var store = new CustomCrcStore();
        store.Add(MyCrc16);
        try
        {
            store.ApplyToCatalog();
            HashAlgorithmInfo custom = HashCatalog.Get("custom:MyCRC16");
            Assert.True(custom.IsCustom);
            Assert.Equal(HashGroup.Crc, custom.Group);
            Assert.Same(custom, HashCatalog.Find("mycrc16"));
            Assert.Equal("BB3D", Convert.ToHexString(Compute(custom, Check9)));
            int index = HashCatalog.All.ToList().IndexOf(custom);
            Assert.Equal(HashGroup.Crc, HashCatalog.All[index - 1].Group);
            Assert.NotEqual(HashGroup.Crc, HashCatalog.All[index + 1].Group);
        }
        finally
        {
            HashCatalog.SetCustom([]);
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-20-03")]
    public void ExportedJsonImportsIntoAnotherStoreWithTheSameValues()
    {
        // 設定フォルダ P1 で幅 5・16・64 の 3 つのカスタム CRC を定義する。
        var p1 = new CustomCrcStore();
        Assert.Empty(p1.Add(new CustomCrcDefinition("Five", 5, 0x15, 0x1F, true, false, 0x0A)));
        Assert.Empty(p1.Add(new CustomCrcDefinition("Sixteen", 16, 0x3D65, 0x1234, false, true, 0xFFFF)));
        Assert.Empty(p1.Add(new CustomCrcDefinition("SixtyFour", 64, 0xAD93D23594C935A9, 0x0123456789ABCDEF, true, true, 0xFEDCBA9876543210)));

        // 手順 1: JSON の各項目に name、width、poly、init、refin、refout、xorout、check がある。
        string json = p1.Export();
        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            Assert.Equal(3, doc.RootElement.GetArrayLength());
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                foreach (string key in new[] { "name", "width", "poly", "init", "refin", "refout", "xorout", "check" })
                {
                    Assert.True(item.TryGetProperty(key, out _), key);
                }
            }

            Assert.Equal("0xFEDCBA9876543210", doc.RootElement[2].GetProperty("xorout").GetString());
        }

        // 手順 2: 新しい設定フォルダ P2 でインポートする。
        var p2 = new CustomCrcStore();
        CustomCrcImportResult result = p2.Import(json);
        Assert.Empty(result.Errors);
        Assert.Equal(p1.Items, p2.Items);

        // 手順 3: TD-RANDOM-16M の先頭 1 MiB の値が P1 と P2 で一致する。
        byte[] data = new byte[1024 * 1024];
        TestDataCatalog.Expected("TD-RANDOM-16M", 0, data);
        for (int i = 0; i < 3; i++)
        {
            HashAlgorithmInfo a1 = p1.Items[i].ToAlgorithm();
            HashAlgorithmInfo a2 = p2.Items[i].ToAlgorithm();
            byte[] expected = HashBytes.FromNumber(CrcHasher.Reference(a1.Crc!, data), (a1.Bits + 7) / 8);
            Assert.Equal(expected, Compute(a1, data));
            Assert.Equal(expected, Compute(a2, data));
        }

        // 手順 4: TD-ANA-CRC-IMPORT-BAD (2 件目は width が 65) をインポートすると、1 件目と 3 件目だけを取り込み、2 件目の理由を返す。
        CustomCrcImportResult bad = p2.Import(File.ReadAllText(PathOf("crc-import-bad.json")));
        Assert.Equal(["Import CRC-8", "Import CRC-32"], bad.Items.Select(d => d.Name));
        CustomCrcImportError error = Assert.Single(bad.Errors);
        Assert.Equal(2, error.ItemNumber);
        Assert.Equal(new CustomCrcError(CustomCrcErrorCode.WidthOutOfRange, CustomCrcField.Width, 65), error.Error);
        Assert.Equal(5, p2.Items.Count);
    }

    [Fact]
    public void SettingsStringRoundTrips()
    {
        var store = new CustomCrcStore();
        store.Add(MyCrc16);
        store.Add(new CustomCrcDefinition("名前付き CRC-3", 3, 0x3, 0x7, true, true, 0));
        CustomCrcStore loaded = CustomCrcStore.Load(store.Serialize());
        Assert.Equal(store.Items, loaded.Items);
        Assert.Empty(CustomCrcStore.Load(null).Items);
        Assert.Empty(CustomCrcStore.Load("not json").Items);
    }

    [Fact]
    public void ImportAcceptsHexStringsDecimalStringsAndJsonNumbersAndDefaults()
    {
        string json = """
            [
              { "name": "A", "width": 16, "poly": 32773, "refin": true, "refout": true, "check": "0xbb3d" },
              { "name": "B", "width": "16", "poly": "4129", "init": "0xFFFF", "check": 10673 },
              { "name": "C", "width": 5, "poly": "0x05", "init": "0x1F", "refin": "true", "refout": true, "xorout": "0x1F", "residue": "0x06" },
            ]
            """;
        CustomCrcImportResult result = CustomCrcJson.Parse(json);
        Assert.Empty(result.Errors);
        Assert.Equal(
            [new CustomCrcDefinition("A", 16, 0x8005, 0, true, true, 0), new CustomCrcDefinition("B", 16, 0x1021, 0xFFFF), new CustomCrcDefinition("C", 5, 0x05, 0x1F, true, true, 0x1F)],
            result.Items);
    }

    [Fact]
    public void ImportReportsEachBrokenItemWithItsNumberAndReason()
    {
        string json = """
            [
              { "name": "Good", "width": 8, "poly": "0x07" },
              42,
              { "width": 8, "poly": "0x07" },
              { "name": "NoPoly", "width": 8 },
              { "name": "BadPoly", "width": 8, "poly": "0xZZ" },
              { "name": "Wide", "width": 8, "poly": "0x107" },
              { "name": "WrongCheck", "width": 8, "poly": "0x07", "check": "0xF5" },
              { "name": "good", "width": 8, "poly": "0x07" },
              { "name": "CRC-8/SMBUS", "width": 8, "poly": "0x07" },
              { "name": "Existing", "width": 8, "poly": "0x07" },
              { "name": "BadBool", "width": 8, "poly": "0x07", "refin": 1 },
              { "name": "", "width": 8, "poly": "0x07" },
              { "name": "Fine", "width": 8, "poly": "0x07", "check": "0xF4" }
            ]
            """;
        CustomCrcImportResult result = CustomCrcJson.Parse(json, ["existing"]);
        Assert.Equal(["Good", "Fine"], result.Items.Select(d => d.Name));
        Assert.Equal(
            [
                (2, CustomCrcErrorCode.NotAnObject, CustomCrcField.Item),
                (3, CustomCrcErrorCode.MissingField, CustomCrcField.Name),
                (4, CustomCrcErrorCode.MissingField, CustomCrcField.Poly),
                (5, CustomCrcErrorCode.InvalidValue, CustomCrcField.Poly),
                (6, CustomCrcErrorCode.ValueTooWide, CustomCrcField.Poly),
                (7, CustomCrcErrorCode.CheckMismatch, CustomCrcField.Check),
                (8, CustomCrcErrorCode.DuplicateName, CustomCrcField.Name),
                (9, CustomCrcErrorCode.DuplicateName, CustomCrcField.Name),
                (10, CustomCrcErrorCode.DuplicateName, CustomCrcField.Name),
                (11, CustomCrcErrorCode.InvalidValue, CustomCrcField.RefIn),
                (12, CustomCrcErrorCode.NameRequired, CustomCrcField.Name),
            ],
            result.Errors.Select(e => (e.ItemNumber, e.Error.Code, e.Error.Field)));
        Assert.Equal(8, result.Errors[4].Error.Width);
    }

    [Theory]
    [InlineData("{", CustomCrcErrorCode.InvalidJson)]
    [InlineData("", CustomCrcErrorCode.InvalidJson)]
    [InlineData("{\"name\": \"x\"}", CustomCrcErrorCode.NotAnArray)]
    public void ImportOfAnUnreadableFileReportsItemZero(string json, CustomCrcErrorCode code)
    {
        CustomCrcImportResult result = CustomCrcJson.Parse(json);
        Assert.Empty(result.Items);
        Assert.Equal([(0, code)], result.Errors.Select(e => (e.ItemNumber, e.Error.Code)));
    }
}
