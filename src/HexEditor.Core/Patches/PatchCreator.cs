using HexEditor.Core.Formats;

namespace HexEditor.Core.Patches;

/// <summary>形式で表せない理由 (EDIT-42 の仕様 4)。</summary>
public enum PatchUnavailableReason
{
    None,

    /// <summary>IPS の範囲 (16 MiB 未満) を超える位置の変更がある。</summary>
    IpsOffsetLimit,

    /// <summary>IPS32 の範囲 (4 GiB 未満) を超える位置の変更がある。</summary>
    Ips32OffsetLimit,

    /// <summary>自己適用型パッチの適用プログラムがこのビルドに同梱されていない。</summary>
    StubMissing,
}

/// <summary>作成前に表示する情報 (EDIT-42 の仕様 6)。</summary>
public sealed record PatchEstimate(PatchFormat Format, PatchUnavailableReason Unavailable, long Records, long EstimatedSize, bool Inefficient)
{
    public bool IsAvailable => Unavailable == PatchUnavailableReason.None;

    /// <summary>差分が変更なし (同じ内容)。</summary>
    public bool IsEmpty { get; init; }
}

/// <summary>作成の設定 (EDIT-42 の「画面」、EDIT-44 の追加設定)。</summary>
public sealed record PatchWriteOptions
{
    /// <summary>独自形式に全体の SHA-256 を入れる (既定オフ。全体を読む)。</summary>
    public bool FullHash { get; init; }

    /// <summary>IPS の RLE を使う (TOOL-12。既定: 使う)。</summary>
    public bool UseRle { get; init; } = true;

    /// <summary>自己適用型パッチの既定の対象ファイル名。</summary>
    public string? DefaultFileName { get; init; }

    /// <summary>自己適用型パッチで表示するメッセージ。</summary>
    public string? Message { get; init; }

    /// <summary>自己適用型パッチの適用プログラム (同梱の実行ファイル)。</summary>
    public string? StubPath { get; init; }

    /// <summary>自己適用型パッチの適用プログラムが表示する文字列 (言語のタグ → キー → 文字列。EDIT-44 の仕様 4)。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ApplierStrings { get; init; }
}

/// <summary>
/// パッチの作成 (EDIT-42)。差分 (<see cref="PatchScript"/>) から、形式ごとの推定 (レコード数・推定サイズ・表せない理由・効率の警告) と書き出しを行う。
/// 位置で比べる差分 (IPS・UPS) は一度求めて使い回す。
/// </summary>
public sealed class PatchCreator(PatchScript script)
{
    /// <summary>挿入・削除を効率よく表せない形式の警告を出す推定サイズの割合 (変換後のデータの 50%。仕様 5)。</summary>
    public const double InefficientRatio = 0.5;

    private List<(long Offset, long Length)>? _positional;
    private List<(long Offset, long Length)>? _upsRuns;
    private List<HexPatchOp>? _ops;

    public PatchScript Script { get; } = script;

    /// <summary>途中の挿入・削除を含むか (変換前からのコピーが同じ位置にない部分がある)。</summary>
    public bool HasInsertOrDelete =>
        Script.SourceLength != Script.TargetLength || Script.Segments.Any(s => s.FromSource && s.SourceOffset != s.TargetOffset);

    /// <summary>位置で比べた差分 (IPS・UPS。挿入・削除の後ろは比べるため、ファイルサイズに比例することがある)。</summary>
    public List<(long Offset, long Length)> Positional(CancellationToken cancellationToken = default) =>
        _positional ??= Script.PositionalChanges(cancellationToken);

    public List<HexPatchOp> Operations() => _ops ??= Script.Operations();

    /// <summary>形式「自動」(TOOL-12 の 3): 16 MiB 以上の位置の変更 (または切り詰め) があれば IPS32、なければ IPS。</summary>
    public PatchFormat AutoIpsFormat(CancellationToken cancellationToken = default) =>
        Estimate(PatchFormat.Ips, cancellationToken).IsAvailable ? PatchFormat.Ips : PatchFormat.Ips32;

    public PatchEstimate Estimate(PatchFormat format, CancellationToken cancellationToken = default, long stubSize = 0)
    {
        switch (format)
        {
            case PatchFormat.Ips or PatchFormat.Ips32:
            {
                List<(long Offset, long Length)> changes = Positional(cancellationToken);
                bool ips32 = format == PatchFormat.Ips32;
                long limit = ips32 ? IpsPatch.Ips32Limit : IpsPatch.IpsLimit;
                long last = changes.Count > 0 ? changes[^1].Offset + changes[^1].Length - 1 : -1;
                long? truncate = Script.TargetLength < Script.SourceLength ? Script.TargetLength : null;
                PatchUnavailableReason reason = last >= limit || truncate >= limit
                    ? ips32 ? PatchUnavailableReason.Ips32OffsetLimit : PatchUnavailableReason.IpsOffsetLimit
                    : PatchUnavailableReason.None;
                (long records, long size) = IpsSize(changes, ips32);
                size += truncate is null ? 0 : ips32 ? 4 : 3;
                return new PatchEstimate(format, reason, records, size, Inefficient(size)) { IsEmpty = changes.Count == 0 && truncate is null };
            }

            case PatchFormat.Ups:
            {
                _upsRuns ??= UpsPatch.Runs(Script, cancellationToken);
                (long records, long size) = UpsPatch.Estimate(Script, _upsRuns);
                return new PatchEstimate(format, PatchUnavailableReason.None, records, size, Inefficient(size)) { IsEmpty = _upsRuns.Count == 0 };
            }

            case PatchFormat.Bps:
            {
                (long records, long size) = BpsPatch.Estimate(Script);
                return new PatchEstimate(format, PatchUnavailableReason.None, records, size, false) { IsEmpty = Operations().Count == 0 };
            }

            default:
            {
                (long records, long size) = HexPatch.Estimate(Script, Operations());
                PatchUnavailableReason reason = format == PatchFormat.SelfApplying && stubSize <= 0 ? PatchUnavailableReason.StubMissing : PatchUnavailableReason.None;
                return new PatchEstimate(format, reason, records, size + (format == PatchFormat.SelfApplying ? stubSize + SelfPatchContainer.TrailerSize : 0), false)
                {
                    IsEmpty = Operations().Count == 0,
                };
            }
        }
    }

