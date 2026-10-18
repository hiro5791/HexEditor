using System.Text;
using HexEditor.Core.Disassembly.Demangling;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Disassembly;

/// <summary>ANA-30 シンボル名のデマングル。</summary>
public sealed class DemanglerTests
{
    public static TheoryData<string, string> Fixture()
    {
        var data = new TheoryData<string, string>();
        foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "demangle"), "*.txt"))
        {
            foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
            {
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                string[] parts = line.Split('\t');
                data.Add(parts[0], parts[1]);
            }
        }

        return data;
    }

    [Theory]
    [Trait(TC, "TC-ANA-30-01")]
    [InlineData("_ZN3foo3barEi", "foo::bar(int)")]
    [InlineData("?bar@foo@@QEAAXH@Z", "public: void __cdecl foo::bar(int)")]
    [InlineData("_ZN4core3ptr13drop_in_place17h0123456789abcdefE", "core::ptr::drop_in_place")]
    [InlineData("_RNvCs1234_7mycrate3foo", "mycrate::foo")]
    [InlineData("_D3foo3barFiZv", "void foo.bar(int)")]
    public void SpecExamples(string mangled, string expected)
    {
        DemangleResult r = Demangler.Demangle(mangled);
        Assert.True(r.Success, mangled);
        Assert.Equal(expected, r.Text);
    }

    [Theory]
    [Trait(TC, "TC-ANA-30-01")]
    [MemberData(nameof(Fixture))]
    public void FixtureNames(string mangled, string expected)
    {
        DemangleResult r = Demangler.Demangle(mangled);
        Assert.True(r.Success, mangled);
        Assert.Equal(expected, r.Text);
    }

    [Fact]
    public void Options()
    {
        Assert.Equal("core::ptr::drop_in_place::h0123456789abcdef",
            Demangler.Demangle("_ZN4core3ptr13drop_in_place17h0123456789abcdefE", new DemangleOptions { ShowRustHash = true }).Text);
        Assert.Equal("foo::bar", Demangler.Demangle("_ZN3foo3barEi", new DemangleOptions { ShortNames = true }).Text);
        Assert.Equal("foo::bar", Demangler.Demangle("?bar@foo@@QEAAXH@Z", new DemangleOptions { ShortNames = true }).Text);
    }

    [Fact]
    [Trait(TC, "TC-ANA-30-02")]
    public void MultipleLines()
    {
        IReadOnlyList<DemangleResult> results = Demangler.DemangleLines(
            "_ZN3foo3barEi\n?bar@foo@@QEAAXH@Z\r\n_ZN4core3ptr13drop_in_place17h0123456789abcdefE\n_RNvCs1234_7mycrate3foo\n_D3foo3barFiZv\n",
            DemangleOptions.Default);
        Assert.Equal(
            ["foo::bar(int)", "public: void __cdecl foo::bar(int)", "core::ptr::drop_in_place", "mycrate::foo", "void foo.bar(int)"],
            results.Select(r => r.Text));
    }

    public static TheoryData<string> InvalidNames() =>
    [
        "_ZN3foo",
        "_ZN999999999foo",
        "?bar@@@@",
        "_RNv",
        "_D9",
        "_Z" + string.Concat(Enumerable.Repeat("N1a", 22_000)),
        "_Z" + string.Concat(Enumerable.Repeat("St", 100_000)),
        "_Z" + string.Concat(Enumerable.Repeat("P", 30_000)) + "i",
        "_ZN" + string.Concat(Enumerable.Repeat("1aI", 10_000)),
        "?f@@YA" + string.Concat(Enumerable.Repeat("PEA", 20_000)) + "H@Z",
        "_RIN" + string.Concat(Enumerable.Repeat("I", 20_000)),
    ];

    [Theory]
    [Trait(TC, "TC-ANA-30-04")]
    [MemberData(nameof(InvalidNames))]
    public void InvalidNamesDoNotCrash(string name)
    {
        DemangleResult r = Demangler.Demangle(name);
        Assert.False(r.Success);
        Assert.Equal(name, r.Text);
        Assert.True(r.Invalid);
    }

    [Fact]
    [Trait(TC, "TC-ANA-30-04")]
    public async Task ManyLinesInBackground()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10_001; i++)
        {
            sb.Append(i % 2 == 0 ? "_ZN3foo3barEi" : "_ZN3foo").Append('\n');
        }

        string text = sb.ToString();
        Assert.True(10_001 > Demangler.BackgroundLineCount);
        IReadOnlyList<DemangleResult> results = await Task.Run(() => Demangler.DemangleLines(text, DemangleOptions.Default));
        Assert.Equal(10_001, results.Count);
        Assert.All(results.Where((_, i) => i % 2 == 0), r => Assert.Equal("foo::bar(int)", r.Text));
        Assert.All(results.Where((_, i) => i % 2 == 1), r => Assert.False(r.Success));
    }

    [Fact]
    public void NotMangledIsReturnedAsIs()
    {
        DemangleResult r = Demangler.Demangle("main");
        Assert.False(r.Success);
        Assert.False(r.Invalid);
        Assert.Equal("main", r.Text);
    }

    /// <summary>テンプレートの標準ライブラリの std::sys::demangle (07 の TPL-18 の std.sys。ANA-30 の仕様 7)。</summary>
    [Fact]
    public void TemplateLibraryUsesDemangler()
    {
        HexEditor.Core.Templates.TemplateResult r = HexEditor.Core.Tests.Templates.TemplateTestSupport.Run(
            "std::core::print(std::sys::demangle(\"_ZN3foo3barEi\")); std::core::print(std::sys::demangle(\"plain\"));", [1]);
        Assert.Contains(r.Output, o => o.Text == "foo::bar(int)");
        Assert.Contains(r.Output, o => o.Text == "plain");
    }

    /// <summary>TC-ANA-30-05 の代わりの短いファジング (CI で毎回動かす。1 時間のファジングは SharpFuzz が必要なため手元では行わない)。</summary>
    [Fact]
    [Trait(TC, "TC-ANA-30-05")]
    public void RandomMutationsNeverThrow()
    {
        var random = new Random(30_05);
        string[] seeds = [.. Fixture().Select(r => (string)r[0])];
        const string alphabet = "_ZNSIEKPRFvisjcdT0123456789@?$ABCDHQUVXYaobmnpqrtuwxyzG";
        for (int n = 0; n < 20_000; n++)
        {
            char[] s = seeds[random.Next(seeds.Length)].ToCharArray();
            int edits = random.Next(1, 4);
            var list = new List<char>(s);
            for (int e = 0; e < edits; e++)
            {
                int at = random.Next(list.Count + 1);
                switch (random.Next(3))
                {
                    case 0 when list.Count > 0 && at < list.Count:
                        list.RemoveAt(at);
                        break;
                    case 1 when at < list.Count:
                        list[at] = alphabet[random.Next(alphabet.Length)];
                        break;
                    default:
                        list.Insert(at, alphabet[random.Next(alphabet.Length)]);
                        break;
                }
            }

            string input = new([.. list]);
            DemangleResult r = Demangler.Demangle(input);
            Assert.True(r.Success || r.Text == input);
        }
    }
}
