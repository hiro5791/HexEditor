using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.TestData;

/// <summary>
/// テストデータ 1 件の定義 (docs/test/test-data.md と各テストケースのファイルの末尾の表)。<paramref name="PathIn"/> は、
/// ファイル名・置き場所が決まっているもの (長いパス・絵文字の名前など) の出力先のパスを返す。null なら <c>&lt;ID&gt;.bin</c>。
/// </summary>
public sealed record TestDataItem(string Id, long Length, string Description, Action<string> Generate, Func<string, string>? PathIn = null);

/// <summary>
/// テストデータを生成する (テスト方針 7.1)。同じ ID からは常に同じ内容を作る。生成したファイルはキャッシュし、
/// 2 回目以降はそのまま使う。
/// </summary>
public static class TestDataCatalog
{
    public const long KiB = 1024;
    public const long MiB = 1024 * KiB;
    public const long GiB = 1024 * MiB;
    public const long TiB = 1024 * GiB;

    /// <summary>目印の長さ: '@' と 16 桁の Hex。</summary>
    public const int MarkerLength = 17;

    private static readonly Dictionary<string, TestDataItem> Items = new List<TestDataItem>
    {
        new("TD-EMPTY", 0, "空のファイル", path => WriteAll(path, [])),
        new("TD-BYTES-256", 256, "00〜FF を 1 回ずつ", path => WriteAll(path, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray())),
        new("TD-SEQ-1M", MiB, "オフセット n の値は n mod 256", path => WriteGenerated(path, MiB, (o, s) => Sequence(o, s))),
        new("TD-ZERO-1M", MiB, "すべて 00", path => WriteGenerated(path, MiB, (_, s) => s.Clear())),
        new("TD-FF-1M", MiB, "すべて FF", path => WriteGenerated(path, MiB, (_, s) => s.Fill(0xFF))),
        new("TD-RANDOM-16M", 16 * MiB, "固定の種の乱数", path => WriteGenerated(path, 16 * MiB, (o, s) => Random(RandomSeed, o, s))),
        new("TD-MARKERS-1G", GiB, "先頭・末尾・2^20 ごとの目印 (スパース)", path => WriteMarkers(path, GiB, MarkersEvery(GiB, MiB))),

        // 04-search.md・09-ui-and-settings.md で定義したもの。
        new("TD-FIND-SPARSE-2G", 2 * GiB, "TD-MARKERS-1G と同じ規則の目印 2,049 個 (スパース)", path => WriteMarkers(path, 2 * GiB, MarkersEvery(2 * GiB, MiB))),
        new("TD-UI-SPARSE-1536M", 1536 * MiB, "先頭と末尾の目印 (スパース)", path => WriteMarkers(path, 1536 * MiB, [0, 1536 * MiB - MarkerLength])),
        new("TD-SPARSE-100G", 100 * GiB, "先頭・末尾・2^31・2^32 の前後・1 GiB ごとの目印 (スパース)",
            path => WriteMarkers(path, 100 * GiB, MarkersEvery(100 * GiB, GiB).Concat(Around(1L << 31)).Concat(Around(1L << 32)))),
        new("TD-SPARSE-2T", 2 * TiB, "先頭・末尾・2^31・2^32・2^40 の前後の目印 (スパース)",
            path => WriteMarkers(path, 2 * TiB, new[] { 0L, 2 * TiB - MarkerLength }.Concat(Around(1L << 31)).Concat(Around(1L << 32)).Concat(Around(1L << 40)))),

        // ---- cases/01-engine-and-sources.md の表 ----
        new("TD-ENG-SPARSE-10G", 10 * GiB, "先頭・末尾・1 GiB ごと・2^31 と 2^32 の前後 (± 32) の目印 (スパース)",
            path => WriteMarkers(path, 10 * GiB, MarkersEvery(10 * GiB, GiB).Concat([(1L << 31) - 32, (1L << 31) + 32, (1L << 32) - 32, (1L << 32) + 32]))),
        new("TD-ENG-SPARSE-100G-1M", 100 * GiB, "[50 GiB, 50 GiB + 1 MiB) に種 0x100 の乱数。それ以外は未割り当て (スパース)",
            path => WriteSparse(path, 100 * GiB, SparseRandomOffset, MiB, (o, s) => Random(SparseRandomSeed, o - SparseRandomOffset, s))),
        new("TD-ENG-PATH-300", KiB, "TD-SEQ-1M の先頭 1 KiB。絶対パスがちょうど 300 文字になる位置に置く",
            path => WriteGenerated(path, KiB, (o, s) => Sequence(o, s)), LongPathIn),
        new("TD-ENG-EMOJI-NAME", KiB, "TD-BYTES-256 を 4 回繰り返したもの。ファイル名は テスト_😀_📦.bin",
            path => WriteGenerated(path, KiB, (o, s) => Sequence(o, s)), dir => Path.Combine(dir, "テスト_😀_📦.bin")),
        new("TD-ENG-ADS", KiB, "ads.bin: TD-SEQ-1M の先頭 1 KiB と、代替データストリーム secret・Zone.Identifier", WriteAds,
            dir => Path.Combine(dir, "TD-ENG-ADS", "ads.bin")),

        // ---- cases/04-search.md の表 ----
        new("TD-FIND-RANDOM-10G", 10 * GiB, "種 4401 の乱数 (スパースにしない)。最後の 8 バイトが HEXEND!!", WriteFindRandom),

        // ---- テキスト (test-data.md の共通の表) ----
        new("TD-TEXT-ASCII", 4 * KiB, "英文の ASCII テキスト (改行は CRLF)", path => WriteAll(path, Text(AsciiLines, Encoding.ASCII, 4 * KiB, bom: false))),
        new("TD-TEXT-UTF8", 4 * KiB, "23 言語の短い文の UTF-8 (BOM なし)。絵文字・結合文字を含む",
            path => WriteAll(path, Text(Utf8Lines, new UTF8Encoding(false), 4 * KiB, bom: false))),
        new("TD-TEXT-UTF16LE", 8 * KiB, "TD-TEXT-UTF8 と同じ内容の UTF-16 LE (BOM 付き)",
            path => WriteAll(path, Text(Utf8Lines, new UnicodeEncoding(false, true), 8 * KiB, bom: true))),
        new("TD-TEXT-SJIS", 4 * KiB, "日本語の文の Shift_JIS (半角カナ・機種依存文字を含む)", path => WriteAll(path, Text(SjisLines, ShiftJis(), 4 * KiB, bom: false))),

        // ---- cases/02-view-and-navigation.md の表 ----
        new("TD-VIEW-PATTERNS", 512, "表示形式・文字コードの確認用の決まったバイト列", path => WriteAll(path, ViewPatterns())),

        // ---- cases/09-ui-and-settings.md の表 ----
        new("TD-UI-SECRET", 4 * KiB, "secret-content.bin: HEXEDITOR-SECRET-7F3A の繰り返し",
            path => WriteGenerated(path, 4 * KiB, (o, s) => Repeat(SecretMarker, o, s)), dir => Path.Combine(dir, "TD-UI-SECRET", "secret-content.bin")),
    }.ToDictionary(i => i.Id);

