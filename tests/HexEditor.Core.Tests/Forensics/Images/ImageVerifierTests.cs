using System.Security.Cryptography;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Forensics.Images.ImageTestSupport;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>FOR-22 イメージの検証。</summary>
[Collection(SerialCollection.Name)]
public sealed class ImageVerifierTests
{
    private static AcquisitionOptions Options(string output, ImageFormat format, bool verify = true) => new()
    {
        Output = output,
        Format = format,
        Verify = verify,
        Hashes = [ImageHashes.Md5, ImageHashes.Sha256],
    };

    [Fact]
    [Trait("TC", "TC-FOR-22-01")]
    public async Task Images_just_acquired_verify_as_match_and_the_result_is_recorded()
    {
        using var dir = new ImageTempDir();
        var hooks = new RecordingHooks { Case = new ImageCaseInfo { CaseNumber = "C-0001", CaseName = "テストケース", Examiner = "tester", Folder = dir.Path } };
        ImageForensics.Hooks = hooks;
        try
        {
            using FileByteSource source = FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G"));
            AcquisitionResult raw = await ImageAcquisition.RunAsync(source, Info(source), Options(dir.File("m.dd"), ImageFormat.Raw));
            AcquisitionResult e01 = await ImageAcquisition.RunAsync(source, Info(source), Options(dir.File("m.E01"), ImageFormat.E01));

            // 自動の検証: MD5 と SHA-256 のどちらも一致。
            foreach (AcquisitionResult r in new[] { raw, e01 })
            {
                Assert.Equal(VerificationVerdict.Match, r.Verification!.VerdictOf(ImageHashes.Md5));
                Assert.Equal(VerificationVerdict.Match, r.Verification.VerdictOf(ImageHashes.Sha256));
            }

            // 「イメージを検証」で m.dd を検証する: 取得ログの値と比べて一致。
            using IImageSource image = ImageOpener.Open(dir.File("m.dd"));
            IReadOnlyList<ReferenceHash> refs = ImageVerifier.FindReferences(image, evidence: hooks.EvidenceHashesOf(dir.File("m.dd")));
            Assert.Contains(refs, r => r.Source == ReferenceSource.AcquisitionLog && r.Algorithm == ImageHashes.Sha256);
            VerificationResult manual = await ImageVerification.RunAsync(image, dir.File("m.dd"), [ImageHashes.Md5, ImageHashes.Sha256], refs);
            Assert.Equal(VerificationVerdict.Match, manual.VerdictOf(ImageHashes.Md5));
            Assert.Equal(VerificationVerdict.Match, manual.VerdictOf(ImageHashes.Sha256));

            // 監査ログに検証の結果、証拠一覧の「検証結果」に日時とともに「一致」。
            ImageAuditRecord audit = hooks.Records.Last(r => r.Operation == "image.verify");
            Assert.Equal("Match", audit.Parameters["verdict"]);
            (string target, VerificationResult recorded) = hooks.Verifications.Last();
            Assert.Equal(dir.File("m.dd"), target);
            Assert.Equal(VerificationVerdict.Match, recorded.Overall);
            Assert.NotEqual(default, recorded.At);
        }
        finally
        {
            ImageForensics.Hooks = NullImageForensicHooks.Instance;
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-22-02")]
    public async Task Changing_one_byte_makes_every_algorithm_mismatch()
    {
        using var dir = new ImageTempDir();
        using (FileByteSource source = FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G")))
        {
            await ImageAcquisition.RunAsync(source, Info(source), Options(dir.File("m.dd"), ImageFormat.Raw, verify: false));
        }

        using (FileStream stream = new(dir.File("m.dd"), FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 0x20000000;
            int b = stream.ReadByte();
            stream.Position = 0x20000000;
            stream.WriteByte((byte)(b ^ 0x01));
        }

        using IImageSource image = ImageOpener.Open(dir.File("m.dd"));
        VerificationResult result = ImageVerifier.Verify(image, [ImageHashes.Md5, ImageHashes.Sha256], ImageVerifier.FindReferences(image));
        Assert.Equal(VerificationVerdict.Mismatch, result.VerdictOf(ImageHashes.Md5));
        Assert.Equal(VerificationVerdict.Mismatch, result.VerdictOf(ImageHashes.Sha256));
        Assert.Equal(VerificationVerdict.Mismatch, result.Overall);
    }

    [Fact]
    [Trait("TC", "TC-FOR-22-03")]
    public void A_raw_image_without_a_log_has_no_reference_until_a_value_is_entered()
    {
        using var dir = new ImageTempDir();
        string plain = dir.File("plain.dd");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), plain);
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plain)));
        using IImageSource image = ImageOpener.Open(plain);

