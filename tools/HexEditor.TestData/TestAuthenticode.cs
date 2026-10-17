using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HexEditor.TestData;

/// <summary>
/// テスト用の Authenticode の署名と、その検証 (08 の AUTO-35 のテスト。TD-AUTO-PLG-SIGNED、TD-AUTO-PLG-SIGNED-TAMPERED)。
/// <list type="bullet">
/// <item>証明書と鍵はプロセスの中で作り、Windows の証明書ストアには入れない (どこにも信頼させない)。</item>
/// <item>署名は PE の Authenticode の形式 (WIN_CERTIFICATE の PKCS #7 SignedData、SpcIndirectDataContent、SHA-256) をそのまま作る。
/// 形式が正しいことは、Windows の WinVerifyTrust がルートが信頼されていないこと (CERT_E_UNTRUSTEDROOT) だけを理由に拒否することで確かめる
/// (Platform のテスト)。</item>
/// <item>検証 (<see cref="Verify"/>) は、テストで作ったルート証明書だけを信頼して同じ手順 (PE のハッシュ、署名、証明書のチェーン、コード署名の用途) を
/// 確かめる。製品は WinVerifyTrust を使い、これは使わない。</item>
/// </list>
/// </summary>
public static class TestAuthenticode
{
    public const string PublisherName = "HexEditor Test Publisher";

    private const string OidSignedData = "1.2.840.113549.1.7.2";
    private const string OidSpcIndirectData = "1.3.6.1.4.1.311.2.1.4";
    private const string OidSpcPeImageData = "1.3.6.1.4.1.311.2.1.15";
    private const string OidSpcSpOpusInfo = "1.3.6.1.4.1.311.2.1.12";
    private const string OidSpcStatementType = "1.3.6.1.4.1.311.2.1.11";
    private const string OidIndividualCodeSigning = "1.3.6.1.4.1.311.2.1.21";
    private const string OidContentType = "1.2.840.113549.1.9.3";
    private const string OidMessageDigest = "1.2.840.113549.1.9.4";
    private const string OidSha256 = "2.16.840.1.101.3.4.2.1";
    private const string OidSha384 = "2.16.840.1.101.3.4.2.2";
    private const string OidSha512 = "2.16.840.1.101.3.4.2.3";
    private const string OidRsa = "1.2.840.113549.1.1.1";
    private const string OidSha256Rsa = "1.2.840.113549.1.1.11";
    private const string OidEcdsaSha256 = "1.2.840.10045.4.3.2";
    private const string OidCodeSigning = "1.3.6.1.5.5.7.3.3";

    /// <summary>テスト用のルート証明書と、それが発行したコード署名の証明書 (鍵付き)。</summary>
    public sealed record Certificates(X509Certificate2 Root, X509Certificate2 Signer) : IDisposable
    {
        public void Dispose()
        {
            Root.Dispose();
            Signer.Dispose();
        }
    }

