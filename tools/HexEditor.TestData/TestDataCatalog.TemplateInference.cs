using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData;

// cases/07-templates.md の表のテンプレートの推定 (TPL-38〜TPL-40) のテストデータ。乱数は ID の文字列の SHA-256 の先頭 8 バイトを種とし、
// レコードごと・フィールドごとに独立に計算する (TD-TPL-INFER-1M でも任意の位置を作れるようにする)。
public static partial class TestDataCatalog
{
    private static IEnumerable<TestDataItem> TemplateInferenceItems() =>
    [
        new("TD-TPL-INFER-REC48", 0, "48 バイトのレコード 1,000 個 (LE) とデータの部分", path => WriteAll(path, InferRec48.Small(false).ToArray()))
        {
            ComputeLength = () => InferRec48.Small(false).Length,
        },
        new("TD-TPL-INFER-REC48-BE", 0, "TD-TPL-INFER-REC48 の数値のフィールドをビッグエンディアンにしたもの", path => WriteAll(path, InferRec48.Small(true).ToArray()))
        {
            ComputeLength = () => InferRec48.Small(true).Length,
        },
        new("TD-TPL-INFER-TIMES", 64_000, "32 バイトのレコード 2,000 個 (FILETIME・UNIX 時刻・DOS の日時・連番・UTF-16)", path => WriteAll(path, InferTimes())),
        new("TD-TPL-INFER-NUMS", 98_304, "24 バイトのレコード 4,096 個 (f64・小さな整数・乱数・列挙・埋め草)", path => WriteAll(path, InferNums())),
        new("TD-TPL-INFER-RANDOM", 64 * KiB, "固定の種の乱数", path => WriteAll(path, InferRandom())),
        new("TD-TPL-INFER-BADPTR", 16_000, "16 バイトのレコード 1,000 個 (1 レコードだけドキュメントの長さを超える値)", path => WriteAll(path, InferBadPointer())),
        new("TD-TPL-INFER-1M", 0, "TD-TPL-INFER-REC48 と同じ作り方のレコード 1,000,000 個 (約 184 MB)",
            path => WriteGenerated(path, InferRec48.Large.Length, InferRec48.Large.Fill))
        {
            ComputeLength = () => InferRec48.Large.Length,
        },
    ];

