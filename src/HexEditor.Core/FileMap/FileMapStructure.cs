using System.Buffers.Binary;
using System.IO.Compression;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Patches;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.FileMap;

public static partial class FileMapBuilder
{
    /// <summary>先頭の形式のテンプレートの評価の時間の上限 (TPL-30 と同じ 5 秒。評価の <c>IsAutoApply</c> で決まる)。</summary>
    public const double LeadingTemplateSeconds = 5;

    /// <summary>埋め込まれた形式のテンプレートの評価の上限 (1 つにつき時間 2 秒・パターン 100,000 個)。</summary>
    public static TemplateLimits EmbeddedTemplateLimits { get; } = TemplateLimits.Default with { TimeSeconds = 2, Patterns = 100_000 };

    /// <summary>繰り返しのレコードを調べる「バイナリ」の領域の最小の長さと数 (仕様 2 の 3 の (d))。</summary>
    public const long MinRecordRegion = 16 * 1024;

    public const int MaxRecordRegions = 64;

    /// <summary>カービングの定義の長さを決める読み取りの、全体の上限の最小 (対象の長さの 4 倍とこの大きい方)。</summary>
    public const long CarveBudgetMinimum = 256L << 20;

    /// <summary>自己相関を求める長さ (領域の先頭 256 KiB)。</summary>
    public const int RecordWindow = 256 * 1024;

    private sealed partial class Builder
    {
        /// <summary>
        /// 構造の解析 (仕様 2 の 3): (a) 先頭の形式のテンプレート、(b) 埋め込まれた形式の長さ、(c) 展開を確かめた圧縮のストリーム、
        /// (d) 繰り返しのレコード。最後に、内容のレーンの境目をバイト単位で確定する (走査の (d))。
        /// </summary>
        private sealed class StructureStage(Builder build)
        {
            private readonly ViewCarveData _carveData = new(build._snapshot);

            // 候補ごとに確保しない作業用の配列 (埋め込まれた形式の候補は最大 100,000 個。候補ごとに 64 KiB × 3 を確保すると、
            // シグネチャの多いデータで数 GB になる)。
            private readonly byte[] _headBytes = new byte[TemplateMatchInput.WindowBytes];
            private readonly byte[] _tailBytes = new byte[TemplateMatchInput.WindowBytes];
            private readonly byte[] _windowBuffer = new byte[ByteWindow.BlockSize];

            // カービングの定義の長さを決めるための読み取りの、すべての候補を合わせた上限 (読むバイト数)。シグネチャだけを敷き詰めたデータで、
            // 候補ごとにフッターを末尾まで探すと「候補の数 × 長さ」になるため。使い切ったら、長さは確度の低い方法 (次の領域まで) で決める。
            private long _carveBudget = Math.Max(CarveBudgetMinimum, 4 * (build.End - build._start));

            public List<FileMapRegion> Regions { get; } = [];

            public List<FileMapDetail> Details { get; } = [];

            public List<FileMapRegion>? FinalContent { get; private set; }

            public int EvaluatedEmbedded { get; private set; }

            public void Run()
            {
                build._runs.Finish();

                // 内容のレーン: 走査の分類 (ならし済み) から作り、境目を確定し、レコードとストリームを重ねる。
                List<FileMapRegion> content = build.Content(build._length);
                RefineConstantEdges(content);
                LabelTextAndConstants(content);
                FinalContent = content;
                build._token.ThrowIfCancellationRequested();
                DetectRecords(content);
                PaintStreams(content);
                FinalContent = content;
                build._token.ThrowIfCancellationRequested();

                LeadingTemplate();
                Embedded(content);
            }

            // ---- (a) 先頭の形式 ----

