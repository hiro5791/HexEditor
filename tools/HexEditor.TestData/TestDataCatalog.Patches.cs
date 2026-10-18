namespace HexEditor.TestData;

/// <summary>
/// パッチ (EDIT-42〜EDIT-44) のテストデータ (cases/03-editing.md の表の TD-EDIT-PATCH-*)。TD-EDIT-PATCH-IPS・TD-EDIT-PATCH-BPS は、
/// Flips の出力と同じ形式を、形式の仕様どおりに組み立てる参照実装で作る (Flips を入れずに作れるようにするため。Flips の出力そのものでの
/// 確認は、CI の Flips の結合テスト TC-EDIT-42-01・TC-EDIT-42-02 で行う)。
/// </summary>
public static partial class TestDataCatalog
{
    public const ulong PatchSourceSeed = 4201;

    public const ulong PatchOtherSeed = 4202;

    /// <summary>TD-EDIT-PATCH-MOD3 で書き換える 3 か所。</summary>
    public static readonly long[] PatchMod3Offsets = [0x100, 0x8000, 0xF0000];

    public const long PatchInsertOffset = 0x80000;

    private static IEnumerable<TestDataItem> PatchItems() =>
    [
        new("TD-EDIT-PATCH-SRC", MiB, "種 4201 の乱数", path => WriteAll(path, PatchSource())),
        new("TD-EDIT-PATCH-MOD3", MiB, "TD-EDIT-PATCH-SRC の 0x100・0x8000・0xF0000 を元の値と FF の XOR にしたもの", path => WriteAll(path, PatchMod3())),
        new("TD-EDIT-PATCH-INS100", MiB + 100, "TD-EDIT-PATCH-SRC の 0x80000 に AA を 100 バイト挿入したもの", path => WriteAll(path, PatchIns100())),
        new("TD-EDIT-PATCH-OTHER", MiB, "種 4202 の乱数", path =>
        {
            byte[] data = new byte[MiB];
            Random(PatchOtherSeed, 0, data);
            WriteAll(path, data);
        }),
        new("TD-EDIT-PATCH-IPS", 26, "TD-EDIT-PATCH-SRC から TD-EDIT-PATCH-MOD3 への IPS (3 レコード)", path => WriteAll(path, PatchIps())),
        new("TD-EDIT-PATCH-BPS", 0, "TD-EDIT-PATCH-SRC から TD-EDIT-PATCH-INS100 への BPS (SourceRead・TargetRead・SourceCopy)", path => WriteAll(path, PatchBps()))
        {
            ComputeLength = () => PatchBps().Length,
        },
    ];

    public static byte[] PatchSource()
    {
        byte[] data = new byte[MiB];
        Random(PatchSourceSeed, 0, data);
        return data;
    }

    public static byte[] PatchMod3()
    {
        byte[] data = PatchSource();
        foreach (long at in PatchMod3Offsets)
        {
            data[at] ^= 0xFF;
        }

        return data;
    }

    public static byte[] PatchIns100()
    {
        byte[] src = PatchSource();
        return [.. src.AsSpan(0, (int)PatchInsertOffset), .. Enumerable.Repeat((byte)0xAA, 100), .. src.AsSpan((int)PatchInsertOffset)];
    }

    /// <summary>IPS: ヘッダ PATCH、3 バイトのオフセット・2 バイトの長さ 1・値のレコード 3 つ、終端 EOF。</summary>
    private static byte[] PatchIps()
    {
        byte[] mod = PatchMod3();
        var ms = new MemoryStream();
        ms.Write("PATCH"u8);
        foreach (long at in PatchMod3Offsets)
        {
            ms.Write([(byte)(at >> 16), (byte)(at >> 8), (byte)at, 0, 1, mod[at]]);
        }

        ms.Write("EOF"u8);
        return ms.ToArray();
    }

    /// <summary>BPS (beat): SourceRead 0x80000、TargetRead 100 バイト、SourceCopy 0x80000 (相対位置 +0x80000)、CRC-32 を 3 つ。</summary>
    private static byte[] PatchBps()
    {
        byte[] src = PatchSource(), target = PatchIns100();
        var ms = new MemoryStream();
        ms.Write("BPS1"u8);
        Varint(ms, (ulong)src.Length);
        Varint(ms, (ulong)target.Length);
        Varint(ms, 0);
        Varint(ms, (ulong)(PatchInsertOffset - 1) << 2 | 0);
        Varint(ms, 99UL << 2 | 1);
        ms.Write(Enumerable.Repeat((byte)0xAA, 100).ToArray());
        Varint(ms, (ulong)(src.Length - PatchInsertOffset - 1) << 2 | 2);
        Varint(ms, (ulong)PatchInsertOffset << 1);
        ms.Write(BitConverter.GetBytes(PatchCrc(src)));
        ms.Write(BitConverter.GetBytes(PatchCrc(target)));
        ms.Write(BitConverter.GetBytes(PatchCrc(ms.ToArray())));
        return ms.ToArray();
    }

    private static void Varint(Stream s, ulong v)
    {
        while (true)
        {
            byte x = (byte)(v & 0x7F);
            v >>= 7;
            if (v == 0)
            {
                s.WriteByte((byte)(0x80 | x));
                return;
            }

            s.WriteByte(x);
            v--;
        }
    }

    private static uint PatchCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