    /// <summary>TD-ENG-SPARSE-100G-1M の乱数の位置と種。</summary>
    public const long SparseRandomOffset = 50 * GiB;

    public const ulong SparseRandomSeed = 0x100;

    /// <summary>TD-ENG-ADS の代替データストリームの名前と内容。</summary>
    public static readonly IReadOnlyDictionary<string, byte[]> AdsStreams = new Dictionary<string, byte[]>
    {
        ["secret"] = Encoding.ASCII.GetBytes("TOP-SECRET-DATA!"),
        ["Zone.Identifier"] = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.com/ads.bin\r\n"),
    };

    /// <summary>TD-UI-SECRET の目印の文字列。</summary>
    public const string SecretMarker = "HEXEDITOR-SECRET-7F3A";

    /// <summary>TD-FIND-RANDOM-10G の乱数の種と、末尾に置く並び (HEXEND!!)。</summary>
    public const ulong FindRandomSeed = 4401;

    public static readonly byte[] FindRandomTail = [0x48, 0x45, 0x58, 0x45, 0x4E, 0x44, 0x21, 0x21];

    /// <summary>TD-RANDOM-16M の乱数の種。</summary>
    public const ulong RandomSeed = 0x5EED_0000_0016_0001UL;

    public static IReadOnlyCollection<TestDataItem> All => Items.Values;