            private void LeadingTemplate()
            {
                if (build._request.Templates is not { } catalog || build._length == 0)
                {
                    return;
                }

                TemplateCandidate? candidate = TemplateFor(catalog, build._start, build._length, build._fileType?.Mime);
                if (candidate is null)
                {
                    return;
                }

                Evaluation e = Evaluate(candidate.Entry, catalog, build._start, build._length, leading: true);
                if (e.Nodes.Count == 0)
                {
                    return;
                }

                string name = candidate.Entry.Metadata.Name ?? candidate.Entry.Name;
                int[] map = new int[e.Nodes.Count];
                for (int n = 0; n < e.Nodes.Count; n++)
                {
                    EvaluatedNode node = e.Nodes[n];
                    var evidence = new List<FileMapMessage>
                    {
                        new("FileMap_Ev_StructurePattern", new FileMapName(name, candidate.Entry), node.Path, new FileMapHex(node.Offset),
                            new FileMapHex(node.Offset + node.Length - 1), node.Length),
                    };
                    if (e.Failure is { } failure)
                    {
                        evidence.Add(failure);
                    }

                    int parent = node.ParentIndex < 0 ? -1 : map[node.ParentIndex];
                    map[n] = Add(new FileMapRegion(node.Offset, node.Length, FileMapKind.Structure, e.Parsed ? Confidence.High : Confidence.Medium,
                        FileMapStage.Structure, -1, -1, float.NaN, float.NaN, 0), new FileMapDetail
                        {
                            Label = FileMapMessage.Raw(node.Label),
                            Evidence = evidence,
                            Template = candidate.Entry,
                            TemplateParsed = e.Parsed,
                            Format = name,
                            Depth = parent < 0 ? 0 : 1,
                        }, parentLocal: parent);
                }
            }

            // ---- (b) 埋め込まれた形式 ----

            private void Embedded(List<FileMapRegion> content)
            {
                TemplateCatalog? catalog = build._request.Templates;
                var candidates = build._embedded.Candidates.OrderBy(c => c.Offset).ToList();
                long coveredEnd = long.MinValue;
                for (int k = 0; k < candidates.Count; k++)
                {
                    build._token.ThrowIfCancellationRequested();
                    EmbeddedCandidate c = candidates[k];
                    if (c.Offset < coveredEnd || c.Offset >= build.End)
                    {
                        // 前の埋め込まれたファイルの中 (ZIP の 2 つ目以降のローカルヘッダなど) は、その一部とみなす。
                        continue;
                    }

                    long nextStart = k + 1 < candidates.Count ? candidates[k + 1].Offset : build.End;
                    CarvingSignature? sig = c.Carving ?? CarvingAt(c.Offset);
                    string name = c.Format?.Name ?? sig?.Name ?? "?";
                    string? extension = c.Format?.Extensions.FirstOrDefault() ?? sig?.Extension;
                    LengthMethod carvedBy = LengthMethod.MaxSize;
                    long? carved = sig is not null ? CarveLength(sig, c.Offset, out carvedBy) : null;

                    TemplateCandidate? template = catalog is null ? null
                        : TemplateFor(catalog, c.Offset, carved ?? (build.End - c.Offset), c.Format?.Mime);
                    var evidence = new List<FileMapMessage> { new("FileMap_Ev_Signature", new FileMapHex(c.Offset), name) };
                    long length;
                    Confidence confidence;
                    Evaluation? evaluation = null;
                    if (template is not null && EvaluatedEmbedded < MaxEvaluatedEmbedded)
                    {
                        EvaluatedEmbedded++;
                        evaluation = Evaluate(template.Entry, catalog!, c.Offset, carved ?? (build.End - c.Offset), leading: false);
                    }

                    string? templateName = template is null ? null : template.Entry.Metadata.Name ?? template.Entry.Name;
                    if (evaluation is { Parsed: true, End: > 0 } ok)
                    {
                        length = ok.End - c.Offset;
                        confidence = Confidence.High;
                        evidence.Add(new FileMapMessage("FileMap_Ev_TemplateParsed", new FileMapName(templateName!, template!.Entry), new FileMapHex(c.Offset),
                            new FileMapHex(ok.End - 1), length));
                    }
                    else
                    {
                        if (evaluation?.Failure is { } failure)
                        {
                            evidence.Add(failure);
                        }

                        if (carved is long carvedLength)
                        {
                            length = carvedLength;
                            confidence = Confidence.Medium;
                            evidence.Add(new FileMapMessage("FileMap_Ev_Carved", length, sig!.Name, new FileMapMessage("FileMap_Method_" + carvedBy)));
                        }
                        else
                        {
                            // 終わりが不明: 次の領域の始まりまで。
                            int at = ContentIndexAt(content, c.Offset);
                            long end = Math.Min(nextStart, at >= 0 ? content[at].End : build.End);
                            length = Math.Max(1, end - c.Offset);
                            confidence = Confidence.Low;
                            evidence.Add(new FileMapMessage("FileMap_Ev_EndUnknown", new FileMapHex(c.Offset + length), length));
                        }
                    }

                    length = Math.Min(length, build.End - c.Offset);
                    int parent = Add(new FileMapRegion(c.Offset, length, FileMapKind.Embedded, confidence, FileMapStage.Structure, -1, 0, float.NaN, float.NaN, 0),
                        new FileMapDetail
                        {
                            Label = FileMapMessage.Raw(name),
                            Evidence = evidence,
                            Template = template?.Entry,
                            TemplateParsed = evaluation?.Parsed == true,
                            Format = templateName ?? name,
                            Mime = c.Format?.Mime,
                            Extension = extension,
                        }, parentLocal: -1);
                    if (confidence >= Confidence.Medium)
                    {
                        coveredEnd = c.Offset + length;
                    }

                    // 埋め込まれたファイルの中に、そのテンプレートの最上位のパターン (2 段目)。
                    if (evaluation is { Parsed: true } parsed)
                    {
                        foreach (EvaluatedNode node in parsed.Nodes.Where(n => n.ParentIndex < 0).Take(1000))
                        {
                            Add(new FileMapRegion(node.Offset, node.Length, FileMapKind.Structure, Confidence.High, FileMapStage.Structure, 0, 0, float.NaN, float.NaN, 0),
                                new FileMapDetail
                                {
                                    Label = FileMapMessage.Raw(node.Label),
                                    Evidence =
                                    [
                                        new FileMapMessage("FileMap_Ev_StructurePattern", new FileMapName(templateName!, template!.Entry), node.Path, new FileMapHex(node.Offset),
                                            new FileMapHex(node.Offset + node.Length - 1), node.Length),
                                    ],
                                    Template = template.Entry,
                                    TemplateParsed = true,
                                    Format = templateName,
                                    Depth = 1,
                                }, parentLocal: parent);
                        }
                    }
                }
            }

