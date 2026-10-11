using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>文字列の抽出の条件 (FIND-32 の仕様 1)。</summary>
public sealed record StringExtractionOptions
{
    /// <summary>最小の長さの範囲と既定値 (文字数。1〜1,024、既定 4)。</summary>
    public const int DefaultMinLength = 4;

    public const int MaxMinLength = 1024;

    /// <summary>表示する最大の長さ (文字数。これを超える文字列も分割せず、表示は最初のこの文字数まで。既定 4,096)。</summary>
    public const int DefaultMaxLength = 4096;

    public int MinLength { get; init; } = DefaultMinLength;

    public int MaxLength { get; init; } = DefaultMaxLength;

    /// <summary>文字コード (名前と文字コード。複数。既定は ASCII と UTF-16LE)。一覧の順が優先の順 (仕様 2)。</summary>
    public IReadOnlyList<(string Name, Encoding Encoding)> Encodings { get; init; } = [];

    /// <summary>「印字可能な文字と改行」(CR と LF を含める)。既定は「印字可能な文字」。</summary>
    public bool IncludeNewlines { get; init; }

    /// <summary>「NUL で終わるものだけ」(既定オフ)。</summary>
    public bool NulTerminatedOnly { get; init; }
}

/// <summary>
/// 文字列の抽出 (FIND-32)。ドキュメントの中から、指定の文字コードで読める文字が最小の長さ以上続く箇所を探し、共通の結果一覧に出す
/// (`strings` コマンドに相当)。先頭から順に読み、文字コード (UTF-16 / UTF-32 は位相ごと) ごとに文字の並びを数える。
/// 同じ範囲が複数の文字コードで文字列になる場合は、長い方 (同じ長さなら一覧で先に選んだ文字コード) を報告する (仕様 2)。
/// 結果の <see cref="SearchMatch.Variant"/> は文字コードの番号、<see cref="SearchMatch.Extra"/> は文字数。
/// </summary>
public static class StringExtractor
{
    /// <summary>文字列の抽出の結果を作る (<see cref="SearchEngine.FindAll(SearchResults, LongRunningOperation?, CancellationToken)"/> で抽出する)。</summary>
    public static SearchResults CreateResults(DocumentSnapshot snapshot, StringExtractionOptions options, SearchOptions searchOptions)
    {
        if (options.Encodings.Count == 0)
        {
            throw new PatternException(PatternError.NoEncoding);
        }

        _ = TextEncodings.FromCatalogId("ascii");
        return new SearchResults(snapshot, SearchPattern.Literal([0]), searchOptions)
        {
            VariantEncodings = [.. options.Encodings.Select(e => e.Encoding)],
            VariantNames = [.. options.Encodings.Select(e => e.Name)],
            VariantColumnOverride = VariantColumn.Encoding,
            HighlightFromResults = true,
            CustomFindAll = (results, from, operation, token) => Run(results, options, from, operation, token),
        };
    }

    /// <summary>文字列の抽出の結果か。</summary>
    public static bool IsStrings(SearchResults results) => results.VariantEncodings is not null && results.HighlightFromResults;

    /// <summary>
    /// 一致の文字列 (先頭から最大 <paramref name="maxChars"/> 文字。表示できない文字は「.」)。文字列の抽出の結果の「文字列」の列。
    /// </summary>
    public static string Text(ReadOnlySpan<byte> bytes, Encoding encoding, int maxChars)
    {
        ByteTextDecoder decoder = ByteTextDecoder.For(encoding);
        var sb = new StringBuilder();
        int at = 0;
        int chars = 0;
        while (at < bytes.Length && chars < maxChars)
        {
            int used = decoder.Next(bytes, at, out int cp);
            sb.Append(cp < 0 || Rune.IsControl(new Rune(cp)) ? "." : char.ConvertFromUtf32(cp));
            chars++;
            at += Math.Max(1, used);
        }

        return sb.ToString();
    }

    /// <summary>1 つの文字コードの 1 つの位相の読み取りの状態。</summary>
    private sealed class Lane(int variant, ByteTextDecoder decoder, bool ascii, long next, int phase)
    {
        public int Variant { get; } = variant;

        /// <summary>位相 (範囲の先頭からの位置を文字の単位で割った余り)。</summary>
        public int Phase { get; } = phase;

        public ByteTextDecoder Decoder { get; } = decoder;

        /// <summary>ASCII (0x20〜0x7E とタブ。仕様 1)。</summary>
        public bool Ascii { get; } = ascii;