    /// <summary>SplitMix64 (レコード r・フィールド f ごとの乱数)。</summary>
    public static ulong InferRandom(ulong seed, long r, int field)
    {
        ulong z = seed + ((ulong)r * 0x9E3779B97F4A7C15UL) + ((ulong)field * 0xD1B54A32D192ED03UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>名前のフィールドの単語表 (3〜16 文字)。先頭はレコード 0 に使う 16 文字の語。</summary>
    public static readonly string[] InferWords =
    [
        "internationalize", "cat", "dog", "tree", "river", "stone", "garden", "window", "mountain", "keyboard", "elephant", "notebook",
        "sunflower", "chocolate", "basketball", "photograph", "lighthouse", "strawberry", "watermelon", "butterfly", "telescope",
        "microphone", "dictionary", "philosophy", "transparent", "temperature", "neighborhood", "encyclopedia", "refrigerator",
        "characterization", "responsibilities", "uncharacteristic", "sky", "sea", "ant", "owl", "map", "pen", "cup", "box", "apple",
        "bread", "cloud", "dream", "eagle", "flame", "grape", "house", "island", "jacket", "kitten", "lemon", "marble", "needle",
        "orange", "pencil", "quartz", "rabbit", "silver", "tomato", "violet", "wizard", "yellow", "zebra", "binary", "editor",
    ];

    /// <summary>TD-TPL-INFER-REC48 / -BE / -1M の作り方 (レコードの部分と、その後ろのデータの部分)。</summary>
    public sealed class InferRec48
    {
        public const int RecordLength = 48;

        private static readonly Lazy<byte[]> SmallLe = new(() => Build(1000, false, "TD-TPL-INFER-REC48"));
        private static readonly Lazy<byte[]> SmallBe = new(() => Build(1000, true, "TD-TPL-INFER-REC48"));
        private static readonly Lazy<InferRec48> LargeLayout = new(() => new InferRec48(1_000_000, false, "TD-TPL-INFER-1M"));

        private readonly long[] _offsets;
        private readonly int[] _lengths;

        public InferRec48(long count, bool bigEndian, string id)
        {
            Count = count;
            BigEndian = bigEndian;
            Seed = SeedOf(id);
            _lengths = new int[count];
            _offsets = new long[count];
            long offset = RecordLength * count;
            for (long r = 0; r < count; r++)
            {
                _lengths[r] = 16 * (1 + (int)(InferRandom(Seed, r, 2) % 16));
                _offsets[r] = offset;
                offset += _lengths[r];
            }

            Length = offset;
        }

        public long Count { get; }

        public bool BigEndian { get; }

        public ulong Seed { get; }

        /// <summary>ファイルの長さ (レコードの部分 + データの部分)。</summary>
        public long Length { get; }

        public static ReadOnlyMemory<byte> Small(bool bigEndian) => bigEndian ? SmallBe.Value : SmallLe.Value;

        /// <summary>TD-TPL-INFER-1M の作り方 (100 万個。値は位置から計算する)。</summary>
        public static InferRec48 Large => LargeLayout.Value;

        private static byte[] Build(int count, bool big, string id)
        {
            var layout = new InferRec48(count, big, id);
            byte[] data = new byte[layout.Length];
            layout.Fill(0, data);
            return data;
        }

        public long OffsetOf(long r) => _offsets[r];

        public int LengthOf(long r) => _lengths[r];

        /// <summary>レコード r の UNIX 時刻 (0x08)。</summary>
        public uint TimeOf(long r) => (uint)(1_577_836_800 + (60 * r) + (long)(InferRandom(Seed, r, 1) % 60));

        public float ValueOf(long r) => (float)((InferRandom(Seed, r, 3) >> 40) / (double)(1UL << 24) * 1000);

        public ushort KindOf(long r) => (ushort)(new[] { 1, 2, 3, 5 }[InferRandom(Seed, r, 4) % 4]);

        public ushort FlagsOf(long r)
        {
            ulong x = InferRandom(Seed, r, 5);
            return (ushort)(((x & 1) != 0 ? 0x0001 : 0) | ((x & 2) != 0 ? 0x0004 : 0) | ((x & 4) != 0 ? 0x0080 : 0));
        }

        public string NameOf(long r) => r == 0 ? InferWords[0] : InferWords[1 + (int)(InferRandom(Seed, r, 6) % (ulong)(InferWords.Length - 1))];

        /// <summary>レコード r の 48 バイト。</summary>
        public void WriteRecord(long r, Span<byte> rec)
        {
            rec.Clear();
            "RCD1"u8.CopyTo(rec);
            WriteU32(rec[0x04..], (uint)r);
            WriteU32(rec[0x08..], TimeOf(r));
            WriteU32(rec[0x0C..], (uint)_offsets[r]);
            WriteU32(rec[0x10..], (uint)_lengths[r]);
            WriteU32(rec[0x14..], BitConverter.SingleToUInt32Bits(ValueOf(r)));
            WriteU16(rec[0x18..], KindOf(r));
            WriteU16(rec[0x1A..], FlagsOf(r));
            Encoding.ASCII.GetBytes(NameOf(r)).CopyTo(rec[0x1C..]);
        }

        private void WriteU32(Span<byte> s, uint v)
        {
            if (BigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(s, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s, v);
            }
        }

        private void WriteU16(Span<byte> s, ushort v)
        {
            if (BigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(s, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s, v);
            }
        }

        /// <summary>位置 <paramref name="offset"/> からの内容 (任意の範囲)。</summary>
        public void Fill(long offset, Span<byte> destination)
        {
            long recordsEnd = RecordLength * Count;
            Span<byte> rec = stackalloc byte[RecordLength];
            int done = 0;
            while (done < destination.Length)
            {
                long at = offset + done;
                if (at < recordsEnd)
                {
                    long r = at / RecordLength;
                    int inRecord = (int)(at % RecordLength);
                    WriteRecord(r, rec);
                    int n = Math.Min(RecordLength - inRecord, destination.Length - done);
                    rec.Slice(inRecord, n).CopyTo(destination[done..]);
                    done += n;
                }
                else
                {
                    int n = destination.Length - done;
                    Random(Seed ^ 0xDA7A, at - recordsEnd, destination.Slice(done, n));
                    done += n;
                }
            }
        }
    }

    /// <summary>TD-TPL-INFER-TIMES: 32 バイトのレコード 2,000 個。</summary>
    public static byte[] InferTimes()
    {
        ulong seed = SeedOf("TD-TPL-INFER-TIMES");
        byte[] data = new byte[64_000];
        long fileTimeBase = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var dosBase = new DateTime(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int r = 0; r < 2000; r++)
        {
            Span<byte> rec = data.AsSpan(r * 32, 32);
            BinaryPrimitives.WriteInt64LittleEndian(rec, fileTimeBase + (r * 10_000_000L) + (long)(InferRandom(seed, r, 0) % 10_000_000));
            BinaryPrimitives.WriteUInt64LittleEndian(rec[0x08..], 1_622_505_600UL + (3UL * (ulong)r) + (InferRandom(seed, r, 1) % 3));
            DateTime t = dosBase.AddSeconds(2 * r);
            uint date = (uint)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
            uint time = (uint)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[0x10..], (date << 16) | time);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[0x14..], (uint)(100 + (4 * r)));
            int chars = r == 0 ? 4 : 1 + (int)(InferRandom(seed, r, 2) % 4);
            for (int c = 0; c < chars; c++)
            {
                // ひらがな (U+3041〜U+3093)
                ushort ch = (ushort)(0x3041 + (int)(InferRandom(seed, r, 10 + c) % 83));
                BinaryPrimitives.WriteUInt16LittleEndian(rec[(0x18 + (c * 2))..], ch);
            }
        }

        return data;
    }