            /// <summary>領域を足す (詳細も足す)。<paramref name="parentLocal"/> はこの段階の領域の番号。足した領域の番号を返す。</summary>
            private int Add(FileMapRegion region, FileMapDetail detail, int parentLocal)
            {
                if (Regions.Count >= MaxRegions / 2)
                {
                    return -1;
                }

                Details.Add(detail);
                Regions.Add(region with { Parent = parentLocal, Detail = Details.Count - 1 });
                return Regions.Count - 1;
            }

            /// <summary>位置 <paramref name="offset"/> から始まるデータに、自動適用の候補 (マジックを含む一致。TPL-30 の 1、2) のテンプレート。</summary>
            private TemplateCandidate? TemplateFor(TemplateCatalog catalog, long offset, long length, string? mime)
            {
                int head = (int)Math.Min(length, TemplateMatchInput.WindowBytes);
                build._snapshot.Read(offset, _headBytes.AsSpan(0, head));
                build._snapshot.Read(offset + length - head, _tailBytes.AsSpan(0, head));
                string? extension = offset == build._start && build._request.FileName is { } file ? Path.GetExtension(file).TrimStart('.') : null;
                var input = new TemplateMatchInput(length, _headBytes.AsMemory(0, head), _tailBytes.AsMemory(0, head), string.IsNullOrEmpty(extension) ? null : extension, mime);
                try
                {
                    return catalog.Detect(input).FirstOrDefault(c => c.Match.Magic);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            private sealed record EvaluatedNode(long Offset, long Length, string Path, string Label, int ParentIndex);

            private sealed class Evaluation
            {
                public List<EvaluatedNode> Nodes { get; } = [];

                public bool Parsed { get; set; }

                /// <summary>最上位のパターンの最後の終わり (ドキュメント上の位置)。</summary>
                public long End { get; set; }

                public FileMapMessage? Failure { get; set; }

            }

            /// <summary>
            /// テンプレートを [<paramref name="offset"/>, +<paramref name="length"/>) に適用して、最上位のパターン (と、先頭の形式では属性
            /// <c>[[bookmark]]</c> の付いたパターン) を集める。評価のエラーは失敗の文にする (ファイルマップ全体はエラーにしない)。
            /// </summary>
            private Evaluation Evaluate(TemplateCatalogEntry entry, TemplateCatalog catalog, long offset, long length, bool leading)
            {
                var e = new Evaluation();
                string name = entry.Metadata.Name ?? entry.Name;
                TemplateProgram program;
                try
                {
                    program = catalog.Load(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    e.Failure = new FileMapMessage("FileMap_Ev_TemplateFailed", new FileMapName(name, entry), "HXT1000");
                    return e;
                }

                if (program.HasErrors)
                {
                    e.Failure = new FileMapMessage("FileMap_Ev_TemplateFailed", new FileMapName(name, entry),
                        program.Diagnostics.First(d => d.Severity == TemplateSeverity.Error).Code);
                    return e;
                }

                var data = new SubViewTemplateData(build._snapshot, offset, length) { FileName = build._request.FileName is { } f ? Path.GetFileName(f) : null };
                var options = new TemplateApplyOptions { IsAutoApply = leading, LimitOverride = leading ? null : EmbeddedTemplateLimits };
                using TemplateResult result = TemplateEngine.ApplyAndWait(program, data, options, build._token);
                build._token.ThrowIfCancellationRequested();
                bool parsed = result.State == TemplateRunState.Completed && result.ErrorCount == 0;
                try
                {
                    int top = 0;
                    foreach (TemplateNode node in result.Root.Children)
                    {
                        if (++top > 10_000)
                        {
                            break;
                        }

                        if (node.Section is not null || node.Size <= 0 || node.HasError)
                        {
                            continue;
                        }

                        long s = offset + node.Offset;
                        long size = Math.Min(node.Size, offset + length - s);
                        if (size <= 0)
                        {
                            continue;
                        }

                        int index = e.Nodes.Count;
                        e.Nodes.Add(new EvaluatedNode(s, size, node.Path, LabelOf(node), -1));
                        e.End = Math.Max(e.End, s + size);
                        if (leading)
                        {
                            CollectBookmarks(node, offset, length, e, index);
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    parsed = false;
                }

                if (!parsed)
                {
                    string code = result.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error)?.Code
                        ?? (result.State == TemplateRunState.LimitReached ? "HXT2900" : "HXT2000");
                    e.Failure = new FileMapMessage("FileMap_Ev_TemplateFailed", new FileMapName(name, entry), code);
                }

                e.Parsed = parsed && e.Nodes.Count > 0;
                return e;
            }

            /// <summary>属性 <c>[[bookmark]]</c> (TPL-13) の付いたパターン (評価済みのものだけ。深さ 6、5,000 個まで) を 2 段目にする。</summary>
            private static void CollectBookmarks(TemplateNode top, long offset, long length, Evaluation e, int parent)
            {
                var stack = new Stack<(TemplateNode Node, int Depth)>();
                stack.Push((top, 0));
                int visited = 0;
                while (stack.Count > 0 && visited++ < 5000)
                {
                    (TemplateNode node, int depth) = stack.Pop();
                    if (depth > 0 && node.Attributes.Bookmark is not null && node.Size > 0 && node.Section is null)
                    {
                        long s = offset + node.Offset;
                        e.Nodes.Add(new EvaluatedNode(s, Math.Min(node.Size, offset + length - s), node.Path, LabelOf(node), parent));
                    }

                    if (depth < 6 && node.HasChildren && node.IsEvaluated)
                    {
                        IReadOnlyList<TemplateNode> children = node.Children;
                        for (int i = Math.Min(children.Count, 1000) - 1; i >= 0; i--)
                        {
                            stack.Push((children[i], depth + 1));
                        }
                    }
                }
            }

            /// <summary>ラベル: パターンのパスと、<c>name</c> のメンバーまたは文字列の値 (例: <c>sections[0] (.text)</c>)。</summary>
            private static string LabelOf(TemplateNode node)
            {
                string path = node.Path;
                try
                {
                    string? value = node.Value as string;
                    if (value is null && node.Kind == TemplateNodeKind.Struct && node.IsEvaluated)
                    {
                        value = (node.FindChild("name") ?? node.FindChild("Name"))?.Value as string;
                    }

                    value = value?.TrimEnd('\0').Trim();
                    return string.IsNullOrEmpty(value) ? path : $"{path} ({(value.Length > 40 ? value[..40] + "…" : value)})";
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    return path;
                }
            }

            // ---- カービングの定義の長さ (FOR-19 の 3: 構造の検証 → サイズの取得 → フッター) ----

            /// <summary>位置で、ヘッダが一致するカービングの定義 (固定の先頭が短くて走査で探さなかったものも含む)。</summary>
            private CarvingSignature? CarvingAt(long offset)
            {
                foreach (CarvingSignature sig in build._request.CarvingSignatures.Where(s => s.Enabled))
                {
                    foreach (HexMaskPattern header in sig.ParsedHeaders())
                    {
                        byte[] bytes = new byte[header.Length];
                        if (build._snapshot.Read(offset + sig.HeaderOffset, bytes).IsComplete && header.MatchesAt(bytes))
                        {
                            return sig;
                        }
                    }
                }

                return null;
            }

            private static LengthMethod MethodOf(CarvingSignature sig) =>
                sig.Structure is { Length: > 0 } ? LengthMethod.Structure : sig.SizeField is not null ? LengthMethod.SizeField : LengthMethod.Footer;

            private long? CarveLength(CarvingSignature sig, long start, out LengthMethod method)
            {
                method = MethodOf(sig);
                long limit = Math.Min(build.End, start > long.MaxValue - sig.MaxSize ? long.MaxValue : start + sig.MaxSize);
                if (limit <= start)
                {
                    return null;
                }

                if (_carveBudget <= 0)
                {
                    return null;
                }

                long budget = Math.Min(_carveBudget, Math.Min(512L << 20, (64L << 20) | ((limit - start) * 16)));
                var window = new ByteWindow(_carveData, limit, budget, build._token, _windowBuffer);
                if (sig.Structure is { Length: > 0 } structure)
                {
                    try
                    {
                        return StructureValidators.Find(structure)?.Measure(window, start) is long m && m >= Math.Max(1, sig.MinSize) ? m : null;
                    }
                    finally
                    {
                        _carveBudget -= budget - Math.Max(0, window.RemainingBudget);
                    }
                }

                if (sig.SizeField is { } rule)
                {
                    if (!window.TryUInt(start + rule.Offset, rule.Bits / 8, rule.BigEndian, out ulong value) || value > (ulong)long.MaxValue / 2)
                    {
                        return null;
                    }

                    long size = (long)value + rule.Add;
                    return size <= 0 || size > sig.MaxSize ? null : Math.Min(size, limit - start);
                }

                if (sig.ParsedFooter() is { } footer)
                {
                    long searchEnd = Math.Min(limit, start + 1 + _carveBudget);
                    long at = ByteSearch.Find(_carveData, start + 1, searchEnd, footer, build._token);
                    _carveBudget -= (at >= 0 ? at : searchEnd) - start;
                    return at >= 0 ? Math.Min(at - start + sig.FooterIncludeLength(footer), limit - start) : null;
                }

                return null;
            }

            // ---- (c) 圧縮のストリーム ----

            /// <summary>
            /// 展開を確かめたストリーム (ANA-16 の仕様 4) のうち、ブロック 1 つ以上の長さのものを、正確な範囲で「圧縮」(ラベルは形式名、確度 高) にする。
            /// 範囲の終わりは DEFLATE の最後のブロックの終わりと末尾のチェック値 (zlib の Adler-32、gzip の CRC-32 と長さ) で決め、チェック値が
            /// 合わないものは偶然の一致として除く。
            /// </summary>
            private void PaintStreams(List<FileMapRegion> content)
            {
                if (build._classification is not { } classification)
                {
                    return;
                }

                foreach (SignatureHit hit in classification.Signatures.Where(h => h.IsCompression && h.StreamLength >= build._blockSize))
                {
                    build._token.ThrowIfCancellationRequested();
                    if (ExactStream(hit.Offset, hit.Format) is not (long length, long produced))
                    {
                        continue;
                    }

                    length = Math.Min(length, build.End - hit.Offset);
                    Details.Add(new FileMapDetail
                    {
                        Label = FileMapMessage.Raw(hit.Format),
                        Evidence = [new FileMapMessage("FileMap_Ev_Stream", hit.Format, new FileMapHex(hit.Offset), length, produced)],
                        Format = hit.Format,
                        Extension = hit.Format == "gzip" ? "gz" : "zlib",
                    });
                    Paint(content, new FileMapRegion(hit.Offset, length, FileMapKind.Compressed, Confidence.High, FileMapStage.Structure, -1, Details.Count - 1,
                        float.NaN, float.NaN, 0));
                }
            }

            /// <summary>zlib・gzip のストリームの正確な長さと展開後の長さ。チェック値が合わなければ null。</summary>
            private (long Length, long Produced)? ExactStream(long offset, string format)
            {
                bool gzip = format == "gzip";
                if (!gzip && format != "zlib")
                {
                    return null;
                }

                long limit = Math.Min(build.End, offset + DataClassifier.MaxVerifyLength + 1024);
                var window = new ByteWindow(_carveData, limit, 1L << 31, build._token);
                long deflateStart;
                if (gzip)
                {
                    if (StructureValidators.Find("gzip")?.Measure(window, offset) is not long total)
                    {
                        return null;
                    }

                    // gzip: 末尾の CRC-32 と ISIZE で確かめる。DEFLATE の始まりはヘッダの後ろ。
                    deflateStart = GzipHeaderLength(offset) is long h ? offset + h : -1;
                    if (deflateStart < 0)
                    {
                        return null;
                    }

                    return Inflate(deflateStart, offset + total - 8, offset + total, gzip: true) is long produced ? (total, produced) : null;
                }

                deflateStart = offset + 2;
                if (DeflateSkipper.FindEnd(window, deflateStart) is not long end || end + 4 > build.End)
                {
                    return null;
                }

                return Inflate(deflateStart, end, end + 4, gzip: false) is long p ? (end + 4 - offset, p) : null;
            }

            private long? GzipHeaderLength(long offset)
            {
                byte[] h = new byte[10];
                if (!build._snapshot.Read(offset, h).IsComplete)
                {
                    return null;
                }

                int flags = h[3];
                long at = offset + 10;
                byte[] two = new byte[2];
                if ((flags & 0x04) != 0)
                {
                    if (!build._snapshot.Read(at, two).IsComplete)
                    {
                        return null;
                    }

                    at += 2 + (two[0] | (two[1] << 8));
                }

                foreach (int bit in new[] { 0x08, 0x10 })
                {
                    if ((flags & bit) != 0)
                    {
                        byte[] one = new byte[1];
                        int guard = 0;
                        do
                        {
                            if (!build._snapshot.Read(at++, one).IsComplete || ++guard > 65536)
                            {
                                return null;
                            }
                        }
                        while (one[0] != 0);
                    }
                }

                if ((flags & 0x02) != 0)
                {
                    at += 2;
                }

                return at - offset;
            }

            /// <summary>[from, deflateEnd) の DEFLATE を展開し、[deflateEnd, trailerEnd) のチェック値と比べる。合えば展開後の長さ。</summary>
            private long? Inflate(long from, long deflateEnd, long trailerEnd, bool gzip)
            {
                try
                {
                    using var source = new RangeStream(build._snapshot, from, deflateEnd);
                    using var inflate = new DeflateStream(source, CompressionMode.Decompress);
                    byte[] buffer = new byte[64 * 1024];
                    var adler = new Adler32();
                    uint crc = PatchCrc32.Initial;
                    long produced = 0;
                    int n;
                    while ((n = inflate.Read(buffer)) > 0)
                    {
                        build._token.ThrowIfCancellationRequested();
                        if (gzip)
                        {
                            crc = PatchCrc32.Append(crc, buffer.AsSpan(0, n));
                        }
                        else
                        {
                            adler.Append(buffer.AsSpan(0, n));
                        }

                        produced += n;
                    }

                    byte[] trailer = new byte[trailerEnd - deflateEnd];
                    if (!build._snapshot.Read(deflateEnd, trailer).IsComplete)
                    {
                        return null;
                    }

                    bool ok = gzip
                        ? BinaryPrimitives.ReadUInt32LittleEndian(trailer) == PatchCrc32.Finish(crc) && BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4)) == (uint)produced
                        : BinaryPrimitives.ReadUInt32BigEndian(trailer) == adler.Value;
                    return ok ? produced : null;
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }

            // ---- (d) 繰り返しのレコード ----

            /// <summary>
            /// 分類が「バイナリ」で 16 KiB 以上の領域 (長い順に最大 64 個) の先頭 256 KiB で自己相関 (ANA-15 の 3) を求め、最も割合の高いずらし
            /// k (8〜4,096) の割合が 20% 以上で、ずらし 1〜(k − 1) の平均の 2 倍以上なら「繰り返しのレコード (k バイト)」にする。
            /// </summary>
            private void DetectRecords(List<FileMapRegion> content)
            {
                var targets = Enumerable.Range(0, content.Count)
                    .Where(i => content[i].Kind == FileMapKind.Binary && content[i].Length >= MinRecordRegion)
                    .OrderByDescending(i => content[i].Length).Take(MaxRecordRegions).ToList();
                foreach (int i in targets)
                {
                    build._token.ThrowIfCancellationRequested();
                    FileMapRegion r = content[i];
                    if (PatternStatistics.AutocorrelationRatios(build._snapshot, r.Offset, (int)Math.Min(RecordWindow, r.Length), build._token) is not { } ratios)
                    {
                        continue;
                    }

                    int best = -1;
                    for (int k = 8; k < ratios.Length; k++)
                    {
                        if (!double.IsNaN(ratios[k]) && (best < 0 || ratios[k] > ratios[best]))
                        {
                            best = k;
                        }
                    }

                    if (best < 0)
                    {
                        continue;
                    }

                    // 割合がほぼ同じ (差が 0.5 ポイント以内) なら、その約数のずらしを選ぶ (ANA-15 の 3。64 と 128 なら 64)。
                    for (int d = 8; d < best; d++)
                    {
                        if (best % d == 0 && ratios[d] >= ratios[best] - 0.005)
                        {
                            best = d;
                            break;
                        }
                    }

                    double share = ratios[best];
                    double mean = 0;
                    int count = 0;
                    for (int k = 1; k < best; k++)
                    {
                        if (!double.IsNaN(ratios[k]))
                        {
                            mean += ratios[k];
                            count++;
                        }
                    }

                    mean = count > 0 ? mean / count : 0;
                    if (share < 0.2 || share < 2 * mean)
                    {
                        continue;
                    }

                    Details.Add(new FileMapDetail
                    {
                        Label = new FileMapMessage("FileMap_Label_Records", (long)best),
                        Evidence = [new FileMapMessage("FileMap_Ev_Records", (long)best, new FileMapPercent(share), (long)(best - 1), new FileMapPercent(mean))],
                    });
                    content[i] = r with
                    {
                        Kind = FileMapKind.Records,
                        Confidence = share >= 0.5 ? Confidence.High : Confidence.Medium,
                        Stage = FileMapStage.Structure,
                        Detail = Details.Count - 1,
                        Extra = best,
                    };
                }
            }

            // ---- 内容のレーンの境目 ----

            /// <summary>
            /// ゼロ・定数の領域 (ブロック単位) の境目を、同じ値のバイトの 512 バイト以上の連続 (走査の (d)) の境目にそろえる (ブロック 1 つの
            /// 範囲まで)。隣の領域はその分だけ縮める・伸ばす。
            /// </summary>
            private void RefineConstantEdges(List<FileMapRegion> content)
            {
                IReadOnlyList<ConstantRun> runs = build._runs.Runs;
                int bs = build._blockSize;
                for (int i = 0; i < content.Count; i++)
                {
                    FileMapRegion r = content[i];
                    if (r.Kind is not (FileMapKind.Zero or FileMapKind.Constant) || r.Stage == FileMapStage.Sample)
                    {
                        continue;
                    }

                    int first = build._runs.FirstEndingAfter(r.Offset);
                    ConstantRun? head = null;
                    ConstantRun? tail = null;
                    for (int k = first; k < runs.Count && runs[k].Start < r.End; k++)
                    {
                        if (r.Kind == FileMapKind.Zero && runs[k].Value != 0)
                        {
                            continue;
                        }

                        if (head is null && runs[k].Start < r.Offset + bs)
                        {
                            head = runs[k];
                        }

                        if (runs[k].End > r.End - bs)
                        {
                            tail = runs[k];
                        }
                    }

                    byte value = head?.Value ?? tail?.Value ?? (byte)0;
                    long start = r.Offset;
                    long end = r.End;
                    if (head is { } h && (r.Kind == FileMapKind.Zero || h.Value == value))
                    {
                        start = Math.Clamp(h.Start, r.Offset - bs, r.Offset + bs);
                    }

                    if (tail is { } t && t.Value == value)
                    {
                        end = Math.Clamp(t.End, r.End - bs, r.End + bs);
                    }

                    if (i > 0)
                    {
                        start = Math.Max(start, content[i - 1].Offset + 1);
                    }

                    if (i + 1 < content.Count)
                    {
                        end = Math.Min(end, content[i + 1].End - 1);
                    }

                    if (end <= start)
                    {
                        continue;
                    }

                    if (i > 0 && start != r.Offset)
                    {
                        FileMapRegion p = content[i - 1];
                        content[i - 1] = p with { Length = start - p.Offset };
                    }

                    if (i + 1 < content.Count && end != r.End)
                    {
                        FileMapRegion n = content[i + 1];
                        content[i + 1] = n with { Offset = end, Length = n.End - end };
                    }

                    content[i] = r with { Offset = start, Length = end - start, Extra = head is null && tail is null ? (r.Kind == FileMapKind.Zero ? 0 : r.Extra) : value };
                }
            }

            /// <summary>テキストの領域の文字コードと、定数の領域の値 (連続から分からなかったもの) を、領域の先頭を少し読んで決める。</summary>
            private void LabelTextAndConstants(List<FileMapRegion> content)
            {
                int reads = 0;
                byte[] buffer = new byte[4096];
                for (int i = 0; i < content.Count; i++)
                {
                    FileMapRegion r = content[i];
                    if (r.Kind == FileMapKind.Text)
                    {
                        int encoding = -1;
                        if (reads++ < 100_000)
                        {
                            int n = (int)Math.Min(buffer.Length, r.Length);
                            ReadResult read = build._snapshot.Read(r.Offset, buffer.AsSpan(0, n));
                            encoding = (int)TextEncodingOf(buffer.AsSpan(0, read.BytesReturned));
                        }

                        content[i] = r with { Extra = encoding };
                    }
                    else if (r.Kind == FileMapKind.Constant && r.Stage != FileMapStage.Sample && r.Extra is < 0 or > 255 && reads++ < 100_000)
                    {
                        byte[] one = new byte[1];
                        build._snapshot.Read(r.Offset + (r.Length / 2), one);
                        content[i] = r with { Extra = one[0] };
                    }
                    else if (r.Kind == FileMapKind.Zero)
                    {
                        content[i] = r with { Extra = 0 };
                    }
                }
            }

            private static FileMapTextEncoding TextEncodingOf(ReadOnlySpan<byte> data)
            {
                int evenZero = 0;
                int oddZero = 0;
                bool high = false;
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] == 0)
                    {
                        if (i % 2 == 0)
                        {
                            evenZero++;
                        }
                        else
                        {
                            oddZero++;
                        }
                    }

                    high |= data[i] >= 0x80;
                }

                int half = Math.Max(1, data.Length / 2);
                if (oddZero * 4 > half && oddZero > evenZero * 4)
                {
                    return FileMapTextEncoding.Utf16LE;
                }

                if (evenZero * 4 > half && evenZero > oddZero * 4)
                {
                    return FileMapTextEncoding.Utf16BE;
                }

                if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
                {
                    return FileMapTextEncoding.Utf16LE;
                }

                if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
                {
                    return FileMapTextEncoding.Utf16BE;
                }

                return high ? FileMapTextEncoding.Utf8 : FileMapTextEncoding.Ascii;
            }

