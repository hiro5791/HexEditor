using System.Diagnostics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>
/// 複数ファイル検索 (FIND-30) の件数の上限 (仕様 8 = FIND-20 の仕様 6・7)、一致の置き場所 (メモリ上は上限まで、超える分は一時ファイル)、
/// 「続ける」、結果一覧の行の並びとチェック、検索条件の共通の作り方 (仕様 1)、置換の細部 (FIND-31)。
/// ファイルの中身は開いているドキュメント (仮想のデータソース) で与え、ディスクには空のファイルだけを作る。
/// </summary>
public sealed class MultiFileSearchLimitTests : IDisposable
{
    /// <summary>仮想のデータ (offset &amp; 0xFF) の中の `00` は 256 バイトごとに 1 件。</summary>
    private static readonly SearchPattern Zero = SearchPattern.FromHex("00");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HexEditor", "tests", "multilimit-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Document> _documents = [];

    public MultiFileSearchLimitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (Document d in _documents)
        {
            d.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// <paramref name="files"/> 個のファイル (中身は空) を作り、それぞれを「開いているドキュメント」として、`00` が <paramref name="matchesPerFile"/>
    /// 件ある仮想のデータを返す。
    /// </summary>
    private (MultiFileTargets Targets, Func<string, OpenDocumentSnapshot?> Open) VirtualFiles(int files, int matchesPerFile)
    {
        string dir = Path.Combine(_root, "v" + files + "x" + matchesPerFile);
        Directory.CreateDirectory(dir);
        var map = new Dictionary<string, OpenDocumentSnapshot>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files; i++)
        {
            string path = Path.Combine(dir, $"f{i:D4}.bin");
            File.WriteAllBytes(path, []);
            var doc = new Document(new VirtualByteSource(256L * matchesPerFile, VirtualContent.OffsetLowByte));
            _documents.Add(doc);
            map[path] = new OpenDocumentSnapshot(doc.Current, doc.Length, File.GetLastWriteTimeUtc(path));
        }

        return (new MultiFileTargets { Folders = [dir] }, p => map.TryGetValue(p, out OpenDocumentSnapshot? s) ? s : null);
    }

