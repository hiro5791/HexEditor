using System.Buffers.Binary;
using HexEditor.Core.Templates;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Templates;

/// <summary>
/// テンプレートの値の検索 (FIND-29) の条件と検索。結果の一覧からテンプレートのツリーとエディタへ移動する部分 (TC-FIND-29-01 の手順 3・4) は、
/// テンプレート結果パネル (別の担当) がこの検索を使うときに UI で確かめる。
/// </summary>
public sealed class TemplateValueSearchTests
{
    private static TemplateResult ApplyPe(byte[] data)
    {
        var catalog = TemplateCatalog.CreateDefault(null);
        TemplateCatalogEntry entry = catalog.Find("pe") ?? throw new InvalidOperationException("pe.hxt is not bundled");
        return TemplateEngine.ApplyAndWait(catalog.Load(entry), TemplateDataSource.FromBytes(data, "pe.exe"));
    }

    /// <summary>テストのコードで PE を読んで求めた、名前に Characteristics を含むフィールドの (オフセット, 長さ)。</summary>
    private static HashSet<(long, long)> ExpectedCharacteristics(byte[] pe)
    {
        long lfanew = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(0x3C));
        long fileHeader = lfanew + 4;
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)fileHeader + 2));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)fileHeader + 16));
        long optional = fileHeader + 20;
        long table = optional + optionalSize;
        var expected = new HashSet<(long, long)> { (fileHeader + 18, 2), (optional + 70, 2) };
        for (int i = 0; i < sections; i++)
        {
            expected.Add((table + i * 40 + 36, 4));
        }

        return expected;
    }

    [Fact]
    [Trait("TC", "TC-FIND-29-01")]
    public void Finds_pe_characteristics_fields_and_reports_syntax_errors()
    {
        byte[] pe = File.ReadAllBytes(TestDataCatalog.Get("TD-PE-X64"));
        using TemplateResult result = ApplyPe(pe);

        // 1・2: name:*Characteristics* で、ファイルヘッダ・オプションヘッダ (DllCharacteristics)・各セクションヘッダのフィールド。
        IReadOnlyList<TemplateValueMatch> matches = TemplateValueSearch.Find(result, TemplateValueQuery.Parse("name:*Characteristics*"), evaluateAll: true);
        Assert.Equal(ExpectedCharacteristics(pe), matches.Select(m => (m.Offset, m.Length)).ToHashSet());
        Assert.All(matches, m =>
        {
            Assert.EndsWith("Characteristics", m.Path, StringComparison.Ordinal);
            Assert.NotEmpty(m.Value);
            Assert.Same(m.Node, result.FindByPath(m.Path));
        });
        Assert.Contains(matches, m => m.Path.Contains("FileHeader", StringComparison.Ordinal));
        Assert.Contains(matches, m => m.Path.StartsWith("sections[0]", StringComparison.Ordinal));

        // 5: 演算子の後に値がない (構文エラー): 位置を示す。
        var error = Assert.Throws<TemplateQueryException>(() => TemplateValueQuery.Parse("name:*Characteristics* value>"));
        Assert.Equal(TemplateQueryErrorKind.MissingValue, error.Kind);
        Assert.Equal("name:*Characteristics* value>".Length, error.Position);
    }

    [Fact]
    public void Combines_type_and_value_conditions()
    {
        byte[] pe = File.ReadAllBytes(TestDataCatalog.Get("TD-PE-X64"));
        using TemplateResult result = ApplyPe(pe);
        IReadOnlyList<TemplateValueMatch> zero = TemplateValueSearch.Find(result, TemplateValueQuery.Parse("type:u16 value=0 name:e_*"), evaluateAll: true);
        Assert.NotEmpty(zero);
        Assert.All(zero, m => Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan((int)m.Offset))));

        long lfanew = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(0x3C));
        IReadOnlyList<TemplateValueMatch> big = TemplateValueSearch.Find(result, TemplateValueQuery.Parse($"name:e_lfanew value>={lfanew}"));
        Assert.Equal(0x3C, Assert.Single(big).Offset);
        Assert.Empty(TemplateValueSearch.Find(result, TemplateValueQuery.Parse($"name:e_lfanew value>0x{lfanew:X}")));

        // 文字列の正規表現 (セクション名)。
        IReadOnlyList<TemplateValueMatch> text = TemplateValueSearch.Find(result, TemplateValueQuery.Parse("name:Name value~\"^\\.text\""), evaluateAll: true);
        Assert.All(text, m => Assert.StartsWith("sections[", m.Path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", TemplateQueryErrorKind.Empty, 0)]
    [InlineData("name:", TemplateQueryErrorKind.MissingValue, 5)]
    [InlineData("size:4", TemplateQueryErrorKind.UnknownKey, 0)]
    [InlineData("value 4", TemplateQueryErrorKind.BadOperator, 5)]
    [InlineData("value~\"(\"", TemplateQueryErrorKind.BadRegex, 6)]
    [InlineData("value=\"abc", TemplateQueryErrorKind.UnclosedQuote, 6)]
    public void Syntax_errors_carry_their_position(string query, TemplateQueryErrorKind kind, int position)
    {
        var error = Assert.Throws<TemplateQueryException>(() => TemplateValueQuery.Parse(query));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(position, error.Position);
    }
}
