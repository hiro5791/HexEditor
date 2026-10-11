using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Formats;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Formats;

/// <summary>Intel HEX (TOOL-05) と Motorola S-record (TOOL-06) の読み書き。</summary>
public sealed class RecordFormatTests
{
    private static byte[] Firmware()
    {
        byte[] data = new byte[128 * 1024];
        TestDataCatalog.Random(TestDataCatalog.FirmwareSeed, 0, data);
        return data;
    }

    private static readonly byte[] Bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static ExportOptions Ihex(int recordLength, IntelHexAddressMode mode, long? start = null) => new()
    {
        Format = FormatIds.IntelHex,
        Records = new RecordExportOptions { RecordLength = recordLength, IntelMode = mode },
        StartAddress = start,
    };

    [Fact]
    public void Intel_hex_records_have_checksums_and_end_record()
    {
        string text = Export([0xDE, 0xAD, 0xBE, 0xEF], Ihex(16, IntelHexAddressMode.Auto, 0x100));
        Assert.Equal(":04010000DEADBEEFC3\r\n:00000001FF\r\n", text);
    }

    [Fact]
    public void Imported_test_data_matches_the_records()
    {
        using ImportResult result = ImportFile(FormatIds.IntelHex, TestDataCatalog.Get("TD-IHEX"));
        Assert.Equal(0, result.Issues.Count);
        Assert.Equal(0x0800_0000, result.BaseAddress);
        byte[] bytes = Bytes(result);
        foreach ((long address, byte[] data) in TestDataCatalog.FirmwareData())
        {
            Assert.Equal(data, bytes.AsSpan((int)(address - 0x0800_0000), data.Length).ToArray());
        }

        // 隙間 (0x08000200〜0x080003FF) は塗りつぶしの値 FF で、データなし。
        Assert.All(bytes.AsSpan(0x200, 0x200).ToArray(), b => Assert.Equal(0xFF, b));
        Assert.Contains((0x200L, 0x200L), result.Image!.GapsIn(0, result.Image.Length));
        Assert.Equal(IntelHexAddressMode.I32Hex, result.Settings!.IntelMode);
        Assert.True(result.Settings.LeadingExtendedRecord);
        Assert.Equal(16, result.Settings.RecordLength);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-05-03")]
    public void Exported_intel_hex_imports_back_to_the_same_bytes()
    {
        foreach (byte[] data in new[] { Bytes256, Firmware() })
        {
            foreach (IntelHexAddressMode mode in Enum.GetValues<IntelHexAddressMode>())
            {
                if (mode == IntelHexAddressMode.I8Hex && data.Length > 0x10000)
                {
                    continue;
                }

                foreach (int length in new[] { 1, 16, 32, 255 })
                {
                    long start = mode == IntelHexAddressMode.I32Hex ? 0x0800_0000 : mode == IntelHexAddressMode.I16Hex ? 0x2_0000 : 0;
                    string text = Export(data, Ihex(length, mode, start));
                    using ImportResult result = Import(FormatIds.IntelHex, text);
                    Assert.Equal(0, result.Issues.ErrorCount);
                    Assert.Equal(start, result.BaseAddress);
                    Assert.Equal(data, Bytes(result));
                }
            }
        }
    }

    /// <summary>長さ 0〜200,000 の無作為なバイト列 (種から作る)。</summary>
    internal static Arbitrary<byte[]> Data() =>
        (from length in Gen.Choose(0, 200_000)
         from seed in ArbMap.Default.GeneratorFor<int>()
         select Make(length, seed)).ToArbitrary();

    private static byte[] Make(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Property(MaxTest = 500)]
    [Trait(TC, "TC-TOOL-05-03")]
    public Property Random_intel_hex_round_trips() => Prop.ForAll(Data(), Gen.Choose(0, 0x7FFF).ToArbitrary(), Gen.Elements(1, 16, 32, 255).ToArbitrary(),
        (data, page, length) =>
        {
            long start = (long)page << 17; // 0〜0xFFFE0000
            string text = Export(data, Ihex(length, IntelHexAddressMode.Auto, start));
            using ImportResult result = Import(FormatIds.IntelHex, text);
            return result.Issues.ErrorCount == 0 && Bytes(result).AsSpan().SequenceEqual(data)
                && (data.Length == 0 || result.BaseAddress == start);
        });

    [Fact]
    [Trait(TC, "TC-TOOL-05-04")]
    public void Extended_linear_records_appear_at_64k_boundaries()
    {
        string text = Export(Firmware(), Ihex(16, IntelHexAddressMode.Auto, 0x0000FFF8));
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // 範囲が 64 KB を超えるので I32HEX。0xFFF8 のレコードは 8 バイトで終わる。
        Assert.StartsWith(":08FFF800", lines[0]);
        int ext1 = Array.IndexOf(lines, ":020000040001F9");
        int ext2 = Array.IndexOf(lines, ":020000040002F8");
        Assert.True(ext1 > 0 && ext2 > ext1);
        Assert.StartsWith(":10000000", lines[ext1 + 1]);
        Assert.StartsWith(":10000000", lines[ext2 + 1]);

        // どのデータレコードも 64 KB の境界をまたがない。
        long upper = 0;
        foreach (string line in lines)
        {
            byte[] b = Convert.FromHexString(line[1..]);
            if (b[3] == 4)
            {
                upper = (b[4] << 8 | b[5]) << 16;
            }
            else if (b[3] == 0)
            {
                long address = upper + (b[1] << 8 | b[2]);
                Assert.Equal(address >> 16, (address + b[0] - 1) >> 16);
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-TOOL-05-05")]
    public void Export_matches_the_objcopy_reference()
    {
        var options = new ExportOptions
        {
            Format = FormatIds.IntelHex,
            Records = new RecordExportOptions { RecordLength = 16, IntelMode = IntelHexAddressMode.I32Hex, UpperCase = true },
            StartAddress = 0x0800_0000,
        };
        byte[] expected = File.ReadAllBytes(TestDataCatalog.Get("TD-TOOL-IHEX-OBJCOPY"));
        Assert.Equal(expected, ExportBytes(Firmware(), options));
    }

    [Fact]
    public void Checksum_error_is_listed_with_line_and_values()
    {
        using ImportResult result = ImportFile(FormatIds.IntelHex, TestDataCatalog.Get("TD-TOOL-IHEX-BADSUM"));
        ImportIssue issue = Assert.Single(result.Issues.Items);
        Assert.Equal(5, issue.Line);
        Assert.Equal(ImportIssueKind.Checksum, issue.Kind);
        Assert.NotEqual(issue.Expected, issue.Actual);
    }

    [Fact]
    public void Sparse_i32hex_uses_little_memory()
    {
        using ImportResult result = ImportFile(FormatIds.IntelHex, TestDataCatalog.Get("TD-TOOL-IHEX-SPARSE"),
            new ImportOptions { Placement = AddressPlacement.Absolute });
        SparseImage image = result.Image!;
        Assert.Equal(0xFFFF0010, image.Length);
        Assert.Equal(32, image.DataBytes);
        byte[] b = new byte[16];
        image.Read(0, b);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), b);
        image.Read(0xFFFF0000, b);
        Assert.Equal(Enumerable.Range(0xF0, 16).Select(i => (byte)i), b);
        byte[] one = new byte[1];
        image.Read(0x7FFFFFFF, one);
        Assert.Equal(0xFF, one[0]);
    }

    [Fact]
    public void Overlapping_records_use_later_data_and_are_listed()
    {
        static string Line(string body)
        {
            byte[] b = Convert.FromHexString(body);
            byte sum = 0;
            foreach (byte x in b)
            {
                sum += x;
            }

            return ":" + body + ((byte)-sum).ToString("X2") + "\r\n";
        }

        string text = Line("0400000011223344") + Line("02000200AABB") + ":00000001FF\r\n";
        using ImportResult strict = Import(FormatIds.IntelHex, text);
        Assert.Equal(ImportIssueKind.Overlap, Assert.Single(strict.Issues.Items).Kind);
        Assert.Equal(new byte[] { 0x11, 0x22, 0xAA, 0xBB }, Bytes(strict));
        using ImportResult later = Import(FormatIds.IntelHex, text, new ImportOptions { PreferLater = true });
        Assert.Equal(0, later.Issues.Count);
    }

    // ---- S-record ----

    [Theory]
    [Trait(TC, "TC-TOOL-06-01")]
    [InlineData("TD-TOOL-SREC-S19", 0x1000L, SRecordAddressMode.S1)]
    [InlineData("TD-TOOL-SREC-S28", 0x010000L, SRecordAddressMode.S2)]
    [InlineData("TD-TOOL-SREC-S37", 0x0800_0000L, SRecordAddressMode.S3)]
    public void S_record_files_import(string id, long address, SRecordAddressMode mode)
    {
        using ImportResult result = ImportFile(FormatIds.SRecord, TestDataCatalog.Get(id));
        byte[] expected = new byte[4096];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, expected);
        Assert.Equal(expected, Bytes(result));
        Assert.Equal(address, result.BaseAddress);
        Assert.Equal("HEXEDTEST", result.Header);
        Assert.Equal(address, result.StartAddress);
        Assert.Equal(0, result.Issues.Count);
        Assert.Equal(mode, result.Settings!.SRecordMode);
    }

    [Fact]
    public void Record_count_mismatch_is_a_warning()
    {
        using ImportResult result = ImportFile(FormatIds.SRecord, TestDataCatalog.Get("TD-TOOL-SREC-BADCOUNT"));
        ImportIssue issue = Assert.Single(result.Issues.Items);
        Assert.Equal(ImportIssueKind.CountMismatch, issue.Kind);
        Assert.True(issue.IsWarning);
        Assert.Equal("257", issue.Expected);
        Assert.Equal("256", issue.Actual);
        Assert.False(result.HasErrors);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-06-03")]
    public void Exported_s_record_imports_back_to_the_same_bytes()
    {
        foreach (byte[] data in new[] { Bytes256, Firmware() })
        {
            foreach (SRecordAddressMode mode in Enum.GetValues<SRecordAddressMode>())
            {
                if (mode == SRecordAddressMode.S1 && data.Length > 0x10000)
                {
                    continue;
                }

                int max = mode switch { SRecordAddressMode.S1 => 252, SRecordAddressMode.S2 => 251, _ => 250 };
                foreach (int length in new[] { 1, 16, 32, max })
                {
                    foreach (bool count in new[] { true, false })
                    {
                        long start = mode switch { SRecordAddressMode.S2 => 0x10000, SRecordAddressMode.S3 => 0x0800_0000, _ => 0 };
                        var options = new ExportOptions
                        {
                            Format = FormatIds.SRecord,
                            Records = new RecordExportOptions { RecordLength = length, SRecordMode = mode, WriteCount = count },
                            StartAddress = start,
                        };
                        using ImportResult result = Import(FormatIds.SRecord, Export(data, options));
                        Assert.Equal(0, result.Issues.Count);
                        Assert.Equal(start, result.BaseAddress);
                        Assert.Equal(data, Bytes(result));
                    }
                }
            }
        }
    }

    [Property(MaxTest = 500)]
    [Trait(TC, "TC-TOOL-06-03")]
    public Property Random_s_records_round_trip() => Prop.ForAll(Data(), Gen.Choose(0, 0x7FFF).ToArbitrary(), (data, page) =>
    {
        long start = (long)page << 17;
        var options = new ExportOptions { Format = FormatIds.SRecord, StartAddress = start };
        using ImportResult result = Import(FormatIds.SRecord, Export(data, options));
        return result.Issues.Count == 0 && Bytes(result).AsSpan().SequenceEqual(data) && (data.Length == 0 || result.BaseAddress == start);
    });

    [Fact]
    [Trait(TC, "TC-TOOL-06-04")]
    public void Export_matches_the_srec_cat_reference()
    {
        var options = new ExportOptions
        {
            Format = FormatIds.SRecord,
            Records = new RecordExportOptions
            {
                RecordLength = 32,
                SRecordMode = SRecordAddressMode.S3,
                Header = "HEXEDTEST",
                WriteCount = true,
                ExecAddress = 0x0800_0000,
            },
            StartAddress = 0x0800_0000,
        };
        byte[] expected = File.ReadAllBytes(TestDataCatalog.Get("TD-TOOL-SREC-SRECCAT"));
        Assert.Equal(expected, ExportBytes(Firmware(), options));
    }

    [Fact]
    public void Addresses_beyond_the_format_limit_are_rejected()
    {
        var options = Ihex(16, IntelHexAddressMode.I8Hex, 0xFFF0);
        Assert.Throws<FormatLimitException>(() => Export(new byte[32], options));
        var srec = new ExportOptions { Format = FormatIds.SRecord, Records = new RecordExportOptions { SRecordMode = SRecordAddressMode.S2 }, StartAddress = 0xFFFFF0 };
        Assert.Throws<FormatLimitException>(() => Export(new byte[32], srec));
    }

    [Fact]
    public void Gap_omission_skips_long_runs_of_the_fill_value()
    {
        byte[] data = [1, 2, .. Enumerable.Repeat((byte)0xFF, 20), 3, 0xFF, 0xFF, 4];
        var options = Ihex(16, IntelHexAddressMode.Auto, 0) with
        {
            Records = new RecordExportOptions { OmitGaps = new GapOmission(0xFF, 16) },
        };
        string text = Export(data, options);
        using ImportResult result = Import(FormatIds.IntelHex, text, new ImportOptions { Placement = AddressPlacement.Absolute });
        Assert.Equal(20, result.GapBytes);
        Assert.Equal(data, Bytes(result));
    }
}