    /// <summary>どのファイルも、`00` の位置 (0, 256, 512, …) を重複も取りこぼしもなく持つ。</summary>
    private static void AssertEveryFileComplete(MultiFileSearchResults results, int files, int matchesPerFile)
    {
        Assert.Equal(files, results.FileCount);
        for (int i = 0; i < results.FileCount; i++)
        {
            FileSearchResult f = results.FileAt(i);
            Assert.False(f.Incomplete);
            IReadOnlyList<SearchMatch> matches = f.Matches;
            Assert.Equal(matchesPerFile, matches.Count);
            for (int k = 0; k < matches.Count; k++)
            {
                Assert.Equal(256L * k, matches[k].Offset);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task TotalLimitIsExactAndContinueFindsTheRestWithoutDuplicates(int parallelism)
    {
        // 20 ファイル × 1,000 件。上限 4,500 件ちょうどで止め (並列数に関係なく)、「続ける」で上限を 2 倍にして続きから探す。
        (MultiFileTargets targets, Func<string, OpenDocumentSnapshot?> open) = VirtualFiles(20, 1_000);
        using var results = new MultiFileSearchResults(Zero, targets);
        await MultiFileSearch.RunAsync(results, new SearchOptions(), open, parallelism, totalLimit: 4_500);
        Assert.Equal(MultiFileSearchState.LimitReached, results.State);
        Assert.Equal(4_500, results.MatchCount);
        Assert.Equal(4_500, Enumerable.Range(0, results.FileCount).Sum(i => results.FileAt(i).MatchCount));
        Assert.True(results.CanContinue);
        Assert.Equal(1, Enumerable.Range(0, results.FileCount).Count(i => results.FileAt(i).Incomplete));

        await MultiFileSearch.ContinueAsync(results, new SearchOptions(), open, parallelism);
        Assert.Equal((MultiFileSearchState.LimitReached, 9_000L, 9_000L), (results.State, results.Limit, results.MatchCount));
        await MultiFileSearch.ContinueAsync(results, new SearchOptions(), open, parallelism);
        Assert.Equal((MultiFileSearchState.LimitReached, 18_000L), (results.State, results.MatchCount));
        await MultiFileSearch.ContinueAsync(results, new SearchOptions(), open, parallelism);
        Assert.Equal((MultiFileSearchState.Completed, 20_000L), (results.State, results.MatchCount));
        Assert.False(results.CanContinue);
        AssertEveryFileComplete(results, 20, 1_000);
        Assert.Equal(20, results.ProcessedFiles);
        Assert.Equal(20, results.FoundFiles);
        Assert.Equal(0, results.FilesWithoutMatches);
    }

    [Fact]
    public async Task PerFileLimitIsShownPerFile()
    {
        (MultiFileTargets targets, Func<string, OpenDocumentSnapshot?> open) = VirtualFiles(3, 1_000);
        using var results = new MultiFileSearchResults(Zero, targets);
        await MultiFileSearch.RunAsync(results, new SearchOptions(), open, perFileLimit: 300);
        Assert.Equal(MultiFileSearchState.Completed, results.State);
        Assert.Equal(900, results.MatchCount);
        Assert.All(results.Files, f => Assert.Equal((300, true, false), (f.MatchCount, f.LimitReached, f.Incomplete)));
    }

    [Fact]
    public async Task MatchesBeyondTheMemoryLimitAreSpilledToATemporaryFile()
    {
        // メモリ上は 5,000 件まで。残りの 15,000 件は一時ファイルに書き、読み戻しても同じ (FIND-20 の仕様 7)。
        (MultiFileTargets targets, Func<string, OpenDocumentSnapshot?> open) = VirtualFiles(20, 1_000);
        string spill = Path.Combine(_root, "spill");
        var results = new MultiFileSearchResults(Zero, targets) { MemoryLimit = 5_000, SpillDirectory = spill };
        await MultiFileSearch.RunAsync(results, new SearchOptions(), open, 4);
        Assert.Equal((MultiFileSearchState.Completed, 20_000L), (results.State, results.MatchCount));
        Assert.True(results.InMemoryMatches <= 5_000, results.InMemoryMatches.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotEmpty(Directory.EnumerateFiles(spill));
        AssertEveryFileComplete(results, 20, 1_000);
        results.Dispose();
        Assert.Empty(Directory.EnumerateFiles(spill));
    }

    [Fact]
    public async Task WhenTheTemporaryFileFailsTheSearchStopsAtTheMemoryLimit()
    {
        // 一時ファイルを作れない (フォルダの名前がファイル): メモリ上の件数で止め、理由を持つ。続けられない (FIND-20 の「エラー」)。
        string notAFolder = Path.Combine(_root, "file-not-folder");
        File.WriteAllBytes(notAFolder, []);
        using (var store = new MatchStore { MemoryLimit = 10, SpillDirectory = notAFolder })
        {
            SearchMatch[] matches = [.. Enumerable.Range(0, 20).Select(i => new SearchMatch(i, 1))];
            Assert.Equal(10, store.Append(matches, 20, out long first));
            Assert.Equal((0L, 10L, true), (first, store.Count, store.SpillFailed));
            Assert.Equal(0, store.Append(matches, 5, out _));
        }

        (MultiFileTargets targets, Func<string, OpenDocumentSnapshot?> open) = VirtualFiles(5, 100);
        using var results = new MultiFileSearchResults(Zero, targets) { MemoryLimit = 250, SpillDirectory = notAFolder };
        await MultiFileSearch.RunAsync(results, new SearchOptions(), open, 2);
        Assert.Equal((MultiFileSearchState.LimitReached, 250L, true, false), (results.State, results.MatchCount, results.SpillFailed, results.CanContinue));
        Assert.NotNull(results.SpillError);
    }

    [Fact]
    public async Task TwoMillionMatchesKeepAtMostOneMillionInMemory()
    {
        // 既定の上限: 全体 1,000,000 件で止め、続けると 2,000,000 件。メモリ上は 1,000,000 件まで (FIND-30 の仕様 8、FIND-20 の仕様 6・7)。
        (MultiFileTargets targets, Func<string, OpenDocumentSnapshot?> open) = VirtualFiles(200, 10_000);
        using var results = new MultiFileSearchResults(Zero, targets) { SpillDirectory = Path.Combine(_root, "spill2") };
        var watch = Stopwatch.StartNew();
        await MultiFileSearch.RunAsync(results, new SearchOptions(), open);
        Assert.Equal((MultiFileSearchState.LimitReached, 1_000_000L), (results.State, results.MatchCount));
        await MultiFileSearch.ContinueAsync(results, new SearchOptions(), open);
        Assert.Equal((MultiFileSearchState.Completed, 2_000_000L), (results.State, results.MatchCount));
        Assert.True(results.InMemoryMatches <= MatchStore.DefaultMemoryLimit);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), watch.Elapsed.ToString());

        // 一時ファイルに書いた分も正しく読める (最後のファイルの最後の一致)。
        FileSearchResult last = results.FileAt(results.FileCount - 1);
        Assert.Equal(256L * 9_999, last.MatchAt(9_999).Offset);
    }

    [Fact]
    public async Task ResultViewMapsRowsWithoutObjectsAndKeepsChecks()
    {
        (MultiFileTargets targets, Func<string, OpenDocumentSnapshot?> open) = VirtualFiles(3, 100);
        using var results = new MultiFileSearchResults(Zero, targets);
        await MultiFileSearch.RunAsync(results, new SearchOptions(), open, 1, totalLimit: 150);
        var view = new MultiFileResultView(results);
        Assert.True(view.Refresh());
        Assert.Equal(2 + 150, view.RowCount); // ファイル 2 つ (100 件と 50 件)
        Assert.Equal((0, -1), view.RowAt(0));
        Assert.Equal((0, 99), view.RowAt(100));
        Assert.Equal((1, -1), view.RowAt(101));
        Assert.Equal((1, 49), view.RowAt(151));
        Assert.Equal(101, view.RowOf(1, -1));

        // チェック: 一致を外すとファイルの行は一部 (null)。ファイルの行で全部を切り替える。
        view.SetChecked(0, 5, false);
        Assert.Null(view.IsChecked(0, -1));
        Assert.Equal(99, view.CheckedMatches(0).Count);
        Assert.DoesNotContain(256L * 5, view.CheckedMatches(0).Select(m => m.Offset));
        view.SetChecked(1, -1, false);
        Assert.False(view.IsChecked(1, -1));
        Assert.Empty(view.CheckedMatches(1));
        view.SetChecked(1, 3, true);
        Assert.Equal([256L * 3], view.CheckedMatches(1).Select(m => m.Offset));

        // 「続ける」で途中までのファイルに一致が加わると、後ろの行がずれる。チェックはそのまま。
        Assert.False(view.Refresh());
        await MultiFileSearch.ContinueAsync(results, new SearchOptions(), open, 1);
        Assert.True(view.Refresh());
        Assert.Equal(3 + 300, view.RowCount);
        Assert.Equal((2, -1), view.RowAt(202));
        Assert.Equal(100, view.MatchCount(1));
        Assert.Equal([256L * 3], view.CheckedMatches(1).Select(m => m.Offset));
        Assert.True(view.IsChecked(2, 0));
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-04")]
    public async Task UnreadableFilesAreNotCountedAsFilesWithoutMatches()
    {
        string dir = Path.Combine(_root, "skip");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), [0xCA, 0xFE, 0xBA, 0xBE]);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), [1, 2, 3, 4]);
        string locked = Path.Combine(dir, "c.bin");
        File.WriteAllBytes(locked, [0xCA, 0xFE, 0xBA, 0xBE]);
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        using var results = new MultiFileSearchResults(SearchPattern.FromHex("CA FE BA BE"), new MultiFileTargets { Folders = [dir] });
        await MultiFileSearch.RunAsync(results, new SearchOptions(), null);
        Assert.Equal((1, 1, 3), (results.FileCount, results.FilesWithoutMatches, results.ProcessedFiles));
        Assert.Contains(results.Skipped, s => s.Path == locked && s.Reason == FileSkipReason.InUse);
    }

    [Fact]
    public async Task RegexChunksSkippedByTheTimeLimitAreRecorded()
    {
        // 後戻りする方式の正規表現が時間の上限に達したチャンクは、飛ばして「スキップしたファイル」に範囲付きで記録する (FIND-18 の「エラー」)。
        string dir = Path.Combine(_root, "regex");
        Directory.CreateDirectory(dir);
        string slow = Path.Combine(dir, "slow.txt");
        File.WriteAllBytes(slow, Encoding.ASCII.GetBytes(new string('a', 64 * 1024)));
        SearchPattern pattern = RegexSearch.Text("(a+)+(?=b)", Encoding.ASCII, new RegexSearchOptions { TimeLimit = RegexSearch.MinTimeLimit });
        using var results = new MultiFileSearchResults(pattern, new MultiFileTargets { Folders = [dir] });
        await MultiFileSearch.RunAsync(results, new SearchOptions(), null);
        Assert.Equal(MultiFileSearchState.Completed, results.State);
        SkippedFile skipped = Assert.Single(results.Skipped);
        Assert.Equal((slow, FileSkipReason.RegexTimedOut), (skipped.Path, skipped.Reason));
        Assert.StartsWith("0x0", skipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-FIND-30-07")]
    public void FoldersUnderALinkTargetAreVisitedOnce()
    {
        // 0m → X\Y と 1l → X の 2 つのジャンクション。1l\Y は X\Y と同じフォルダなので、X\Y\f.bin は 1 回だけ列挙する。
        string root = Path.Combine(_root, "links");
        string y = Path.Combine(root, "X", "Y");
        Directory.CreateDirectory(y);
        File.WriteAllBytes(Path.Combine(y, "f.bin"), [1]);
        CreateJunction(Path.Combine(root, "0m"), y);
        CreateJunction(Path.Combine(root, "1l"), Path.Combine(root, "X"));
        string[] files = [.. MultiFileSearch.Enumerate(new MultiFileTargets { Folders = [root], FollowLinks = true })];
        Assert.Single(files, f => f.EndsWith("f.bin", StringComparison.Ordinal));
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "ジャンクションを作れません。");
    }

    [Fact]
    [Trait(TC, "TC-FIND-31-04")]
    public void ReplacementsThatCannotBePlannedInAnOpenDocumentAreRecorded()
    {
        // 「埋めて長さを保つ」で置換語が一致より長い: エディタのドキュメントは変えず、理由を記録する (黙って捨てない)。
        using var doc = new Document(new MemoryByteSource([0xCA, 0xFE, 0xBA, 0xBE, 0, 0xCA, 0xFE, 0xBA, 0xBE]));
        var editor = new EditorState(doc);
        SearchPattern pattern = SearchPattern.FromHex("CA FE BA BE");
        var matches = new List<SearchMatch> { new(0, 4), new(5, 4) };
        var tooLong = new MultiFileReplaceOptions
        {
            Template = ReplacementTemplate.FromHex("11 22 33 44 55 66"),
            ReplaceOptions = new ReplaceOptions { Policy = LengthPolicy.PadKeepLength },
            JournalDirectory = Path.Combine(_root, "journal"),
        };
        FileReplaceOutcome outcome = MultiFileSearch.ReplaceInEditor(editor, "x.bin", matches, pattern, tooLong, "replace");
        Assert.Equal((FileReplaceStatus.Skipped, FileSkipReason.CannotReplace, nameof(ReplaceIssue.TooLong)), (outcome.Status, outcome.Reason!.Value, outcome.Message));
        Assert.False(doc.IsModified);

        // 埋め草 FF (FIND-24 の仕様 3) で 2 バイトの置換語: 残りは FF。
        FileReplaceOutcome padded = MultiFileSearch.ReplaceInEditor(editor, "x.bin", matches, pattern,
            tooLong with { Template = ReplacementTemplate.FromHex("11 22"), ReplaceOptions = new ReplaceOptions { Policy = LengthPolicy.PadKeepLength, Filler = [0xFF] } },
            "replace");
        Assert.Equal((FileReplaceStatus.ReplacedInEditor, 2L), (padded.Status, padded.Count));
        Assert.Equal(new byte[] { 0x11, 0x22, 0xFF, 0xFF, 0, 0x11, 0x22, 0xFF, 0xFF }, ReadAll(doc));
    }

    private static byte[] ReadAll(Document doc)
    {
        byte[] bytes = new byte[doc.Length];
        doc.Current.Read(0, bytes);
        return bytes;
    }

    // ---- 検索条件の共通の作り方 (FIND-30 の仕様 1) ----

    private static IReadOnlyList<long> FindAll(byte[] data, SearchQuery query)
    {
        using var doc = new Document(new MemoryByteSource(data));
        SearchPattern pattern = SearchQueryBuilder.Build(query, new SearchQueryEnvironment()).Pattern;
        using SearchResults results = pattern.IsMismatch
            ? MismatchSearch.CreateResults(doc.Current, pattern, new SearchOptions())
            : new SearchResults(doc.Current, pattern, new SearchOptions());
        SearchEngine.FindAll(results);
        return [.. results.Matches.Select(m => m.Offset)];
    }

    [Fact]
    public void QueryBuilderSupportsTheFindBarKindsAndOptions()
    {
        // 16 bit ビッグエンディアンの整数、範囲、浮動小数点の許容誤差、正規表現のフラグ、ビットマスク、位置の条件、単語単位、複数の文字コード、複数の語。
        byte[] data = [0x12, 0x34, 0x00, 0x00, 0x34, 0x12, 0x00, 0x00];
        Assert.Equal([0L], FindAll(data, new SearchQuery { Kind = SearchKind.Integer, Text = "0x1234", IntegerBits = 16, Endian = SearchEndian.Big }));
        Assert.Equal([4L], FindAll(data, new SearchQuery { Kind = SearchKind.Integer, Text = "0x1234", IntegerBits = 16 }));
        Assert.Equal([0L, 4L], FindAll(data, new SearchQuery { Kind = SearchKind.Integer, Text = "0x1234", IntegerBits = 16, Endian = SearchEndian.Both }));
        Assert.Contains(4L, FindAll(data, new SearchQuery { Kind = SearchKind.Integer, Text = "0x1230..0x1240", IntegerBits = 16 }));

        byte[] floats = BitConverter.GetBytes(1.0005f);
        Assert.Empty(FindAll(floats, new SearchQuery { Kind = SearchKind.Float, Text = "1" }));
        Assert.Equal([0L], FindAll(floats, new SearchQuery { Kind = SearchKind.Float, Text = "1", Tolerance = ToleranceKind.Absolute, ToleranceText = "0.001" }));

        byte[] text = Encoding.ASCII.GetBytes("ab\nAB cat category");
        Assert.Equal([3L], FindAll(text, new SearchQuery { Kind = SearchKind.RegexText, Text = "^AB", RegexMultiline = true, RegexIgnoreCase = false }));
        Assert.Equal([0L, 3L], FindAll(text, new SearchQuery { Kind = SearchKind.RegexText, Text = "^ab", RegexMultiline = true, RegexIgnoreCase = true }));
        Assert.Equal([6L], FindAll(text, new SearchQuery { Kind = SearchKind.Text, Text = "cat", WholeWord = true }));
        Assert.Equal([6L, 10L], FindAll(text, new SearchQuery { Kind = SearchKind.Text, Text = "cat" }));

        Assert.Equal([0L], FindAll(data, new SearchQuery { Kind = SearchKind.Hex, Text = "10 30", Mask = true, MaskText = "F0 F0" }));
        Assert.Equal([3L, 7L], FindAll(data, new SearchQuery { Kind = SearchKind.Hex, Text = "00", Position = PositionCondition.Create(4, 3) }));

        byte[] utf16 = [.. Encoding.ASCII.GetBytes("hi"), .. Encoding.Unicode.GetBytes("hi")];
        Assert.Equal([0L, 2L], FindAll(utf16, new SearchQuery { Kind = SearchKind.Text, Text = "hi", Encodings = ["ascii", "utf-16le"] }));
        Assert.Equal([0L, 4L], FindAll(data, new SearchQuery
        {
            Terms = [new SearchTerm { Kind = SearchKind.Hex, Text = "12 34" }, new SearchTerm { Kind = SearchKind.Hex, Text = "34 12" }],
        }));
        Assert.False(new SearchQuery { Terms = [] }.CanReplace);
        Assert.False(new SearchQuery { Mismatch = true }.CanReplace);
        PatternException error = Assert.Throws<PatternException>(() => SearchQueryBuilder.Build(
            new SearchQuery { Kind = SearchKind.Float, Text = "1", Tolerance = ToleranceKind.Absolute, ToleranceText = "x" }, new SearchQueryEnvironment()));
        Assert.Equal(PatternError.InvalidTolerance, error.Error);
    }
}
