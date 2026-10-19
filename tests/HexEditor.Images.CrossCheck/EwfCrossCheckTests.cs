using System.Security.Cryptography;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Ewf;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Forensics.Images;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Forensics.Images.ImageTestSupport;

namespace HexEditor.Images.CrossCheck;

/// <summary>
/// 本アプリで作った E01 を libewf のツールで確かめる (基準の検証)。CI のジョブ ewf-crosscheck (Linux、ewf-tools) で実行する。
/// </summary>
public sealed class EwfCrossCheckTests
{
    private static AcquisitionOptions Options(string output) => new()
    {
        Output = output,
        Format = ImageFormat.E01,
        Verify = false,
        Case = new EwfCaseInfo { CaseNumber = "C-0001", EvidenceNumber = "E001", Examiner = "tester" },
    };

    [EwfToolsFact]
    [Trait("TC", "TC-FOR-20-03")]
    public async Task Ewfverify_succeeds_and_ewfinfo_shows_the_case_and_evidence_numbers()
    {
        using var dir = new ImageTempDir("hexeditor-ewf-crosscheck");
        using FileByteSource source = FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G"));
        byte[] md5 = Hash(source, HashAlgorithmName.MD5);
        string e01 = dir.File(Path.Combine("e01", "m.E01"));
        await ImageAcquisition.RunAsync(source, Info(source), Options(e01) with { Compression = EwfCompression.Fast });
        EwfTools.VerifyIfAvailable(e01, md5, "C-0001", "E001");
    }

    [EwfToolsFact]
    [Trait("TC", "TC-FOR-20-04")]
    public async Task Ewfinfo_reports_the_unreadable_sectors_from_error2()
    {
        using var dir = new ImageTempDir("hexeditor-ewf-crosscheck");
        var faulty = new FaultyByteSource(FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G")));
        using (faulty)
        {
            faulty.AddReadError(0x100000, 0x1000);
            string e01 = dir.File("r.E01");
            AcquisitionResult result = await ImageAcquisition.RunAsync(faulty, Info(faulty), Options(e01) with { Retries = 3 });
            Assert.Equal(8, result.BadSectorCount);
            EwfTools.CheckErrorsIfAvailable(e01, 2048, 8);
        }
    }

    [EwfToolsFact]
    [Trait("TC", "TC-FOR-21-04")]
    public async Task Ewfverify_fails_for_a_damaged_chunk()
    {
        using var dir = new ImageTempDir("hexeditor-ewf-crosscheck");
        using var source = new VirtualByteSource(8 * MiB, VirtualContent.Random, seed: 3);
        string e01 = dir.File("m.E01");
        await ImageAcquisition.RunAsync(source, Info(source), Options(e01) with { Compression = EwfCompression.None });
        (string file, long offset, int size, bool compressed) location;
        using (var probe = (EwfByteSource)ImageOpener.Open(e01))
        {
            location = probe.ChunkLocation(100)!.Value;
        }

        using (var stream = new FileStream(location.file, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = location.offset + 1234;
            int b = stream.ReadByte();
            stream.Position = location.offset + 1234;
            stream.WriteByte((byte)(b ^ 0xFF));
        }

        EwfTools.ReportsCorruptionIfAvailable(e01);
    }
}