    /// <summary>証明書を作る (メモリの中だけ)。<paramref name="expired"/> なら、有効期限が過去の署名用の証明書にする。</summary>
    public static Certificates CreateCertificates(string publisher = PublisherName, bool expired = false)
    {
        using RSA rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=HexEditor Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        X509Certificate2 root = rootRequest.CreateSelfSigned(now.AddDays(-30), now.AddYears(5));

        RSA signerKey = RSA.Create(2048);
        var signerRequest = new CertificateRequest($"CN={publisher}", signerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        signerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        signerRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(OidCodeSigning)], false));
        signerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(signerRequest.PublicKey, false));
        DateTimeOffset notBefore = expired ? now.AddDays(-20) : now.AddDays(-1);
        DateTimeOffset notAfter = expired ? now.AddDays(-10) : now.AddYears(2);
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using X509Certificate2 signerPublic = signerRequest.Create(root, notBefore, notAfter, serial);
        X509Certificate2 signer = signerPublic.CopyWithPrivateKey(signerKey);
        return new Certificates(root, signer);
    }

    /// <summary>証明書の SHA-256 (アプリが発行者の識別に使うものと同じ)。</summary>
    public static string Thumbprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    // ======================== PE ========================

    private readonly record struct PeLayout(int ChecksumOffset, int SecurityEntryOffset, int CertificateOffset, int CertificateSize);

    private static PeLayout Layout(ReadOnlySpan<byte> file)
    {
        if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z')
        {
            throw new InvalidDataException("Not a PE file.");
        }

        int pe = BinaryPrimitives.ReadInt32LittleEndian(file[0x3C..]);
        if (pe < 0 || pe + 24 > file.Length || !file.Slice(pe, 4).SequenceEqual("PE\0\0"u8))
        {
            throw new InvalidDataException("Not a PE file.");
        }

        int optional = pe + 24;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(file[optional..]);
        int directories = magic switch
        {
            0x10B => optional + 96,
            0x20B => optional + 112,
            _ => throw new InvalidDataException("Unknown optional header."),
        };
        int count = BinaryPrimitives.ReadInt32LittleEndian(file[(directories - 4)..]);
        if (count < 5 || directories + 5 * 8 > file.Length)
        {
            throw new InvalidDataException("No security directory.");
        }

        int security = directories + 4 * 8;
        int certOffset = BinaryPrimitives.ReadInt32LittleEndian(file[security..]);
        int certSize = BinaryPrimitives.ReadInt32LittleEndian(file[(security + 4)..]);
        return new PeLayout(optional + 64, security, certOffset, certSize);
    }

    /// <summary>Authenticode の PE のハッシュ (チェックサム、セキュリティディレクトリの項目、証明書の表を除いたファイル全体)。</summary>
    private static byte[] PeHash(ReadOnlySpan<byte> file, PeLayout layout, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        int end = layout.CertificateSize > 0 && layout.CertificateOffset > 0 ? layout.CertificateOffset : file.Length;
        if (end > file.Length || end < layout.SecurityEntryOffset + 8)
        {
            throw new InvalidDataException("The certificate table is out of the file.");
        }

        hash.AppendData(file[..layout.ChecksumOffset]);
        hash.AppendData(file[(layout.ChecksumOffset + 4)..layout.SecurityEntryOffset]);
        hash.AppendData(file[(layout.SecurityEntryOffset + 8)..end]);
        if (layout.CertificateSize > 0 && (long)layout.CertificateOffset + layout.CertificateSize < file.Length)
        {
            hash.AppendData(file[(layout.CertificateOffset + layout.CertificateSize)..]);
        }

        return hash.GetHashAndReset();
    }

    // ======================== 署名 ========================

    /// <summary>ファイルに署名する (既にある署名は置き換える)。</summary>
    public static void Sign(string path, Certificates certificates)
    {
        byte[] original = File.ReadAllBytes(path);
        PeLayout layout = Layout(original);
        int length = layout.CertificateSize > 0 && layout.CertificateOffset > 0 ? layout.CertificateOffset : original.Length;
        int padded = (length + 7) & ~7;
        byte[] file = new byte[padded];
        original.AsSpan(0, length).CopyTo(file);

        // 署名の前の状態 (証明書の表なし) でハッシュを計算する。
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(layout.SecurityEntryOffset), 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(layout.SecurityEntryOffset + 4), 0);
        byte[] digest = PeHash(file, layout with { CertificateOffset = 0, CertificateSize = 0 }, HashAlgorithmName.SHA256);
        byte[] pkcs7 = CreateSignedData(digest, certificates);

        int certificateLength = (8 + pkcs7.Length + 7) & ~7;
        byte[] result = new byte[file.Length + certificateLength];
        file.CopyTo(result, 0);
        Span<byte> win = result.AsSpan(file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(win, certificateLength);
        BinaryPrimitives.WriteUInt16LittleEndian(win[4..], 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(win[6..], 0x0002);
        pkcs7.CopyTo(win[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(layout.SecurityEntryOffset), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(layout.SecurityEntryOffset + 4), certificateLength);
        File.WriteAllBytes(path, result);
    }

    /// <summary>署名の対象の範囲の 1 バイトを書き換える (TD-AUTO-PLG-SIGNED-TAMPERED)。PE のヘッダより後の、コードの部分。</summary>
    public static void Tamper(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        PeLayout layout = Layout(file);
        int at = Math.Min(file.Length / 2, (layout.CertificateOffset > 0 ? layout.CertificateOffset : file.Length) - 16);
        file[at] ^= 0x01;
        File.WriteAllBytes(path, file);
    }

    private static void WriteAlgorithm(AsnWriter w, string oid)
    {
        w.PushSequence();
        w.WriteObjectIdentifier(oid);
        w.WriteNull();
        w.PopSequence();
    }

    private static byte[] SpcIndirectDataContent(byte[] digest)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        w.PushSequence();
        w.PushSequence();
        w.WriteObjectIdentifier(OidSpcPeImageData);
        w.PushSequence();
        w.WriteBitString([]);
        w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true));
        w.WriteCharacterString(UniversalTagNumber.BMPString, "<<<Obsolete>>>", new Asn1Tag(TagClass.ContextSpecific, 0));
        w.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 2, isConstructed: true));
        w.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        w.PopSequence();
        w.PopSequence();
        w.PushSequence();
        WriteAlgorithm(w, OidSha256);
        w.WriteOctetString(digest);
        w.PopSequence();
        w.PopSequence();
        return w.Encode();
    }

    /// <summary>DER の値の部分 (外側のタグと長さを除く。Authenticode の messageDigest はここのハッシュ)。</summary>
    private static ReadOnlySpan<byte> ContentsOf(byte[] der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        _ = reader.PeekTag();
        ReadOnlyMemory<byte> contents = reader.PeekContentBytes();
        return contents.Span;
    }

    private static byte[] SignedAttributes(byte[] contentDigest, Asn1Tag? tag)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        w.PushSetOf(tag);
        WriteAttribute(w, OidContentType, a => a.WriteObjectIdentifier(OidSpcIndirectData));
        WriteAttribute(w, OidMessageDigest, a => a.WriteOctetString(contentDigest));
        WriteAttribute(w, OidSpcSpOpusInfo, a =>
        {
            a.PushSequence();
            a.PopSequence();
        });
        WriteAttribute(w, OidSpcStatementType, a =>
        {
            a.PushSequence();
            a.WriteObjectIdentifier(OidIndividualCodeSigning);
            a.PopSequence();
        });
        w.PopSetOf(tag);
        return w.Encode();
    }

    private static void WriteAttribute(AsnWriter w, string oid, Action<AsnWriter> value)
    {
        w.PushSequence();
        w.WriteObjectIdentifier(oid);
        w.PushSetOf();
        value(w);
        w.PopSetOf();
        w.PopSequence();
    }

    private static byte[] CreateSignedData(byte[] peDigest, Certificates certificates)
    {
        byte[] content = SpcIndirectDataContent(peDigest);
        byte[] contentDigest = SHA256.HashData(ContentsOf(content));
        byte[] toSign = SignedAttributes(contentDigest, null);
        using RSA key = certificates.Signer.GetRSAPrivateKey()!;
        byte[] signature = key.SignData(toSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var w = new AsnWriter(AsnEncodingRules.DER);
        w.PushSequence();
        w.WriteObjectIdentifier(OidSignedData);
        w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        w.PushSequence();
        w.WriteInteger(1);
        w.PushSetOf();
        WriteAlgorithm(w, OidSha256);
        w.PopSetOf();
        w.PushSequence();
        w.WriteObjectIdentifier(OidSpcIndirectData);
        w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        w.WriteEncodedValue(content);
        w.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        w.PopSequence();
        Asn1Tag certificatesTag = new(TagClass.ContextSpecific, 0, isConstructed: true);
        w.PushSetOf(certificatesTag);
        w.WriteEncodedValue(certificates.Signer.RawData);
        w.WriteEncodedValue(certificates.Root.RawData);
        w.PopSetOf(certificatesTag);
        w.PushSetOf();
        w.PushSequence();
        w.WriteInteger(1);
        w.PushSequence();
        w.WriteEncodedValue(certificates.Signer.IssuerName.RawData);
        w.WriteInteger(certificates.Signer.SerialNumberBytes.Span);
        w.PopSequence();
        WriteAlgorithm(w, OidSha256);
        w.WriteEncodedValue(SignedAttributes(contentDigest, new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)));
        WriteAlgorithm(w, OidRsa);
        w.WriteOctetString(signature);
        w.PopSequence();
        w.PopSetOf();
        w.PopSequence();
        w.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
        w.PopSequence();
        return w.Encode();
    }

    // ======================== 検証 ========================

    /// <summary>検証の結果。</summary>
    public enum State
    {
        Unsigned,
        Valid,
        Invalid,
    }

    public sealed record Result(State State, string? Publisher = null, string? Thumbprint = null, string? Problem = null);

    /// <summary>
    /// <paramref name="trustedRoot"/> だけを信頼して署名を確かめる。形式の誤り・ハッシュの不一致・署名の誤り・チェーンの失敗 (期限切れを含む)・
    /// コード署名の用途がないものは無効。確認の途中の例外も無効として扱う (「有効」にはしない)。
    /// </summary>
    public static Result Verify(string path, X509Certificate2 trustedRoot)
    {
        try
        {
            byte[] file = File.ReadAllBytes(path);
            PeLayout layout;
            try
            {
                layout = Layout(file);
            }
            catch (InvalidDataException)
            {
                return new Result(State.Unsigned);
            }

            if (layout.CertificateOffset == 0 || layout.CertificateSize == 0)
            {
                return new Result(State.Unsigned);
            }

            if (layout.CertificateOffset < 0 || layout.CertificateSize < 8 || (long)layout.CertificateOffset + layout.CertificateSize > file.Length)
            {
                return new Result(State.Invalid, Problem: "certificate table out of range");
            }

            ReadOnlySpan<byte> win = file.AsSpan(layout.CertificateOffset, layout.CertificateSize);
            int length = BinaryPrimitives.ReadInt32LittleEndian(win);
            if (length < 8 || length > win.Length || BinaryPrimitives.ReadUInt16LittleEndian(win[4..]) != 0x0200 || BinaryPrimitives.ReadUInt16LittleEndian(win[6..]) != 0x0002)
            {
                return new Result(State.Invalid, Problem: "unsupported WIN_CERTIFICATE");
            }

            return VerifySignedData(win[8..length].ToArray(), file, layout, trustedRoot);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or InvalidDataException or ArgumentException or IOException
            or InvalidOperationException or FormatException)
        {
            return new Result(State.Invalid, Problem: ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static Result VerifySignedData(byte[] pkcs7, byte[] file, PeLayout layout, X509Certificate2 trustedRoot)
    {
        var outer = new AsnReader(pkcs7, AsnEncodingRules.BER);
        AsnReader contentInfo = outer.ReadSequence();
        if (contentInfo.ReadObjectIdentifier() != OidSignedData)
        {
            return new Result(State.Invalid, Problem: "not SignedData");
        }

        AsnReader signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        _ = signedData.ReadInteger();
        _ = signedData.ReadSetOf();
        AsnReader encap = signedData.ReadSequence();
        if (encap.ReadObjectIdentifier() != OidSpcIndirectData)
        {
            return new Result(State.Invalid, Problem: "not SpcIndirectDataContent");
        }

        byte[] content = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadEncodedValue().ToArray();
        var certificates = new X509Certificate2Collection();
        if (signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            AsnReader certs = signedData.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
            while (certs.HasData)
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(certs.ReadEncodedValue().Span));
            }
        }

        if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
        {
            _ = signedData.ReadEncodedValue();
        }

        AsnReader signers = signedData.ReadSetOf();
        AsnReader signer = signers.ReadSequence();
        if (signers.HasData)
        {
            return new Result(State.Invalid, Problem: "more than one signer");
        }

        _ = signer.ReadInteger();
        AsnReader sid = signer.ReadSequence();
        byte[] issuer = sid.ReadEncodedValue().ToArray();
        byte[] serial = sid.ReadIntegerBytes().ToArray();
        string digestOid = signer.ReadSequence().ReadObjectIdentifier();
        HashAlgorithmName digestAlgorithm = HashOf(digestOid) ?? throw new CryptographicException("unsupported digest " + digestOid);
        if (!signer.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            return new Result(State.Invalid, Problem: "no signed attributes");
        }

        byte[] signedAttributes = signer.ReadEncodedValue().ToArray();
        string signatureOid = signer.ReadSequence().ReadObjectIdentifier();
        byte[] signature = signer.ReadOctetString();

        X509Certificate2? signerCertificate = certificates.FirstOrDefault(c => c.IssuerName.RawData.AsSpan().SequenceEqual(issuer)
            && c.SerialNumberBytes.Span.TrimStart((byte)0).SequenceEqual(serial.AsSpan().TrimStart((byte)0)));
        if (signerCertificate is null)
        {
            return new Result(State.Invalid, Problem: "signer certificate not found");
        }

        // 署名付き属性: contentType と messageDigest を確かめる。
        var attributes = new AsnReader(signedAttributes, AsnEncodingRules.DER).ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
        byte[]? messageDigest = null;
        string? contentType = null;
        while (attributes.HasData)
        {
            AsnReader attribute = attributes.ReadSequence();
            string oid = attribute.ReadObjectIdentifier();
            AsnReader values = attribute.ReadSetOf();
            if (oid == OidMessageDigest)
            {
                messageDigest = values.ReadOctetString();
            }
            else if (oid == OidContentType)
            {
                contentType = values.ReadObjectIdentifier();
            }
        }

        if (contentType != OidSpcIndirectData || messageDigest is null
            || !CryptographicOperations.FixedTimeEquals(messageDigest, Hash(digestAlgorithm, ContentsOf(content))))
        {
            return new Result(State.Invalid, Problem: "content digest mismatch");
        }

        byte[] toVerify = (byte[])signedAttributes.Clone();
        toVerify[0] = 0x31;
        bool ok = signatureOid switch
        {
            OidRsa or OidSha256Rsa => signerCertificate.GetRSAPublicKey()?.VerifyData(toVerify, signature, digestAlgorithm, RSASignaturePadding.Pkcs1) == true,
            OidEcdsaSha256 => signerCertificate.GetECDsaPublicKey()?.VerifyData(toVerify, signature, digestAlgorithm, DSASignatureFormat.Rfc3279DerSequence) == true,
            _ => false,
        };
        if (!ok)
        {
            return new Result(State.Invalid, Problem: "bad signature");
        }

        // SpcIndirectDataContent の PE のハッシュとファイルを比べる (改ざんの検出)。
        var spc = new AsnReader(content, AsnEncodingRules.DER).ReadSequence();
        _ = spc.ReadSequence();
        AsnReader digestInfo = spc.ReadSequence();
        HashAlgorithmName peAlgorithm = HashOf(digestInfo.ReadSequence().ReadObjectIdentifier()) ?? throw new CryptographicException("unsupported PE digest");
        byte[] expected = digestInfo.ReadOctetString();
        if (!CryptographicOperations.FixedTimeEquals(expected, PeHash(file, layout, peAlgorithm)))
        {
            return new Result(State.Invalid, Problem: "file digest mismatch (modified)");
        }

        // 証明書のチェーン (テストのルートだけを信頼する)。期限切れ・用途の違いは無効。
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.ExtraStore.AddRange(certificates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(OidCodeSigning));
        chain.ChainPolicy.VerificationTime = DateTime.Now;
        if (!chain.Build(signerCertificate))
        {
            return new Result(State.Invalid, Problem: "chain: " + string.Join(", ", chain.ChainStatus.Select(s => s.Status)));
        }

        return new Result(State.Valid, signerCertificate.GetNameInfo(X509NameType.SimpleName, false), Thumbprint(signerCertificate));
    }

    private static HashAlgorithmName? HashOf(string oid) => oid switch
    {
        OidSha256 => HashAlgorithmName.SHA256,
        OidSha384 => HashAlgorithmName.SHA384,
        OidSha512 => HashAlgorithmName.SHA512,
        _ => null,
    };

    private static byte[] Hash(HashAlgorithmName algorithm, ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }
}