    /// <summary>テストデータの置き場所。環境変数 HEXEDITOR_TESTDATA で変えられる。</summary>
    public static string CacheDirectory =>
        Environment.GetEnvironmentVariable("HEXEDITOR_TESTDATA") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "HexEditorTestData");

    /// <summary>テストデータのパスを返す。なければ生成する。</summary>
    public static string Get(string id) => Generate(id, CacheDirectory);

    /// <summary>テストデータを <paramref name="directory"/> に生成し、パスを返す。同じ長さのファイルがあれば作り直さない。</summary>
    public static string Generate(string id, string directory)
    {
        if (!Items.TryGetValue(id, out TestDataItem? item))
        {
            throw new ArgumentException($"未定義のテストデータです: {id}", nameof(id));
        }

        Directory.CreateDirectory(directory);
        string path = item.PathIn?.Invoke(directory) ?? Path.Combine(directory, id + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (Items)
        {
            if (File.Exists(path) && new FileInfo(path).Length == item.Length)
            {
                return path;
            }

            string temp = path + ".tmp";
            item.Generate(temp);
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    /// <summary>テストデータの内容を計算で求める (ファイルを読まずに期待値を作るため)。</summary>
    public static void Expected(string id, long offset, Span<byte> destination)
    {
        switch (id)
        {
            case "TD-SEQ-1M":
                Sequence(offset, destination);
                break;
            case "TD-RANDOM-16M":
                Random(RandomSeed, offset, destination);
                break;
            case "TD-ENG-SPARSE-100G-1M":
                destination.Clear();
                long from = Math.Max(offset, SparseRandomOffset);
                long to = Math.Min(offset + destination.Length, SparseRandomOffset + MiB);
                if (from < to)
                {
                    Random(SparseRandomSeed, from - SparseRandomOffset, destination.Slice((int)(from - offset), (int)(to - from)));
                }

                break;
            default:
                throw new NotSupportedException(id);
        }
    }

    /// <summary>オフセット <paramref name="position"/> に置く目印のバイト列 (`@` と 16 桁の大文字の Hex)。</summary>
    public static byte[] Marker(long position) => Encoding.ASCII.GetBytes("@" + position.ToString("X16"));

    /// <summary>オフセット n の値を n mod 256 にする。</summary>
    public static void Sequence(long offset, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(offset + i);
        }
    }

    /// <summary>SplitMix64 のカウンタ方式の乱数 (エンジンの生成ピースと同じ方式)。</summary>
    public static void Random(ulong seed, long offset, Span<byte> destination)
    {
        // 8 バイトごとに 1 つの 64 bit の値を作り、下位のバイトから順に使う (同じ 8 バイトの中では作り直さない)。
        long word = -1;
        ulong z = 0;
        for (int i = 0; i < destination.Length; i++)
        {
            long p = offset + i;
            if (p >> 3 != word)
            {
                word = p >> 3;
                z = unchecked(seed + (ulong)word * 0x9E3779B97F4A7C15UL + 0x9E3779B97F4A7C15UL);
                z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
                z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
                z ^= z >> 31;
            }

            destination[i] = (byte)(z >> (int)((p & 7) * 8));
        }
    }

    private static IEnumerable<long> MarkersEvery(long length, long step)
    {
        for (long p = 0; p < length; p += step)
        {
            yield return p;
        }

        yield return length - MarkerLength;
    }

    /// <summary><paramref name="p"/> の直前と <paramref name="p"/> に置く目印の位置。</summary>
    private static IEnumerable<long> Around(long p) => [p - MarkerLength, p];

    private static void WriteAll(string path, byte[] data) => File.WriteAllBytes(path, data);

    // ---- テキストのテストデータ ----

    private static string[] AsciiLines =>
    [
        "The quick brown fox jumps over the lazy dog.",
        "Pack my box with five dozen liquor jugs.",
        "How vexingly quick daft zebras jump!",
        "Sphinx of black quartz, judge my vow.",
        "HexEditor test data: 0123456789 ABCDEF abcdef ~!@#$%^&*()_+-=[]{};':\",./<>?",
    ];

    /// <summary>23 言語の短い文 (絵文字・結合文字を含む)。</summary>
    private static string[] Utf8Lines =>
    [
        "English: Hello, world!",
        "日本語: こんにちは、世界。漢字とカタカナ",
        "简体中文: 你好，世界",
        "繁體中文: 你好，世界",
        "한국어: 안녕하세요 세계",
        "Deutsch: Grüße, schöne Welt",
        "Français: Bonjour le monde, ça va ?",
        "Español: ¡Hola, mundo! Ñandú",
        "Italiano: Ciao mondo, perché",
        "Português: Olá, mundo! Ação",
        "Русский: Привет, мир",
        "Українська: Привіт, світе",
        "Polski: Witaj świecie, zażółć",
        "Čeština: Ahoj světe, příliš",
        "Magyar: Helló világ, árvíztűrő",
        "Türkçe: Merhaba dünya, ışık",
        "Nederlands: Hallo wereld",
        "Svenska: Hej världen, åäö",
        "العربية: مرحبا بالعالم",
        "فارسی: سلام دنیا",
        "עברית: שלום עולם",
        "ไทย: สวัสดีชาวโลก",
        "Tiếng Việt: Xin chào thế giới",
        "Emoji: 😀🎉👍 ★ é ä (combining)",
    ];

    private static string[] SjisLines =>
    [
        "日本語の文章です。ひらがな、カタカナ、漢字を含みます。",
        "半角カナ: ｱｲｳｴｵ ｶﾞｷﾞｸﾞ ﾊﾟﾋﾟﾌﾟ",
        "機種依存文字: ①②③ ㈱ ㌔ Ⅰ Ⅱ Ⅲ",
        "記号: ＡＢＣ　１２３　〒　※　→　♪",
        "ASCII mixed: HexEditor 0123456789",
    ];

    /// <summary>行を CRLF で区切って並べ、<paramref name="size"/> バイトに収まらない行は入れずに空白で埋める。</summary>
    private static byte[] Text(string[] lines, Encoding encoding, long size, bool bom)
    {
        var result = new List<byte>((int)size);
        if (bom)
        {
            result.AddRange(encoding.GetPreamble());
        }

        byte[] space = encoding.GetBytes(" ");
        for (int i = 0; ; i++)
        {
            byte[] line = encoding.GetBytes(lines[i % lines.Length] + "\r\n");
            if (result.Count + line.Length > size)
            {
                break;
            }

            result.AddRange(line);
        }

        while (result.Count + space.Length <= size)
        {
            result.AddRange(space);
        }

        while (result.Count < size)
        {
            result.Add(0x20);
        }

        return [.. result];
    }

    private static Encoding ShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    /// <summary>TD-VIEW-PATTERNS (cases/02-view-and-navigation.md の表)。</summary>
    public static byte[] ViewPatterns()
    {
        byte[] data = new byte[512];
        void Put(int offset, params byte[] bytes) => bytes.CopyTo(data, offset);
        Put(0x000, 0x4F);
        Put(0x010, 0xFF, 0xFF, 0xFF, 0xFF);
        Put(0x020, 0x00, 0x00, 0xC0, 0x7F);
        Put(0x030, 0xA4, 0x70, 0x9D, 0x3F, 0x00, 0x00, 0xC0, 0x3F);
        Put(0x040, 0xE9, 0x82, 0xC1);
        Put(0x050, 0xE3, 0x81, 0x82);
        Put(0x060, 0xC3, 0x28);
        Put(0x070, 0x3D, 0xD8, 0x00, 0xDE);
        Put(0x080, 0x41, 0xCC, 0x81);
        Put(0x090, 0xC7, 0xE1, 0xDA, 0xD1, 0xC8, 0xED, 0xC9);
        Put(0x0BF, 0xE3, 0x81, 0x82);
        Put(0x0E1, 0x48, 0x00, 0x65, 0x00, 0x6C, 0x00, 0x6C, 0x00, 0x6F, 0x00);
        Put(0x100, 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04);
        Put(0x110, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08);
        Put(0x120, 0x34, 0x12);
        return data;
    }

    /// <summary><paramref name="text"/> を先頭から繰り返した内容。</summary>
    private static void Repeat(string text, long offset, Span<byte> destination)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = bytes[(offset + i) % bytes.Length];
        }
    }

    /// <summary>
    /// TD-ENG-PATH-300 の置き場所: 出力先の下に `d` と 49 個の `x` の名前のフォルダを重ね、絶対パスがちょうど 300 文字になるように
    /// ファイル名 (`f` + `x` の繰り返し + `.bin`) の長さで調整する。
    /// </summary>
    public static string LongPathIn(string directory)
    {
        const int Total = 300;
        const string ShortestName = "f.bin";
        string folder = "d" + new string('x', 49);
        string dir = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        while (dir.Length + 1 + folder.Length + 1 + ShortestName.Length <= Total)
        {
            dir = Path.Combine(dir, folder);
        }

        int nameLength = Total - dir.Length - 1;
        if (nameLength < ShortestName.Length)
        {
            throw new PathTooLongException($"出力先のパスが長すぎます: {directory}");
        }

        return Path.Combine(dir, "f" + new string('x', nameLength - ShortestName.Length) + ".bin");
    }

    /// <summary>TD-ENG-ADS: メインのストリームと代替データストリーム (NTFS)。名前の変更 (生成後の移動) でストリームも移る。</summary>
    private static void WriteAds(string path)
    {
        WriteGenerated(path, KiB, (o, s) => Sequence(o, s));
        foreach ((string name, byte[] content) in AdsStreams)
        {
            File.WriteAllBytes(path + ":" + name, content);
        }
    }

    /// <summary>
    /// TD-FIND-RANDOM-10G: 種 4401 の乱数。最後の 8 バイトを HEXEND!! にし、それ以外で検索のテストに使う 4 つの並びと一致する箇所は、
    /// 一致の 2 バイト目 (どの並びでも 45) を 00 に変えて取り除く (スパースにしない。実際に 10 GiB を書く)。
    /// </summary>
    private static void WriteFindRandom(string path) => WriteFindRandom(path, 10 * GiB);

    /// <summary>TD-FIND-RANDOM-10G の作り方で、長さ <paramref name="length"/> のファイルを作る (生成の確認のテスト用に長さを変えられる)。</summary>
    public static void WriteFindRandom(string path, long length, Action<long, Span<byte>>? plant = null)
    {
        using FileStream stream = File.Create(path);
        stream.SetLength(length);
        long tailAt = length - FindRandomTail.Length;
        byte[] buffer = new byte[MiB + Lookahead];
        var pending = new List<long>();
        for (long offset = 0; offset < length; offset += MiB)
        {
            int n = (int)Math.Min(MiB, length - offset);
            int withLookahead = (int)Math.Min(n + Lookahead, length - offset);
            Span<byte> chunk = buffer.AsSpan(0, withLookahead);
            Random(FindRandomSeed, offset, chunk);
            plant?.Invoke(offset, chunk);
            for (long p = Math.Max(offset, tailAt); p < offset + withLookahead; p++)
            {
                chunk[(int)(p - offset)] = FindRandomTail[p - tailAt];
            }

            // 前のチャンクの先読みの範囲で 00 にした位置は、作り直したこのチャンクにも反映する。
            foreach (long p in pending)
            {
                chunk[(int)(p - offset)] = 0x00;
            }

            pending.Clear();
            RemoveFindMatches(chunk, n, offset, tailAt, pending);
            stream.Write(buffer, 0, n);
        }
    }

    private const int Lookahead = 7;

    /// <summary>TD-FIND-RANDOM-10G から取り除く並び。(値, マスク) でマスクが 0 のニブルは任意。</summary>
    private static readonly (byte Value, byte Mask)[][] FindPatterns =
    [
        [(0x48, 0xFF), (0x45, 0xFF), (0x58, 0xFF), (0x45, 0xFF), (0x4E, 0xFF), (0x44, 0xFF), (0x21, 0xFF), (0x21, 0xFF)],
        [(0x48, 0xFF), (0x45, 0xFF), (0x00, 0x00), (0x45, 0xFF), (0x4E, 0xFF), (0x44, 0xFF)],
        [(0x40, 0xF0), (0x45, 0xFF), (0x58, 0xFF), (0x45, 0xFF), (0x4E, 0xFF), (0x40, 0xF0)],
        [(0x00, 0x00), (0x45, 0xFF), (0x58, 0xFF), (0x00, 0x00), (0x4E, 0xFF), (0x44, 0xFF)],
    ];

    /// <summary>
    /// チャンクの先頭 <paramref name="n"/> バイトから始まる一致を取り除く。どの並びも 2 バイト目は 45 なので、そこを 00 にすると、その位置から
    /// 始まるすべての並びの一致が消える (先頭が ?? の並びがあるため、先頭のバイトを変えても消えない)。どの並びの固定のニブルも 0 でないため、
    /// 00 にしたバイトが新しい一致を作ることはない。先読みの範囲 (次のチャンク) を 00 にした位置は <paramref name="pending"/> に入れる。
    /// </summary>
    private static void RemoveFindMatches(Span<byte> chunk, int n, long offset, long tailAt, List<long> pending)
    {
        for (int i = 0; i < n && offset + i < tailAt; i++)
        {
            if (i + 1 >= chunk.Length || chunk[i + 1] != 0x45 || offset + i + 1 >= tailAt)
            {
                continue;
            }

            foreach ((byte Value, byte Mask)[] pattern in FindPatterns)
            {
                if (i + pattern.Length <= chunk.Length && Matches(chunk[i..], pattern))
                {
                    chunk[i + 1] = 0x00;
                    if (i + 1 >= n)
                    {
                        pending.Add(offset + i + 1);
                    }

                    break;
                }
            }
        }

        static bool Matches(ReadOnlySpan<byte> data, (byte Value, byte Mask)[] pattern)
        {
            for (int k = 0; k < pattern.Length; k++)
            {
                if ((data[k] & pattern[k].Mask) != pattern[k].Value)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>スパースファイルを作り、[<paramref name="dataOffset"/>, + <paramref name="dataLength"/>) だけに内容を書く。</summary>
    private static void WriteSparse(string path, long length, long dataOffset, long dataLength, Action<long, Span<byte>> fill)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        MakeSparse(handle);
        RandomAccess.SetLength(handle, length);
        byte[] buffer = new byte[MiB];
        for (long offset = dataOffset; offset < dataOffset + dataLength; offset += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, dataOffset + dataLength - offset);
            fill(offset, buffer.AsSpan(0, n));
            RandomAccess.Write(handle, buffer.AsSpan(0, n), offset);
        }
    }

    private static void WriteGenerated(string path, long length, Action<long, Span<byte>> fill)
    {
        using FileStream stream = File.Create(path);
        byte[] buffer = new byte[MiB];
        for (long offset = 0; offset < length; offset += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, length - offset);
            fill(offset, buffer.AsSpan(0, n));
            stream.Write(buffer, 0, n);
        }
    }

    /// <summary>スパースファイルを作り、指定の位置に目印を書く。それ以外は 00 (実際のディスク使用量はほぼ 0)。</summary>
    private static void WriteMarkers(string path, long length, IEnumerable<long> positions)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        MakeSparse(handle);
        RandomAccess.SetLength(handle, length);
        foreach (long p in positions.Distinct().Order())
        {
            RandomAccess.Write(handle, Marker(p), p);
        }
    }

    private static void MakeSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Windows 以外のファイルシステムは書き込みのない範囲を自動で疎にする。
        }

        const uint FsctlSetSparse = 0x000900C4;
        if (!DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException("スパースファイルにできません。NTFS のドライブが必要です。", Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
