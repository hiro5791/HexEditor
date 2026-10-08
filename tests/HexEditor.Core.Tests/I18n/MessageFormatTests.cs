using System.Globalization;
using HexEditor.Core.Text;

namespace HexEditor.Core.Tests.I18n;

/// <summary>UI-42 の仕様 4: 数値を含む文の複数形 (ICU MessageFormat の plural)。</summary>
public sealed class MessageFormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private const string Matches = "{0, plural, one {# match} other {# matches}} found.";

    [Fact]
    public void English_uses_singular_for_one_and_plural_otherwise()
    {
        Assert.Equal("1 match found.", MessageFormat.Format(Matches, En, "en", 1));
        Assert.Equal("2 matches found.", MessageFormat.Format(Matches, En, "en", 2));
        Assert.Equal("0 matches found.", MessageFormat.Format(Matches, En, "en", 0));
        Assert.Equal("1,234 matches found.", MessageFormat.Format(Matches, En, "en", 1234L));
    }

    [Fact]
    public void Exact_selector_and_other_arguments()
    {
        const string pattern = "{0}: {1, plural, =0 {no matches} one {# match} other {# matches}} ({2})";
        Assert.Equal("a.bin: no matches (done)", MessageFormat.Format(pattern, En, "en", "a.bin", 0, "done"));
        Assert.Equal("a.bin: 1 match (done)", MessageFormat.Format(pattern, En, "en", "a.bin", 1, "done"));
    }

    [Fact]
    public void Plain_patterns_are_string_format() =>
        Assert.Equal("x 5", MessageFormat.Format("{0} {1}", En, "en", "x", 5));

    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 2, "other")]
    [InlineData("ja", 1, "other")]
    [InlineData("fr", 0, "one")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 3, "few")]
    [InlineData("ru", 11, "many")]
    [InlineData("pl", 22, "few")]
    [InlineData("pl", 5, "many")]
    [InlineData("cs", 3, "few")]
    [InlineData("ar", 2, "two")]
    [InlineData("ar", 0, "zero")]
    public void Plural_categories(string language, int n, string category) => Assert.Equal(category, PluralRules.Category(language, n));
}
