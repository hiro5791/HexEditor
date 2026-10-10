namespace HexEditor.TestData;

/// <summary>比較 (ANA-01〜ANA-08) のテストデータ (cases/06-analysis.md の末尾の表)。</summary>
public static partial class TestDataCatalog
{
    /// <summary>TD-ANA-10G-A の乱数の種 (TD-ANA-10G-A は統計と共用。TestDataCatalog.Analysis.cs で定義する)。</summary>
    public const ulong Compare10GSeed = Ana10GSeed;

    private static IEnumerable<TestDataItem> CompareItems() =>
    [
        new("TD-ANA-DIFF3-A", 4 * KiB, "TD-SEQ-1M の先頭 4 KiB", path => WriteAll(path, Diff3A())),
        new("TD-ANA-DIFF3-B", 4 * KiB - 16, "TD-ANA-DIFF3-A の 0x100〜0x103 を FF に、0x800 の前に EE を 16 バイト挿入、0xC00〜0xC1F を削除",
            path => WriteAll(path, Diff3B())),
        new("TD-ANA-SHIFT200-A", 4 * KiB + 0x200, "AA を 0x200 バイトの後に TD-ANA-DIFF3-A", path => WriteAll(path, [.. Enumerable.Repeat((byte)0xAA, 0x200), .. Diff3A()])),
        new("TD-ANA-SHIFT200-B", 4 * KiB, "TD-ANA-DIFF3-A と同じ内容", path => WriteAll(path, Diff3A())),
        new("TD-ANA-SEQ-1M-MOD", MiB, "TD-SEQ-1M の 0x10000、0x40000、0x80000、0xC0000 から 16 バイトずつを FF と XOR",
            path => WriteGenerated(path, MiB, SequenceModified)),
        new("TD-ANA-LEN100", 100, "TD-BYTES-256 の先頭 100 バイト", path => WriteAll(path, [.. Enumerable.Range(0, 100).Select(i => (byte)i)])),
        new("TD-ANA-LEN120", 120, "TD-ANA-LEN100 の後に AA を 20 バイト",
            path => WriteAll(path, [.. Enumerable.Range(0, 100).Select(i => (byte)i), .. Enumerable.Repeat((byte)0xAA, 20)])),
        new("TD-ANA-ALT2M-A", 2_000_000, "すべて 00", path => WriteGenerated(path, 2_000_000, (_, s) => s.Clear())),
        new("TD-ANA-ALT2M-B", 2_000_000, "偶数のオフセットは 00、奇数のオフセットは FF", path => WriteGenerated(path, 2_000_000, Alternating)),
        new("TD-ANA-ALT20M-A", 20_000_000, "すべて 00", path => WriteGenerated(path, 20_000_000, (_, s) => s.Clear())),
        new("TD-ANA-ALT20M-B", 20_000_000, "偶数のオフセットは 00、奇数のオフセットは FF", path => WriteGenerated(path, 20_000_000, Alternating)),
        new("TD-ANA-10G-B", 10 * GiB, "TD-ANA-10G-A の 0x140000000 から 16 バイトを FF と XOR", path => WriteGenerated(path, 10 * GiB, (o, s) =>
        {
            Random(Compare10GSeed, o, s);
            XorFf(o, s, 0x140000000, 16);
        })),
    ];

    /// <summary>TD-ANA-DIFF3-A の内容。</summary>
    public static byte[] Diff3A()
    {
        byte[] data = new byte[4 * KiB];
        Sequence(0, data);
        return data;
    }

    /// <summary>TD-ANA-DIFF3-B の内容。</summary>
    public static byte[] Diff3B()
    {
        byte[] a = Diff3A();
        a.AsSpan(0x100, 4).Fill(0xFF);
        return [.. a[..0x800], .. Enumerable.Repeat((byte)0xEE, 16), .. a[0x800..0xC00], .. a[0xC20..]];
    }

    /// <summary>TD-ANA-SEQ-1M-MOD の内容。</summary>
    public static void SequenceModified(long offset, Span<byte> destination)
    {
        Sequence(offset, destination);
        foreach (long at in (long[])[0x10000, 0x40000, 0x80000, 0xC0000])
        {
            XorFf(offset, destination, at, 16);
        }
    }

    /// <summary>偶数のオフセットは 00、奇数のオフセットは FF。</summary>
    public static void Alternating(long offset, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = ((offset + i) & 1) == 1 ? (byte)0xFF : (byte)0;
        }
    }

    private static void XorFf(long offset, Span<byte> destination, long at, int length)
    {
        for (long p = Math.Max(at, offset); p < Math.Min(at + length, offset + destination.Length); p++)
        {
            destination[(int)(p - offset)] ^= 0xFF;
        }
    }
}