        VerificationResult none = ImageVerifier.Verify(image, [ImageHashes.Sha256], ImageVerifier.FindReferences(image));
        Assert.Equal(VerificationVerdict.NoReference, none.VerdictOf(ImageHashes.Sha256));
        Assert.Equal(VerificationVerdict.NoReference, none.Overall);

        VerificationResult match = ImageVerifier.Verify(image, [ImageHashes.Sha256], ImageVerifier.FindReferences(image, sha256));
        Assert.Equal(VerificationVerdict.Match, match.VerdictOf(ImageHashes.Sha256));

        string changed = sha256[..^1] + (sha256[^1] == '0' ? '1' : '0');
        VerificationResult mismatch = ImageVerifier.Verify(image, [ImageHashes.Sha256], ImageVerifier.FindReferences(image, changed));
        Assert.Equal(VerificationVerdict.Mismatch, mismatch.VerdictOf(ImageHashes.Sha256));
    }

    [Fact]
    public void Read_errors_are_reported_as_cannot_verify_with_the_range()
    {
        var faulty = new FaultyByteSource(new VirtualByteSource(2 * MiB, VirtualContent.Offset64));
        faulty.AddReadError(MiB, 512);
        using (faulty)
        {
            byte[] value = new byte[32];
            VerificationResult result = ImageVerifier.Verify(faulty, [ImageHashes.Sha256], [new ReferenceHash(ImageHashes.Sha256, value, ReferenceSource.User)]);
            Assert.Equal(VerificationVerdict.ReadError, result.Overall);
            Assert.Equal(VerificationVerdict.ReadError, result.VerdictOf(ImageHashes.Sha256));
            Assert.Equal(MiB, result.ReadErrors.Single().Offset);
        }
    }

    [Fact]
    public void Rereading_the_source_disk_compares_three_values()
    {
        using var image = new VirtualByteSource(MiB, VirtualContent.Random, seed: 3);
        using var disk = new VirtualByteSource(MiB, VirtualContent.Random, seed: 3);
        byte[] sha = Hash(image, HashAlgorithmName.SHA256);
        VerificationResult result = ImageVerifier.Verify(image, [ImageHashes.Sha256], [new ReferenceHash(ImageHashes.Sha256, sha, ReferenceSource.Stored)], sourceDisk: disk);
        AlgorithmVerification row = result.Algorithms.Single();
        Assert.Equal(2, row.Comparisons.Count);
        Assert.Contains(row.Comparisons, c => c.Reference.Source == ReferenceSource.SourceDisk && c.Match);
        Assert.Equal(VerificationVerdict.Match, row.Verdict);

        using var other = new VirtualByteSource(MiB, VirtualContent.Random, seed: 4);
        VerificationResult changed = ImageVerifier.Verify(image, [ImageHashes.Sha256], [new ReferenceHash(ImageHashes.Sha256, sha, ReferenceSource.Stored)], sourceDisk: other);
        Assert.Equal(VerificationVerdict.Mismatch, changed.Overall);
    }

    [Theory]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e", "MD5")]
    [InlineData("DA39A3EE 5E6B4B0D 3255BFEF 95601890 AFD80709", "SHA-1")]
    [InlineData("0x" + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "SHA-256")]
    public void User_values_are_parsed_by_length(string text, string algorithm)
    {
        Assert.Equal(algorithm, ImageVerifier.ParseUserHash(text)!.Algorithm);
        Assert.Null(ImageVerifier.ParseUserHash(text + "0"));
        Assert.Null(ImageVerifier.ParseUserHash("zz" + text[2..]));
    }
}
