using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Support;

/// <summary>ドキュメントの内容を読むためのテスト用の補助。</summary>
public static class DocumentAssert
{
    /// <summary>テストケース ID の Trait 名 (テスト方針 6.2)。</summary>
    public const string TC = "TC";

    public static byte[] ReadAll(DocumentSnapshot snapshot) => Read(snapshot, 0, (int)snapshot.Length);

    public static byte[] Read(DocumentSnapshot snapshot, long offset, int length)
    {
        byte[] buffer = new byte[length];
        ReadResult result = snapshot.Read(offset, buffer);
        Assert.True(result.IsComplete, "読めない範囲があります。");
        return buffer[..result.BytesReturned];
    }

    public static byte[] Sha256(DocumentSnapshot snapshot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[4 * 1024 * 1024];
        for (long offset = 0; offset < snapshot.Length; offset += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, snapshot.Length - offset);
            snapshot.Read(offset, buffer.AsSpan(0, n));
            hash.AppendData(buffer, 0, n);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>一時フォルダを使うドキュメントの設定。</summary>
    public static DocumentOptions Options(long addBufferMemoryLimit = 256L * 1024 * 1024, long cacheCapacity = 256L * 1024 * 1024) => new()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "HexEditorTests", "recovery"),
        AddBufferMemoryLimit = addBufferMemoryLimit,
        CacheCapacity = cacheCapacity,
    };

    /// <summary>表示用の読み込みを、すべてのバイトが読み込み中でなくなるまで繰り返す。</summary>
    public static (byte[] Bytes, ByteState[] States) ReadForDisplayWhenLoaded(DocumentSnapshot snapshot, long offset, int length, int timeoutMs = 10_000)
    {
        byte[] bytes = new byte[length];
        var states = new ByteState[length];
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            int n = snapshot.ReadForDisplay(offset, bytes, states);
            if (!states.AsSpan(0, n).Contains(ByteState.Loading))
            {
                return (bytes[..n], states[..n]);
            }

            Assert.True(DateTime.UtcNow < deadline, "読み込みが終わりません。");
            Thread.Sleep(5);
        }
    }
}
