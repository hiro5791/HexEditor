using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Sdk;
using CoreHashInfo = HexEditor.Core.Hashing.HashAlgorithmInfo;
using SdkHashInfo = HexEditor.Sdk.HashAlgorithmInfo;
using SdkRange = HexEditor.Sdk.ByteRange;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>hash</c> (AUTO-14)。ハッシュパネル (06) と同じ計算。複数のアルゴリズムはデータを 1 回だけ読んで同時に計算する。</summary>
internal sealed class ScriptHash(ScriptContext ctx) : IHash
{
    public IReadOnlyList<SdkHashInfo> Algorithms =>
        [.. HashCatalog.All.Where(a => a.IsAvailable).Select(a => new SdkHashInfo(a.Id, a.Name, a.Bits))];

    private static (string Id, CoreHashInfo Info) Resolve(HashAlgorithmSpec spec)
    {
        if (spec.Crc is CustomCrc crc)
        {
            var def = new CustomCrcDefinition("script", crc.Width, crc.Poly, crc.Init, crc.RefIn, crc.RefOut, crc.XorOut);
            IReadOnlyList<CustomCrcError> errors = def.ValidateParameters();
            if (errors.Count > 0)
            {
                throw new Sdk.ArgumentError($"カスタム CRC のパラメータが正しくありません: {string.Join(", ", errors.Select(e => $"{e.Field} {e.Code}"))}");
            }

            return ("crc", def.ToAlgorithm());
        }

        string id = spec.Id ?? throw new Sdk.ArgumentError("アルゴリズムを指定してください。");
        CoreHashInfo info = HashCatalog.Find(id) is { IsAvailable: true } a
            ? a
            : throw new Sdk.ArgumentError($"ハッシュのアルゴリズムが不明です: {id} (hash.algorithms で一覧できます)");
        return (info.Id, info);
    }

    private static HashResult Result(string id, byte[] value) => new(id, Convert.ToHexStringLower(value), value);

    public Task<IReadOnlyList<HashResult>> ComputeAsync(IDocument document, RangeArg? range, IReadOnlyList<HashAlgorithmSpec> algorithms,
        AsyncOptions? options = null) => ScriptErrorMap.GuardAsync(() =>
    {
        if (document is not ScriptDocument doc)
        {
            throw new Sdk.ArgumentError("document にはドキュメントを指定してください。");
        }

        if (algorithms.Count == 0)
        {
            throw new Sdk.ArgumentError("アルゴリズムを 1 つ以上指定してください。");
        }

        List<(string Id, CoreHashInfo Info)> resolved = [.. algorithms.Select(Resolve)];
        SdkRange r = doc.CheckedRange(range);
        DocumentSnapshot snapshot = doc.Snapshot;
        var request = new HashRequest
        {
            Ranges = [new HashRange(r.Start, r.Length)],
            Algorithms = [.. resolved.Select(x => new HashAlgorithmChoice(x.Info))],
        };
        return doc.RunBulkAsync("hash", r.Length, options, op =>
        {
            HashComputation computation = HashEngine.Compute(snapshot, request, op, op.CancellationToken);
            LastBytesRead = computation.BytesRead;
            return (IReadOnlyList<HashResult>)[.. computation.Rows.Select((row, i) => Result(resolved[i].Id, row.Value))];
        });
    });

    /// <summary>直前の計算で読んだバイト数 (テスト用。AUTO-14 の受け入れ基準 2)。</summary>
    internal long LastBytesRead { get; private set; }

    public HashResult ComputeBytes(BytePattern data, HashAlgorithmSpec algorithm) => ScriptErrorMap.Guard(() =>
    {
        byte[] bytes = ScriptDocument.PatternBytes(data, allowRandom: false);
        if (bytes.Length > ScriptDocument.SyncLimit)
        {
            throw new LimitError("同期 API で扱えるのは 1 回 16 MB までです。hash.compute を使ってください。") { Limit = "size" };
        }

        (string id, CoreHashInfo info) = Resolve(algorithm);
        IHasher hasher = info.CreateHasher();
        hasher.Append(bytes);
        return Result(id, hasher.Finish());
    });

    public Task<IReadOnlyList<HashResult>> ComputeFileAsync(string path, IReadOnlyList<HashAlgorithmSpec> algorithms, AsyncOptions? options = null) =>
        ScriptErrorMap.GuardAsync(async () =>
        {
            string full = Path.GetFullPath(path);
            ctx.RequireFileRead(full);
            List<(string Id, CoreHashInfo Info)> resolved = [.. algorithms.Select(Resolve)];
            using CancellationTokenSource cts = ctx.LinkedToken(options?.Signal);
            return await Task.Run(() =>
            {
                List<IHasher> hashers = [.. resolved.Select(x => x.Info.CreateHasher())];
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                byte[] buffer = new byte[4 * 1024 * 1024];
                long total = stream.Length;
                long done = 0;
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    foreach (IHasher h in hashers)
                    {
                        h.Append(buffer.AsSpan(0, n));
                    }

                    done += n;
                    options?.Progress?.Invoke(total > 0 ? (double)done / total : 1, null);
                }

                return (IReadOnlyList<HashResult>)[.. hashers.Select((h, i) => Result(resolved[i].Id, h.Finish()))];
            }, cts.Token).ConfigureAwait(false);
        });
}
