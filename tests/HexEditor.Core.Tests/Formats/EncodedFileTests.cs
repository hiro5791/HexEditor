using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Formats;

/// <summary>エンコード形式のファイルを開く (ENG-38) と、元の形式で保存する (TOOL-11)。</summary>
public sealed class EncodedFileTests
{
    private static (Document Doc, EncodedFileSettings Settings, long Base) Open(string format, string path)
    {
        using ImportResult result = ImportFile(format, path, EncodedFile.OpenOptions(format));
        EncodedFileSettings settings = result.Settings!;
        long baseAddress = result.BaseAddress;
        return (EncodedFile.CreateDocument(result, format, Options()), settings, baseAddress);
    }

    [Theory]
    [InlineData(FormatIds.IntelHex, "TD-IHEX")]
    [InlineData(FormatIds.SRecord, "TD-SREC")]
    [InlineData(FormatIds.Base64, "TD-BASE64")]
    public void Saving_without_edits_reproduces_the_file(string format, string id)
    {
        string path = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(TempDirectory);
        File.Copy(TestDataCatalog.Get(id), path);
        (Document doc, EncodedFileSettings settings, long baseAddress) = Open(format, path);
        using (doc)
        {
            string out2 = path + ".out";
            EncodedFile.Save(doc.Current, baseAddress, settings, out2);
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(out2));
        }
    }

    [Theory]
    [Trait(TC, "TC-TOOL-11-01")]
    [InlineData(FormatIds.IntelHex, "TD-IHEX", 2)]
    [InlineData(FormatIds.SRecord, "TD-SREC", 2)]
    [InlineData(FormatIds.Base64, "TD-BASE64", 0)]
    public void Editing_one_byte_changes_only_that_line(string format, string id, long offset)
    {
        string path = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(TempDirectory);
        File.Copy(TestDataCatalog.Get(id), path);
        string[] before = File.ReadAllLines(path);
        (Document doc, EncodedFileSettings settings, long baseAddress) = Open(format, path);
        using (doc)
        {
            byte old = Read(doc.Current, offset, 1)[0];
            doc.Overwrite(offset, [(byte)(old == 0x5A ? 0xA5 : 0x5A)]);
            EncodedFile.Save(doc.Current, baseAddress, settings, path);
        }

        string[] after = File.ReadAllLines(path);
        Assert.Equal(before.Length, after.Length);
        int[] changed = [.. Enumerable.Range(0, before.Length).Where(i => before[i] != after[i])];
        int line = Assert.Single(changed);
        Assert.Equal(before[line].Length, after[line].Length);
        if (format != FormatIds.Base64)
        {
            // データの 1 バイト (2 桁) とチェックサム (2 桁) だけが違う。
            int differences = Enumerable.Range(0, before[line].Length).Count(i => before[line][i] != after[line][i]);
            Assert.InRange(differences, 1, 4);
            Assert.NotEqual(before[line][^2..], after[line][^2..]);
        }
        else
        {
            Assert.Equal(0, line);
        }
    }

    [Theory]
    [InlineData("FF", 0xFF)]
    [InlineData("00", 0x00)]
    [InlineData(" 0x5a ", 0x5A)]
    [InlineData("A", 0x0A)]
    [InlineData("", 0xFF)]
    [InlineData("100", 0xFF)]
    [InlineData("zz", 0xFF)]
    [InlineData(null, 0xFF)]
    public void Gap_fill_setting_is_parsed_as_hex(string? text, int expected) =>
        Assert.Equal((byte)expected, EncodedFile.ParseGapFill(text));

    [Fact]
    public void Gaps_return_the_configured_fill_value_and_save_the_same()
    {
        // ENG-38 の仕様 3: 隙間の値は塗りつぶしの値 (既定 FF、設定可能)。値を変えても隙間は「データなし」で、保存には出ない。
        string path = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".hex");
        Directory.CreateDirectory(TempDirectory);
        File.Copy(TestDataCatalog.Get("TD-IHEX"), path);
        using ImportResult result = ImportFile(FormatIds.IntelHex, path, EncodedFile.OpenOptions(FormatIds.IntelHex, 0x00));
        EncodedFileSettings settings = result.Settings!;
        long baseAddress = result.BaseAddress;
        using Document doc = EncodedFile.CreateDocument(result, FormatIds.IntelHex, Options());
        (byte[] bytes, ByteState[] states) = ReadForDisplayWhenLoaded(doc.Current, 0x1F0, 0x20);
        Assert.All(states[0x10..], s => Assert.Equal(ByteState.NoData, s));
        Assert.All(bytes[0x10..], b => Assert.Equal(0x00, b));

        string saved = path + ".out";
        EncodedFile.Save(doc.Current, baseAddress, settings, saved);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(saved));
    }

    [Fact]
    public void Gaps_are_no_data_and_are_not_written()
    {
        (Document doc, EncodedFileSettings settings, long baseAddress) = Open(FormatIds.IntelHex, TestDataCatalog.Get("TD-IHEX"));
        using (doc)
        {
            Assert.False(doc.CanResize);
            (byte[] bytes, ByteState[] states) = ReadForDisplayWhenLoaded(doc.Current, 0x1F0, 0x20);
            Assert.All(states[..0x10], s => Assert.Equal(ByteState.Valid, s));
            Assert.All(states[0x10..], s => Assert.Equal(ByteState.NoData, s));
            Assert.All(bytes[0x10..], b => Assert.Equal(0xFF, b));

            // 隙間に書き込むとその範囲がデータありになる。
            doc.Overwrite(0x300, [0x12]);
            (_, ByteState[] after) = ReadForDisplayWhenLoaded(doc.Current, 0x300, 1);
            Assert.Equal(ByteState.Valid, after[0]);
            Assert.Contains((0x300L, 1L), doc.Current.DataRanges(0x200, 0x200));
        }
    }

    [Fact]
    public void Addresses_that_do_not_fit_the_format_are_rejected_before_writing()
    {
        using ImportResult result = Import(FormatIds.SRecord, File.ReadAllText(TestDataCatalog.Get("TD-TOOL-SREC-S19")));
        var settings = result.Settings! with { SRecordMode = SRecordAddressMode.S1 };
        using Document doc = EncodedFile.CreateDocument(result, FormatIds.SRecord, Options());
        Assert.Throws<FormatLimitException>(() => EncodedFile.Validate(doc.Current, 0xFFFF0, settings));
    }
}
