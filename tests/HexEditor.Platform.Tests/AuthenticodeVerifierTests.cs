using HexEditor.Platform.Security;

namespace HexEditor.Platform.Tests;

/// <summary>Authenticode の確認 (AUTO-03 の仕様 3。C# スクリプト コンポーネントの Roslyn のアセンブリ)。読むだけで、通信しない。</summary>
public sealed class AuthenticodeVerifierTests
{
    [Fact]
    public void Runtime_assembly_is_microsoft_signed()
    {
        string coreLib = typeof(object).Assembly.Location;
        Assert.True(AuthenticodeVerifier.IsMicrosoftSigned(coreLib, out string? error), error);
    }

    [Fact]
    public void Tampered_or_unsigned_files_are_rejected()
    {
        string temp = Path.Combine(Path.GetTempPath(), "HexEditor", "authenticode-" + Guid.NewGuid().ToString("N") + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            byte[] bytes = File.ReadAllBytes(typeof(object).Assembly.Location);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(temp, bytes);
            Assert.False(AuthenticodeVerifier.IsMicrosoftSigned(temp, out _));
            File.WriteAllBytes(temp, [1, 2, 3]);
            Assert.False(AuthenticodeVerifier.IsMicrosoftSigned(temp, out _));
            Assert.False(AuthenticodeVerifier.IsMicrosoftSigned(temp + ".missing", out _));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