        /// <summary>次に読む文字の位置。</summary>
        public long Next { get; set; } = next;

        public bool Active { get; set; }

        public long RunStart { get; set; }

        public long RunEnd { get; set; }

        public long RunChars { get; set; }
    }

    private readonly record struct Candidate(long Start, long Length, int Variant, long Chars, bool NulTerminated);

    private static bool Run(SearchResults results, StringExtractionOptions options, long startFrom, LongRunningOperation? operation,
        CancellationToken token)
    {
        DocumentSnapshot snapshot = results.Snapshot;
        IReadOnlyList<SearchRange> ranges = results.Options.Scope.Resolve(snapshot.Length);
        int minLength = Math.Clamp(options.MinLength, 1, StringExtractionOptions.MaxMinLength);
        int chunkSize = Math.Max(4096, results.Options.ChunkSize);
        const int Tail = 8; // 文字がチャンクの境界をまたぐ分
        byte[] buffer = GC.AllocateUninitializedArray<byte>(chunkSize + Tail);
        long done = ranges.Sum(r => Math.Clamp(startFrom - r.Offset, 0, r.Length));
        operation?.Report(done);
        var pending = new List<Candidate>();
        var emitted = new List<SearchMatch>();
        bool limited = false;

        // 候補を決める: 重なる候補のまとまりのうち、最大の末尾が threshold 以下のものを確定する (以後の候補とは重ならない)。
        bool Settle(long threshold)
        {
            pending.Sort((a, b) => a.Start.CompareTo(b.Start));
            int i = 0;
            while (i < pending.Count)
            {
                int j = i;
                long end = pending[i].Start + pending[i].Length;
                while (j + 1 < pending.Count && pending[j + 1].Start < end)
                {
                    j++;
                    end = Math.Max(end, pending[j].Start + pending[j].Length);
                }

                if (end > threshold)
                {
                    break;
                }

                // まとまりの中で、長い方 (文字数。同じならバイト数、それも同じなら先に選んだ文字コード) から重ならないものを選ぶ (仕様 2)。
                List<Candidate> group = pending.GetRange(i, j - i + 1);
                var chosen = new List<Candidate>();
                foreach (Candidate c in group.OrderByDescending(c => c.Chars).ThenByDescending(c => c.Length).ThenBy(c => c.Variant).ThenBy(c => c.Start))
                {
                    if (!chosen.Any(k => c.Start < k.Start + k.Length && k.Start < c.Start + c.Length))
                    {
                        chosen.Add(c);
                    }
                }

                // 「NUL で終わるものだけ」は、重なりを決めてから除く (NUL で終わらない長い文字列に重なる短い候補を出さない)。
                foreach (Candidate c in chosen.OrderBy(c => c.Start).Where(c => !options.NulTerminatedOnly || c.NulTerminated))
                {
                    if (results.LongCount + emitted.Count >= results.Limit)
                    {
                        return true;
                    }

                    emitted.Add(new SearchMatch(c.Start, c.Length, c.Variant, (int)Math.Min(int.MaxValue, c.Chars)));
                }

                i = j + 1;
            }

            pending.RemoveRange(0, i);
            if (emitted.Count > 0)
            {
                results.AddMatches([.. emitted]);
                emitted.Clear();
            }

            return false;
        }

        foreach (SearchRange r in ranges)
        {
            long first = Math.Max(r.Offset, startFrom);
            if (first >= r.End)
            {
                continue;
            }

            var lanes = new List<Lane>();
            for (int v = 0; v < options.Encodings.Count; v++)
            {
                Encoding encoding = options.Encodings[v].Encoding;
                var decoder = ByteTextDecoder.For(encoding);
                bool ascii = encoding.CodePage == 20127;
                for (int phase = 0; phase < decoder.Unit; phase++)
                {
                    lanes.Add(new Lane(v, decoder, ascii, first + phase, phase));
                }
            }

            long pos = first;
            while (pos < r.End)
            {
                token.ThrowIfCancellationRequested();
                operation?.CancellationToken.ThrowIfCancellationRequested();
                long coreEnd = Math.Min(r.End, pos + chunkSize);
                int count = (int)(Math.Min(r.End, coreEnd + Tail) - pos);
                ReadResult read = snapshot.Read(pos, buffer.AsSpan(0, count));
                count = read.BytesReturned;
                if (count <= 0)
                {
                    break;
                }

                // 読めない範囲: 文字列を区切る。読めない範囲の後ろから読み直す。
                long gapStart = long.MaxValue;
                long gapEnd = long.MaxValue;
                UnreadableRange? gapRange = null;
                foreach (UnreadableRange u in read.Unreadable.OrderBy(u => u.Offset))
                {
                    if (u.Offset < coreEnd && u.End > pos)
                    {
                        gapStart = Math.Max(pos, u.Offset);
                        gapEnd = Math.Min(r.End, u.End);
                        gapRange = u;
                        break;
                    }
                }

                long limitPos = Math.Min(coreEnd, gapStart);
                ReadOnlySpan<byte> data = buffer.AsSpan(0, (int)Math.Min(count, Math.Min(gapStart, pos + count) - pos));
                foreach (Lane lane in lanes)
                {
                    while (lane.Next < limitPos)
                    {
                        int at = (int)(lane.Next - pos);
                        if (at >= data.Length)
                        {
                            break;
                        }

                        int used;
                        bool printable;
                        if (lane.Ascii)
                        {
                            byte b = data[at];
                            used = 1;
                            printable = b is >= 0x20 and <= 0x7E or 0x09 || (options.IncludeNewlines && b is 0x0A or 0x0D);
                            if (!printable && lane.Active)
                            {
                                Close(lane, b == 0);
                            }
                        }
                        else
                        {
                            // チャンクの後ろに 8 バイト余分に読んでいるので、チャンクの中で始まる文字はバッファに収まる。
                            used = lane.Decoder.Next(data, at, out int cp);
                            printable = IsPrintable(cp, options.IncludeNewlines);
                            if (!printable && lane.Active)
                            {
                                Close(lane, cp == 0);
                            }
                        }

                        if (printable)
                        {
                            if (!lane.Active)
                            {
                                lane.Active = true;
                                lane.RunStart = lane.Next;
                                lane.RunChars = 0;
                            }

                            lane.RunChars++;
                            lane.RunEnd = lane.Next + Math.Max(1, used);
                        }

                        lane.Next += Math.Max(1, used);
                    }
                }

                if (gapStart < coreEnd)
                {
                    // 読めない範囲で文字列を区切り、知らせる (FIND-01 の「エラー」の扱い)。
                    foreach (Lane lane in lanes.Where(l => l.Active))
                    {
                        Close(lane, false);
                    }

                    UnreadableRange gap = gapRange!.Value with { Offset = gapStart, Length = gapEnd - gapStart };
                    if ((results.Options.OnUnreadable?.Invoke(gap) ?? UnreadableAction.Skip) == UnreadableAction.Abort)
                    {
                        throw new SearchAbortedException(gap);
                    }

                    results.AddSkipped(gap);
                    foreach (Lane lane in lanes)
                    {
                        int unit = lane.Decoder.Unit;
                        long aligned = gapEnd + ((((lane.Phase - (gapEnd - first)) % unit) + unit) % unit);
                        lane.Next = Math.Max(lane.Next, aligned);
                    }

                    done += gapEnd - pos;
                    pos = gapEnd;
                }
                else
                {
                    done += coreEnd - pos;
                    pos = coreEnd;
                }

                operation?.Report(done);
                long threshold = lanes.Min(l => l.Active ? l.RunStart : l.Next);
                if (Settle(threshold))
                {
                    limited = true;
                    break;
                }
            }

            if (limited)
            {
                break;
            }

            foreach (Lane lane in lanes.Where(l => l.Active))
            {
                Close(lane, false);
            }

            if (Settle(long.MaxValue))
            {
                limited = true;
                break;
            }
        }

        return limited;

        void Close(Lane lane, bool nulFollows)
        {
            lane.Active = false;
            if (lane.RunChars >= minLength)
            {
                pending.Add(new Candidate(lane.RunStart, lane.RunEnd - lane.RunStart, lane.Variant, lane.RunChars, nulFollows));
            }
        }
    }

    /// <summary>
    /// UTF-8 / UTF-16 などで印字可能な文字か (仕様 1): Unicode の制御文字 (Cc)・未割り当て (Cn)・サロゲートの片方だけ・私用領域 (Co) を除く。
    /// 「印字可能な文字と改行」なら CR と LF も含める。
    /// </summary>
    private static bool IsPrintable(int codePoint, bool newlines)
    {
        if (codePoint < 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return false;
        }

        if (codePoint is 0x0A or 0x0D)
        {
            return newlines;
        }

        return CharUnicodeInfo.GetUnicodeCategory(codePoint) is not (UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate);
    }
}