    /// <summary>挿入・削除を効率よく表せない (仕様 5): 挿入・削除があり、推定サイズが変換後のデータの 50% を超える。</summary>
    private bool Inefficient(long size) => HasInsertOrDelete && size > Script.TargetLength * InefficientRatio;

    /// <summary>IPS の推定 (RLE は数えない上限の見積もり)。</summary>
    private static (long Records, long Size) IpsSize(List<(long Offset, long Length)> changes, bool ips32)
    {
        int header = ips32 ? 6 : 5;
        long records = 0, size = 5 + (ips32 ? 4 : 3);
        long mergedStart = -1, mergedEnd = -1;
        void Flush()
        {
            if (mergedStart < 0)
            {
                return;
            }

            long length = mergedEnd - mergedStart;
            long n = (length + IpsPatch.MaxRecord - 1) / IpsPatch.MaxRecord;
            records += n;
            size += n * header + length;
        }

        foreach ((long o, long l) in changes)
        {
            if (mergedStart >= 0 && o - mergedEnd <= IpsPatch.MergeGap)
            {
                mergedEnd = o + l;
                continue;
            }

            Flush();
            mergedStart = o;
            mergedEnd = o + l;
        }

        Flush();
        return (records, size);
    }

    /// <summary>
    /// パッチを書く。表せない場合は <see cref="IpsLimitException"/> (IPS・IPS32)。<paramref name="progress"/> には処理したバイト数を知らせる
    /// (UPS・BPS は変換前と変換後の全体を読むため、合計は変換前と変換後の長さの和)。
    /// </summary>
    public void Write(PatchFormat format, Stream output, PatchWriteOptions? options = null, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PatchWriteOptions();
        switch (format)
        {
            case PatchFormat.Ips or PatchFormat.Ips32:
                IpsPatch.Write(output, Positional(cancellationToken), Script.ReadTarget, Script.TargetLength, Script.SourceLength,
                    format == PatchFormat.Ips32 ? IpsFormat.Ips32 : IpsFormat.Ips, options.UseRle);
                break;
            case PatchFormat.Ups:
                UpsPatch.Write(output, Script, _upsRuns ??= UpsPatch.Runs(Script, cancellationToken), progress, cancellationToken);
                break;
            case PatchFormat.Bps:
                BpsPatch.Write(output, Script, progress, cancellationToken);
                break;
            case PatchFormat.HexPatch:
                HexPatch.Write(output, Script, options.FullHash, options.DefaultFileName, options.Message, ops: Operations(), progress: progress,
                    cancellationToken: cancellationToken);
                break;
            default:
                SelfPatchBuilder.Write(output, options.StubPath ?? throw new InvalidOperationException("適用プログラムがありません。"),
                    s => HexPatch.Write(s, Script, options.FullHash, options.DefaultFileName, options.Message, ops: Operations(), progress: progress,
                        cancellationToken: cancellationToken, applierStrings: options.ApplierStrings));
                break;
        }
    }
}

/// <summary>
/// 自己適用型パッチ (EDIT-44) を作る: 同梱の適用プログラム (x64 / ARM64) の後ろに独自形式のパッチを付け、末尾に目印と長さを置く
/// (<see cref="SelfPatchContainer"/>)。出力する実行ファイルにはコード署名をしない (仕様 3)。
/// </summary>
public static class SelfPatchBuilder
{
    /// <summary>同梱の適用プログラムのファイル名 (アプリのフォルダの <c>PatchApplier\&lt;arch&gt;\</c> に置く)。</summary>
    public const string StubFileName = "HexPatchApply.stub";

    /// <summary>
    /// 同梱の適用プログラムのパス (なければ null)。<paramref name="appDirectory"/> はアプリのフォルダ、<paramref name="architecture"/> は <c>x64</c> か <c>arm64</c>。
    /// </summary>
    public static string? FindStub(string appDirectory, string architecture)
    {
        string path = Path.Combine(appDirectory, "PatchApplier", architecture, StubFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>適用プログラムとパッチを 1 つの実行ファイルに書く。<paramref name="writePatch"/> は独自形式のパッチを書く。</summary>
    public static void Write(Stream output, string stubPath, Action<Stream> writePatch)
    {
        using (var stub = File.OpenRead(stubPath))
        {
            if (SelfPatchContainer.Find(stub) is not null)
            {
                throw new InvalidOperationException("適用プログラムにすでにパッチが付いています。");
            }

            stub.Position = 0;
            stub.CopyTo(output);
        }

        // zip の中の位置はストリームの先頭から数えるため、パッチは一時ファイルに書いてから後ろに付ける。
        string temp = Path.Combine(Path.GetTempPath(), "HexEditor", "selfpatch-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            long length;
            using (var patch = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose))
            {
                writePatch(patch);
                length = patch.Length;
                patch.Position = 0;
                patch.CopyTo(output);
            }

            output.Write(SelfPatchContainer.Trailer(length));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
