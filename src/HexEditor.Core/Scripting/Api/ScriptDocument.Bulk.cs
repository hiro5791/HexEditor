using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Sdk;
using CoreResults = HexEditor.Core.Search.SearchResults;
using OverflowMode = HexEditor.Core.Editing.Transforms.OverflowMode;
using SdkRange = HexEditor.Sdk.ByteRange;
using SdkResults = HexEditor.Sdk.SearchResults;

namespace HexEditor.Core.Scripting.Api;

/// <summary>
/// 一括処理 API (AUTO-09)。データ量に比例する処理はメニューと同じ C# の実装 (データ演算・検索・置換・統計) で行い、
/// スクリプトのループに頼らない。すべてチャンク単位で処理し、メモリ使用量はデータ量に比例しない。
/// </summary>
public sealed partial class ScriptDocument
{
    /// <summary>findAll の既定の件数の上限 (仕様 1)。</summary>
    public const long DefaultMaxResults = 1_000_000;

    // ---- transform (03 のデータ演算) ----

    public Task TransformAsync(RangeArg range, string op, TransformOperand? operand = null, TransformOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        SdkRange r = CheckedRange(range);
        DataOperationSpec spec = TransformSpec(op, operand, options ?? new TransformOptions());
        DataOperationError error = DataOperationRunner.Validate([new TargetRange(r.Start, r.Length)], spec);
        if (error != DataOperationError.None)
        {
            throw new Sdk.ArgumentError(Msg.T($"データ演算の指定が正しくありません: {error}", $"Invalid data operation: {error}"));
        }

        if (r.Length == 0)
        {
            return;
        }

        WriteSession session = _ctx.AcquireWrite(this);
        DataOperationResult result = await RunBulkAsync("transform", r.Length, options, o =>
            DataOperationRunner.For(Document).Run(Snapshot, [new TargetRange(r.Start, r.Length)], spec, o)).ConfigureAwait(false);
        if (!result.Stats.Changed)
        {
            foreach (RangeReplacement part in result.Replacements)
            {
                (part.Content as IDisposable)?.Dispose();
            }

            return;
        }

        _ctx.Dispatcher.Invoke(() => session.Apply(d =>
        {
            TransformApplier.Apply(d, result.Replacements, _ctx.Environment.Localize(ScriptTexts.UndoName, _ctx.Name));
            return true;
        }));
    });

    private static DataOperationSpec TransformSpec(string op, TransformOperand? operand, TransformOptions o)
    {
        if (o.Unit is not (1 or 2 or 4 or 8))
        {
            throw new Sdk.ArgumentError(Msg.T($"unit は 1・2・4・8 です: {o.Unit}", $"unit must be 1, 2, 4 or 8: {o.Unit}"));
        }

        bool big = o.Endian switch
        {
            null or "le" => false,
            "be" => true,
            _ => throw new Sdk.ArgumentError(Msg.T($"エンディアンは \"le\" か \"be\" です: {o.Endian}", $"endian must be \"le\" or \"be\": {o.Endian}")),
        };
        OverflowMode overflow = o.Overflow switch
        {
            null or "wrap" => OverflowMode.Wrap,
            "saturate" => OverflowMode.Saturate,
            _ => throw new Sdk.ArgumentError(Msg.T($"overflow は \"wrap\" か \"saturate\" です: {o.Overflow}", $"overflow must be \"wrap\" or \"saturate\": {o.Overflow}")),
        };
        var spec = new DataOperationSpec { Size = o.Unit, BigEndian = big, Overflow = overflow, Type = ElementType.Unsigned };
        DataOperationKind kind = op switch
        {
            "xor" => DataOperationKind.Xor,
            "and" => DataOperationKind.And,
            "or" => DataOperationKind.Or,
            "not" => DataOperationKind.Not,
            "add" => DataOperationKind.Add,
            "sub" => DataOperationKind.Subtract,
            "mul" => DataOperationKind.Multiply,
            "div" => DataOperationKind.Divide,
            "shl" => DataOperationKind.ShiftLeft,
            "shr" => DataOperationKind.ShiftRightLogical,
            "rol" => DataOperationKind.RotateLeft,
            "ror" => DataOperationKind.RotateRight,
            "neg" => DataOperationKind.Negate,
            "swap" => o.Unit switch
            {
                2 => DataOperationKind.ByteSwap16,
                4 => DataOperationKind.ByteSwap32,
                8 => DataOperationKind.ByteSwap64,
                _ => throw new Sdk.ArgumentError(Msg.T("swap には unit (2・4・8) を指定してください。", "Specify a unit (2, 4 or 8) for swap.")),
            },
            "reverse" => DataOperationKind.ReverseRange,
            _ => throw new Sdk.ArgumentError(Msg.T($"演算が不明です: {op} (xor and or not add sub mul div shl shr rol ror neg swap reverse)", $"Unknown operation: {op} (xor and or not add sub mul div shl shr rol ror neg swap reverse)")),
        };
        spec = spec with { Kind = kind };
        if (kind is DataOperationKind.Negate)
        {
            spec = spec with { Type = ElementType.Signed };
        }

        if (kind is DataOperationKind.ByteSwap16 or DataOperationKind.ByteSwap32 or DataOperationKind.ByteSwap64 or DataOperationKind.ReverseRange)
        {
            spec = spec with { Size = 1 };
        }

        bool usesOperand = kind is not (DataOperationKind.Not or DataOperationKind.Negate or DataOperationKind.ByteSwap16
            or DataOperationKind.ByteSwap32 or DataOperationKind.ByteSwap64 or DataOperationKind.ReverseRange);
        if (!usesOperand)
        {
            return spec;
        }

        if (operand is not TransformOperand value)
        {
            throw new Sdk.ArgumentError(Msg.T($"{op} にはオペランドを指定してください。", $"Specify an operand for {op}."));
        }

        if (value.HexKey is string key)
        {
            if (kind is not (DataOperationKind.Xor or DataOperationKind.And or DataOperationKind.Or))
            {
                throw new Sdk.ArgumentError(Msg.T("Hex のキーを使えるのは xor・and・or だけです。", "A hex key can be used only with xor, and, or."));
            }

            return spec with { OperandSource = OperandSource.KeyBytes, Key = ScriptValues.ParseHex(key), Size = 1 };
        }

        BigInteger n = value.Value ?? BigInteger.Zero;
        if (kind is DataOperationKind.ShiftLeft or DataOperationKind.ShiftRightLogical or DataOperationKind.RotateLeft or DataOperationKind.RotateRight)
        {
            return spec with { ShiftBits = (int)n };
        }

        return spec with { Operand = (Int128)n };
    }

    // ---- 検索 (04) ----

    private SearchPattern Pattern(BytePattern pattern, FindOptions o)
    {
        SearchPattern p;
        if (pattern.Bytes is byte[] bytes)
        {
            p = SearchPattern.Literal(bytes);
        }
        else
        {
            string text = pattern.Text ?? "";
            Encoding Enc() => o.Encoding is EncodingArg e ? ScriptText.Resolve(e) : Ui(() => ScriptText.Resolve(Host.Editor.TextEncoding.CodePage));
            p = (o.Type ?? "hex") switch
            {
                "hex" => SearchPattern.FromHex(text),
                "text" => SearchPattern.FromText(text, Enc(), new TextSearchOptions { CaseSensitive = o.CaseSensitive }),
                "regex" => RegexSearch.Text(text, Enc(), new RegexSearchOptions { IgnoreCase = !o.CaseSensitive }),
                "int" => NumericSearch.Integer(text, new IntegerSearchOptions
                {
                    Bits = o.Size * 8,
                    Endian = o.Endian == "be" ? SearchEndian.Big : SearchEndian.Little,
                }, Ui(ExpressionContext)),
                "float" => NumericSearch.Float(text, new FloatSearchOptions
                {
                    Format = o.Size == 8 ? FloatFormat.Double : FloatFormat.Single,
                    Endian = o.Endian == "be" ? SearchEndian.Big : SearchEndian.Little,
                }),
                _ => throw new Sdk.ArgumentError(Msg.T($"検索の種類が不明です: {o.Type} (hex text regex int float)", $"Unknown search type: {o.Type} (hex text regex int float)")),
            };
        }

        if (o.Alignment > 1)
        {
            p = p.WithPosition(PositionCondition.Create(o.Alignment, 0));
        }

        return p;
    }

    private SearchOptions SearchOptionsFor(FindOptions o, long max)
    {
        var options = new SearchOptions { MaxMatches = max };
        if (o.Range is RangeArg range)
        {
            SdkRange r = CheckedRange(range);
            options = options with { Scope = SearchScope.Of(r.Start, r.Length) };
        }

        return options;
    }

    private long ScopeBytes(FindOptions o) => o.Range is RangeArg range ? CheckedRange(range).Length : Snapshot.Length;

    public Task<SdkResults> FindAllAsync(BytePattern pattern, FindOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        FindOptions o = options ?? new FindOptions();
        if (o.Max <= 0)
        {
            throw new Sdk.ArgumentError(Msg.T("max は 1 以上にしてください。", "max must be 1 or greater."));
        }

        SearchPattern p = Pattern(pattern, o);
        SearchOptions so = SearchOptionsFor(o, o.Max);
        DocumentSnapshot snapshot = Snapshot;
        return RunBulkAsync("findAll", ScopeBytes(o), o, op =>
        {
            using CoreResults results = SearchEngine.FindAll(snapshot, p, so, op, op.CancellationToken);
            return ToSdkResults(results, o.Max);
        });
    });

    internal static SdkResults ToSdkResults(CoreResults results, long max)
    {
        long count = Math.Min(results.LongCount, max);
        var list = new List<SdkRange>((int)Math.Min(count, 1_000_000));
        const int Batch = 65536;
        for (long i = 0; i < count; i += Batch)
        {
            foreach (SearchMatch m in results.GetRange(i, (int)Math.Min(Batch, count - i)))
            {
                list.Add(new SdkRange(m.Offset, m.Length));
            }
        }

        return new SdkResults(list, results.LimitReached || results.LongCount > max);
    }

    public Task<SdkRange?> FindAsync(BytePattern pattern, FindOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        FindOptions o = options ?? new FindOptions();
        SearchPattern p = Pattern(pattern, o);
        SearchOptions so = SearchOptionsFor(o, long.MaxValue);
        long from = o.From is OffsetArg f ? ResolveOffset(f) : (o.Backward ? Snapshot.Length : 0);
        DocumentSnapshot snapshot = Snapshot;
        return RunBulkAsync("find", ScopeBytes(o), o, op =>
        {
            SearchHit? hit = SearchEngine.Find(snapshot, p, from, forward: !o.Backward, wrap: false, so, op, op.CancellationToken);
            return hit is { } h ? new SdkRange(h.Offset, h.Length) : (SdkRange?)null;
        });
    });

    public Task<long> CountAsync(BytePattern pattern, FindOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        FindOptions o = options ?? new FindOptions();
        SearchPattern p = Pattern(pattern, o);
        SearchOptions so = SearchOptionsFor(o, long.MaxValue);
        DocumentSnapshot snapshot = Snapshot;
        return RunBulkAsync("count", ScopeBytes(o), o, op =>
        {
            using CoreResults results = SearchEngine.FindAll(snapshot, p, so, op, op.CancellationToken);
            return results.LongCount;
        });
    });

    public Task<long> ReplaceAllAsync(BytePattern pattern, BytePattern replacement, FindOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        FindOptions o = options ?? new FindOptions();
        SearchPattern p = Pattern(pattern, o);
        SearchOptions so = SearchOptionsFor(o, long.MaxValue);
        ReplacementTemplate template = replacement.Bytes is byte[] rb
            ? ReplacementTemplate.FromBytes(rb)
            : o.Type is "text" or "regex"
                ? ReplacementTemplate.FromText(replacement.Text ?? "", o.Encoding is EncodingArg e ? ScriptText.Resolve(e) : Ui(() => ScriptText.Resolve(Host.Editor.TextEncoding.CodePage)), false)
                : ReplacementTemplate.FromHex(replacement.Text ?? "");
        bool canResize = Ui(() => Document.CanResize);
        var replaceOptions = new ReplaceOptions { Policy = ReplaceOptions.DefaultPolicy(canResize) };
        WriteSession session = _ctx.AcquireWrite(this);
        DocumentSnapshot snapshot = Snapshot;
        PreparedReplacement? prepared = await RunBulkAsync("replaceAll", ScopeBytes(o), o, op =>
        {
            using CoreResults found = Replacer.FindForReplaceAll(snapshot, p, so, op, op.CancellationToken);
            if (found.LongCount == 0)
            {
                return null;
            }

            return Document.PrepareReplacements(Replacer.PlanAll(snapshot, found, template, replaceOptions, canResize), op, ignoreEditLock: true,
                op.CancellationToken);
        }).ConfigureAwait(false);
        if (prepared is null || prepared.Count == 0)
        {
            return 0L;
        }

        _ctx.Dispatcher.Invoke(() => session.Apply(d =>
        {
            d.CommitReplacements(prepared, _ctx.Environment.Localize(ScriptTexts.UndoName, _ctx.Name), ignoreEditLock: true);
            return true;
        }));
        return prepared.Count;
    });

    // ---- コピー・書き出し ----

    public Task CopyRangeAsync(RangeArg source, IDocument destination, OffsetArg destinationOffset, CopyRangeOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        if (destination is not ScriptDocument dest)
        {
            throw new Sdk.ArgumentError(Msg.T("destination にはドキュメントを指定してください。", "Specify a document for destination."));
        }

        dest.ThrowIfClosed();
        SdkRange r = CheckedRange(source);
        long at = dest.ResolveOffset(destinationOffset);
        dest.CheckOffset(at, allowEnd: true);
        bool insert = options?.Insert ?? false;
        if (insert)
        {
            dest.RequireResizable();
        }

        DocumentSnapshot snapshot = Snapshot;
        await Task.Yield();
        _ctx.ThrowIfStopped();

        // データエンジンの参照で行い、実データを複製しない (仕様 1)。
        dest.Context.Edit(dest, d =>
        {
            if (r.Length == 0)
            {
                return;
            }

            if (insert)
            {
                d.InsertFrom(at, snapshot, r.Start, r.Length, _ctx.Environment.Localize(ScriptTexts.UndoName, _ctx.Name));
            }
            else
            {
                if (at + r.Length > d.Length && !d.CanResize)
                {
                    throw new Sdk.RangeError(Msg.T("長さを変えられないドキュメントの末尾を越えてコピーできません。", "Cannot copy past the end of a document whose length cannot be changed."));
                }

                d.OverwriteFrom(at, snapshot, r.Start, r.Length, _ctx.Environment.Localize(ScriptTexts.UndoName, _ctx.Name));
            }
        });
    });

    public Task ExportRangeAsync(RangeArg range, string path, ExportRangeOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        string full = System.IO.Path.GetFullPath(path);
        _ctx.RequireFileWrite(full);
        SdkRange r = CheckedRange(range);
        if (File.Exists(full) && !(options?.Overwrite ?? false))
        {
            throw new IoError(Msg.T($"ファイルがあります: {full} (上書きするには overwrite: true)", $"The file already exists: {full} (use overwrite: true to replace it)"));
        }

        DocumentSnapshot snapshot = Snapshot;
        await RunBulkAsync("exportRange", r.Length, options, op =>
        {
            string temp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    for (long done = 0; done < r.Length;)
                    {
                        int n = (int)Math.Min(buffer.Length, r.Length - done);
                        ReadExact(snapshot, r.Start + done, buffer.AsSpan(0, n));
                        stream.Write(buffer, 0, n);
                        done += n;
                        op.Report(done);
                    }
                }

                File.Move(temp, full, overwrite: true);
                return true;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }).ConfigureAwait(false);
    });

    // ---- チャンク ----

    public IAsyncEnumerable<DataChunk> Chunks(RangeArg? range = null, long chunkSize = 1024 * 1024)
    {
        ThrowIfClosed();
        if (chunkSize <= 0)
        {
            throw new Sdk.ArgumentError(Msg.T("chunkSize は 1 以上にしてください。", "chunkSize must be 1 or greater."));
        }

        if (chunkSize > SyncLimit)
        {
            throw new LimitError(Msg.T("chunkSize は最大 16 MB です。", "chunkSize can be at most 16 MB.")) { Limit = "size" };
        }

        SdkRange r = CheckedRange(range);
        return ReadChunks(r, (int)chunkSize, _ctx.Token);
    }

    private async IAsyncEnumerable<DataChunk> ReadChunks(SdkRange r, int chunkSize, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (long offset = r.Start; offset < r.End;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfClosed();
            DocumentSnapshot snapshot = Snapshot;
            long end = Math.Min(r.End, snapshot.Length);
            if (offset >= end)
            {
                yield break;
            }

            int n = (int)Math.Min(chunkSize, end - offset);
            long at = offset;
            byte[] data = await Task.Run(() =>
            {
                byte[] buffer = new byte[n];
                ReadExact(snapshot, at, buffer);
                return buffer;
            }, cancellationToken).ConfigureAwait(false);
            yield return new DataChunk(offset, data);
            offset += n;
        }
    }

    public Task TransformChunksAsync(RangeArg range, Func<byte[], long, byte[]> transform, AsyncOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        SdkRange r = CheckedRange(range);
        WriteSession session = _ctx.AcquireWrite(this);
        using CancellationTokenSource cts = _ctx.LinkedToken(options?.Signal);
        const int ChunkSize = 1024 * 1024;
        for (long offset = r.Start; offset < r.End;)
        {
            cts.Token.ThrowIfCancellationRequested();
            int n = (int)Math.Min(ChunkSize, r.End - offset);
            DocumentSnapshot snapshot = Snapshot;
            long at = offset;
            byte[] data = await Task.Run(() =>
            {
                byte[] buffer = new byte[n];
                ReadExact(snapshot, at, buffer);
                return buffer;
            }, cts.Token).ConfigureAwait(false);
            byte[] result = _ctx.Thread.Invoke(() => transform(data, at));
            if (result.Length != data.Length)
            {
                // それより前のチャンクの変更は残り、Undo できる (AUTO-09 の「エラー」)。
                throw new Sdk.ArgumentError(Msg.T($"transformChunks の関数が長さの違うバイト列を返しました (オフセット 0x{at:X}: {data.Length} バイトに対して {result.Length} バイト)。", $"The transformChunks function returned a byte array of a different length (offset 0x{at:X}: {result.Length} bytes for {data.Length} bytes)."));
            }

            _ctx.Dispatcher.Invoke(() => session.Apply(d =>
            {
                d.Overwrite(at, result, _ctx.Environment.Localize(ScriptTexts.UndoName, _ctx.Name));
                return true;
            }));
            offset += n;
            options?.Progress?.Invoke((double)(offset - r.Start) / Math.Max(1, r.Length), null);
        }
    });

    // ---- 統計 (06) ----

    public Task<long[]> HistogramAsync(RangeArg? range = null, AsyncOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        SdkRange r = CheckedRange(range);
        DocumentSnapshot snapshot = Snapshot;
        return RunBulkAsync("histogram", r.Length, options, op => Histogram(snapshot, r, op));
    });

    private static long[] Histogram(DocumentSnapshot snapshot, SdkRange r, LongRunningOperation op)
    {
        long[] counts = new long[256];
        byte[] buffer = new byte[1024 * 1024];
        for (long done = 0; done < r.Length;)
        {
            int n = (int)Math.Min(buffer.Length, r.Length - done);
            ReadExact(snapshot, r.Start + done, buffer.AsSpan(0, n));
            foreach (byte b in buffer.AsSpan(0, n))
            {
                counts[b]++;
            }

            done += n;
            op.Report(done);
        }

        return counts;
    }

    public Task<object> EntropyAsync(RangeArg? range = null, EntropyOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        SdkRange r = CheckedRange(range);
        long block = options?.BlockSize ?? 0;
        if (block < 0)
        {
            throw new Sdk.ArgumentError(Msg.T("blockSize は 0 以上にしてください。", "blockSize must be 0 or greater."));
        }

        if (block > 0 && r.Length / block > 10_000_000)
        {
            throw new LimitError(Msg.T("ブロックの数が多すぎます (最大 10,000,000)。blockSize を大きくしてください。", "Too many blocks (at most 10,000,000). Increase blockSize.")) { Limit = "count" };
        }

        DocumentSnapshot snapshot = Snapshot;
        return RunBulkAsync("entropy", r.Length, options, op =>
        {
            if (block == 0)
            {
                return (object)Entropy(Histogram(snapshot, r, op), r.Length);
            }

            var values = new List<double>();
            for (long start = r.Start; start < r.End; start += block)
            {
                long len = Math.Min(block, r.End - start);
                values.Add(Entropy(Histogram(snapshot, new SdkRange(start, len), op), len));
            }

            return values.ToArray();
        });
    });

    private static double Entropy(long[] counts, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        double h = 0;
        foreach (long c in counts)
        {
            if (c > 0)
            {
                double p = (double)c / total;
                h -= p * Math.Log2(p);
            }
        }

        return h;
    }

    // ---- 比較 (06) ----

    public Task<IReadOnlyList<SdkRange>> CompareAsync(IDocument other, Sdk.CompareOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        if (other is not ScriptDocument o)
        {
            throw new Sdk.ArgumentError(Msg.T("other にはドキュメントを指定してください。", "Specify a document for other."));
        }

        o.ThrowIfClosed();
        long max = options?.Max ?? 1_000_000;
        DocumentSnapshot a = Snapshot;
        DocumentSnapshot b = o.Snapshot;
        return RunBulkAsync("compare", Math.Max(a.Length, b.Length), options, op => (IReadOnlyList<SdkRange>)Compare(a, b, max, op));
    });

    private static List<SdkRange> Compare(DocumentSnapshot a, DocumentSnapshot b, long max, LongRunningOperation op)
    {
        var diffs = new List<SdkRange>();
        long common = Math.Min(a.Length, b.Length);
        byte[] x = new byte[1024 * 1024];
        byte[] y = new byte[x.Length];
        long diffStart = -1;
        for (long done = 0; done < common && diffs.Count < max;)
        {
            int n = (int)Math.Min(x.Length, common - done);
            ReadExact(a, done, x.AsSpan(0, n));
            ReadExact(b, done, y.AsSpan(0, n));
            int i = 0;
            while (i < n && diffs.Count < max)
            {
                if (diffStart < 0)
                {
                    int mismatch = x.AsSpan(i, n - i).CommonPrefixLength(y.AsSpan(i, n - i));
                    i += mismatch;
                    if (i < n)
                    {
                        diffStart = done + i;
                    }
                }
                else
                {
                    while (i < n && x[i] != y[i])
                    {
                        i++;
                    }

                    if (i < n)
                    {
                        diffs.Add(new SdkRange(diffStart, done + i - diffStart));
                        diffStart = -1;
                    }
                }
            }

            done += n;
            op.Report(done);
        }

        if (diffs.Count < max)
        {
            long end = Math.Max(a.Length, b.Length);
            if (diffStart >= 0)
            {
                diffs.Add(new SdkRange(diffStart, end - diffStart));
            }
            else if (end > common)
            {
                diffs.Add(new SdkRange(common, end - common));
            }
        }

        return diffs;
    }

    // ---- 文字列の抽出 (04) ----

    public Task<IReadOnlyList<FoundString>> StringsAsync(StringsOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        StringsOptions o = options ?? new StringsOptions();
        if (o.MinLength is < 1 or > StringExtractionOptions.MaxMinLength)
        {
            throw new Sdk.ArgumentError(Msg.T($"minLength は 1〜{StringExtractionOptions.MaxMinLength} です。", $"minLength must be 1 to {StringExtractionOptions.MaxMinLength}."));
        }

        IReadOnlyList<EncodingArg> encs = o.Encodings ?? ["ascii", "utf-16le"];
        var encodings = encs.Select(e => (e.ToString(), ScriptText.Resolve(e))).ToList();
        var extraction = new StringExtractionOptions { MinLength = o.MinLength, Encodings = encodings };
        var so = new SearchOptions { MaxMatches = o.Max };
        SdkRange scope = CheckedRange(o.Range);
        so = so with { Scope = SearchScope.Of(scope.Start, scope.Length) };
        DocumentSnapshot snapshot = Snapshot;
        return RunBulkAsync("strings", scope.Length, o, op =>
        {
            using CoreResults results = StringExtractor.CreateResults(snapshot, extraction, so);
            SearchEngine.FindAll(results, op, op.CancellationToken);
            var list = new List<FoundString>();
            long count = Math.Min(results.LongCount, o.Max);
            for (long i = 0; i < count; i++)
            {
                SearchMatch m = results[i];
                (string name, Encoding enc) = encodings[Math.Clamp(m.Variant, 0, encodings.Count - 1)];
                byte[] bytes = new byte[(int)Math.Min(m.Length, StringExtractionOptions.DefaultMaxLength * 4)];
                ReadExact(snapshot, m.Offset, bytes);
                list.Add(new FoundString(m.Offset, m.Length, StringExtractor.Text(bytes, enc, StringExtractionOptions.DefaultMaxLength), name));
            }

            return (IReadOnlyList<FoundString>)list;
        });
    });
}