            /// <summary>内容のレーンに領域を重ねる (重なった部分を置き換え、はみ出した部分は元の種類のまま残す)。</summary>
            private static void Paint(List<FileMapRegion> content, FileMapRegion region)
            {
                int i = ContentIndexAt(content, region.Offset);
                if (i < 0)
                {
                    return;
                }

                int j = i;
                while (j < content.Count && content[j].Offset < region.End)
                {
                    j++;
                }

                var replacement = new List<FileMapRegion>(3);
                FileMapRegion first = content[i];
                FileMapRegion last = content[j - 1];
                if (first.Offset < region.Offset)
                {
                    replacement.Add(first with { Length = region.Offset - first.Offset });
                }

                replacement.Add(region);
                if (last.End > region.End)
                {
                    replacement.Add(last with { Offset = region.End, Length = last.End - region.End });
                }

                content.RemoveRange(i, j - i);
                content.InsertRange(i, replacement);
            }

            private static int ContentIndexAt(List<FileMapRegion> content, long offset)
            {
                int lo = 0;
                int hi = content.Count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (offset < content[mid].Offset)
                    {
                        hi = mid - 1;
                    }
                    else if (offset >= content[mid].End)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        return mid;
                    }
                }

                return -1;
            }
        }
    }

    /// <summary>スナップショットの範囲 [from, to) を順に読む Stream (展開の確認)。</summary>
    private sealed class RangeStream(Engine.DocumentSnapshot snapshot, long start, long to) : Stream
    {
        private readonly long _from = start;
        private long _position = start;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => to - _from;

        public override long Position { get => _position - _from; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = (int)Math.Min(buffer.Length, to - _position);
            if (n <= 0)
            {
                return 0;
            }

            ReadResult r = snapshot.Read(_position, buffer[..n]);
            if (!r.IsComplete)
            {
                throw new InvalidDataException("読み込めない範囲です。");
            }

            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