    /// <summary>TD-TPL-INFER-NUMS: 24 バイトのレコード 4,096 個。</summary>
    public static byte[] InferNums()
    {
        ulong seed = SeedOf("TD-TPL-INFER-NUMS");
        byte[] data = new byte[98_304];
        byte[] kinds = [7, 9, 12];
        for (int r = 0; r < 4096; r++)
        {
            Span<byte> rec = data.AsSpan(r * 24, 24);

            // 正規分布の乱数 (Box–Muller)
            double u1 = ((InferRandom(seed, r, 0) >> 11) + 1.0) / (1UL << 53);
            double u2 = (InferRandom(seed, r, 1) >> 11) / (double)(1UL << 53);
            double normal = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            BinaryPrimitives.WriteDoubleLittleEndian(rec, 20 + (5 * normal));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[0x08..], (uint)(InferRandom(seed, r, 2) % 1001));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[0x0C..], (uint)InferRandom(seed, r, 3));
            rec[0x10] = kinds[InferRandom(seed, r, 4) % 3];
        }

        return data;
    }

    /// <summary>TD-TPL-INFER-RANDOM: 64 KiB の乱数。</summary>
    public static byte[] InferRandom()
    {
        byte[] data = new byte[64 * 1024];
        Random(SeedOf("TD-TPL-INFER-RANDOM"), 0, data);
        return data;
    }

    /// <summary>TD-TPL-INFER-BADPTR: 16 バイトのレコード 1,000 個。0x04 は 16 の倍数を小さい順に並べた値で、レコード 500 だけ 0x10000。</summary>
    public static byte[] InferBadPointer()
    {
        ulong seed = SeedOf("TD-TPL-INFER-BADPTR");
        byte[] data = new byte[16_000];
        uint[] pointers = new uint[1000];
        for (int i = 0; i < 1000; i++)
        {
            pointers[i] = (uint)(16 * (1 + (int)(InferRandom(seed, i, 9) % 999)));
        }

        Array.Sort(pointers);
        for (int r = 0; r < 1000; r++)
        {
            Span<byte> rec = data.AsSpan(r * 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)InferRandom(seed, r, 0));
            BinaryPrimitives.WriteUInt32LittleEndian(rec[0x04..], r == 500 ? 0x10000u : pointers[r]);
            BinaryPrimitives.WriteUInt64LittleEndian(rec[0x08..], InferRandom(seed, r, 1));
        }

        return data;
    }
}
