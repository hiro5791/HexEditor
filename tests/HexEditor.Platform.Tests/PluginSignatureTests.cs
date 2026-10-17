using HexEditor.Platform.Security;
using HexEditor.TestData;
using static HexEditor.Platform.Security.AuthenticodeVerifier;

namespace HexEditor.Platform.Tests;

/// <summary>
/// プラグインの署名の確認 (08 の AUTO-35) を Windows の WinVerifyTrust で確かめる。証明書ストアには何も入れず、失効の確認は
/// キャッシュだけにする (通信しない)。テスト用の証明書で署名したファイルは、ルートが信頼されていないことだけを理由に拒否されることを確かめる
/// (署名の形式が正しいことの確認。アプリのテストはこの署名を、テストのルートだけを信頼する検証で使う)。
/// </summary>
public sealed class PluginSignatureTests : IDisposable
{
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int TrustEBadDigest = unchecked((int)0x80096010);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HexEditor", "plugin-signature-" + Guid.NewGuid().ToString("N"));

    public PluginSignatureTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Copy(string source)
    {
        string target = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".dll");
        File.Copy(source, target);
        return target;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-35-01")]
    public void A_validly_signed_file_reports_its_publisher()
    {
        SignatureResult r = VerifyPluginFile(typeof(object).Assembly.Location, offline: true);
        Assert.Equal(SignatureStatus.Valid, r.Status);
        Assert.False(string.IsNullOrEmpty(r.Publisher));
        Assert.Equal(64, r.Thumbprint!.Length);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-35-02")]
    public void One_changed_byte_in_a_signed_file_makes_the_signature_invalid()
    {
        string copy = Copy(typeof(object).Assembly.Location);
        Assert.Equal(SignatureStatus.Valid, VerifyPluginFile(copy, offline: true).Status);
        byte[] bytes = File.ReadAllBytes(copy);
        bytes[bytes.Length / 3] ^= 0x01;
        File.WriteAllBytes(copy, bytes);
        SignatureResult r = VerifyPluginFile(copy, offline: true);
        Assert.Equal(SignatureStatus.Invalid, r.Status);
        Assert.NotEqual(0, r.Code);
    }

    [Fact]
    public void Unsigned_files_are_unsigned_and_broken_files_are_not_valid()
    {
        Assert.Equal(SignatureStatus.Unsigned, VerifyPluginFile(typeof(PluginSignatureTests).Assembly.Location, offline: true).Status);
        string garbage = Path.Combine(_folder, "garbage.dll");
        File.WriteAllBytes(garbage, [1, 2, 3, 4]);
        Assert.NotEqual(SignatureStatus.Valid, VerifyPluginFile(garbage, offline: true).Status);
        Assert.Equal(SignatureStatus.Invalid, VerifyPluginFile(garbage + ".missing", offline: true).Status);
    }

    [Fact]
    public void Test_signatures_are_well_formed_and_refused_only_for_the_untrusted_root()
    {
        using TestAuthenticode.Certificates certificates = TestAuthenticode.CreateCertificates();
        string signed = Copy(typeof(PluginSignatureTests).Assembly.Location);
        TestAuthenticode.Sign(signed, certificates);
        Assert.True(HasCertificateTable(signed));

        // テストのルートを信頼していない Windows では「信頼されていないルート」(形式とハッシュは正しい)。
        SignatureResult r = VerifyPluginFile(signed, offline: true);
        Assert.Equal(SignatureStatus.Invalid, r.Status);
        Assert.Equal(CertEUntrustedRoot, r.Code);
        Assert.Equal(TestAuthenticode.State.Valid, TestAuthenticode.Verify(signed, certificates.Root).State);

        // 署名の対象の 1 バイトを変えると、ハッシュの不一致。
        TestAuthenticode.Tamper(signed);
        SignatureResult tampered = VerifyPluginFile(signed, offline: true);
        Assert.Equal(SignatureStatus.Invalid, tampered.Status);
        Assert.Equal(TrustEBadDigest, tampered.Code);
        Assert.Equal(TestAuthenticode.State.Invalid, TestAuthenticode.Verify(signed, certificates.Root).State);
    }

    [Fact]
    public void The_test_verifier_computes_the_same_pe_hash_as_windows_for_real_signatures()
    {
        // 実際の Microsoft の署名で、テスト用の検証の PE のハッシュの計算が Windows と一致することを確かめる (ルートは別なのでチェーンで無効になる)。
        using TestAuthenticode.Certificates certificates = TestAuthenticode.CreateCertificates();
        TestAuthenticode.Result r = TestAuthenticode.Verify(typeof(object).Assembly.Location, certificates.Root);
        Assert.Equal(TestAuthenticode.State.Invalid, r.State);
        Assert.StartsWith("chain:", r.Problem, StringComparison.Ordinal);
    }
}
