using HexEditor.Core.Engine;

namespace HexEditor.Core.Templates.Inference;

/// <summary>推定の結果 (TPL-38・TPL-39)。</summary>
public sealed class TemplateInferenceResult
{
    public required TemplateInferenceRequest Request { get; init; }

    /// <summary>範囲の開始。</summary>
    public long Start { get; init; }

    /// <summary>実際に使った範囲の長さ (レコード長と数を両方指定したら「レコード長 × 数」)。</summary>
    public long RangeLength { get; init; }

    public long DocumentLength { get; init; }

    /// <summary>レコード長の候補 (自動のとき。得点の順に最大 5 個)。</summary>
    public IReadOnlyList<RecordLengthCandidate> Candidates { get; init; } = [];

    /// <summary>レコード長を推定したか (指定したら false。TC-TPL-38-04)。</summary>
    public bool LengthEstimated { get; init; }

    /// <summary>使ったレコード長 (見つからない・推定しないときは null)。</summary>
    public int? RecordLength { get; init; }

    public long RecordCount { get; init; }

    /// <summary>末尾の端数 (レコードに入らないバイト数。TPL-38 の仕様 6)。</summary>
    public long Remainder { get; init; }

    /// <summary>「固定長のレコードが見つかりませんでした」(TPL-38 の仕様 4・5)。</summary>
    public bool NotFound { get; init; }

    /// <summary>推定しなかった理由 (範囲の条件、読めたレコードが 4 個未満)。</summary>
    public InferenceMessage? Blocked { get; init; }

    /// <summary>使った標本のレコードの数。</summary>
    public int SampleCount { get; init; }

    /// <summary>読めなかったため標本から除いたレコードの数。</summary>
    public int UnreadableRecords { get; init; }

    /// <summary>レコード全体のエンディアン。</summary>
    public bool BigEndian { get; init; }

    public IReadOnlyList<InferredField> Fields { get; init; } = [];

    /// <summary>推定のために読んだバイト数 (TC-TPL-38-05)。</summary>
    public long BytesRead { get; init; }

    /// <summary>ビッグエンディアンの規則の計算の回数 (TC-TPL-39-08)。</summary>
    public int BigEndianEvaluations => Inferrer?.BigEndianEvaluations ?? 0;

    /// <summary>結合・分割の後の範囲を推定し直す (TPL-40 の仕様 3)。フィールドを推定したときだけ。</summary>
    public FieldInferrer? Inferrer { get; init; }

    public bool HasFields => Fields.Count > 0;

    /// <summary>範囲の終わり (含む。下書きの先頭のコメント)。</summary>
    public long RecordsEnd => Start + ((RecordLength ?? 0) * RecordCount) - 1;
}

/// <summary>
/// テンプレートの推定 (07 の 8 章、TPL-38〜TPL-40)。読む量は、レコード長の推定に最大 1 MiB と、フィールドの推定の標本に最大 16 MiB だけ
/// (範囲の長さによらない)。バックグラウンドのスレッドから呼ぶ。
/// </summary>
public static class TemplateInference
{
    /// <summary>入力を変えたときの範囲の既定 (選択範囲がなければカーソル位置から 64 KiB)。</summary>
    public const long DefaultRangeLength = 64 * 1024;

    public static TemplateInferenceResult Run(DocumentSnapshot snapshot, TemplateInferenceRequest request, CancellationToken cancellationToken = default)
    {
        long docLength = snapshot.Length;
        long length = request.EffectiveLength(docLength);
        if (request.Validate(docLength) is { } blocked)
        {
            return new TemplateInferenceResult { Request = request, Start = request.Start, RangeLength = length, DocumentLength = docLength, Blocked = blocked };
        }

        long bytesRead = 0;
        IReadOnlyList<RecordLengthCandidate> candidates = [];
        int k;
        if (request.RecordLength is int given)
        {
            k = given;
        }
        else
        {
            int window = (int)Math.Min(RecordLengthEstimator.Window, length);
            byte[] data = new byte[window];
            snapshot.Read(request.Start, data);
            bytesRead += window;
            cancellationToken.ThrowIfCancellationRequested();
            candidates = RecordLengthEstimator.Estimate(data, length, cancellationToken);
            if (candidates.Count == 0 || candidates[0].Score < RecordLengthEstimator.MinScore)
            {
                return new TemplateInferenceResult
                {
                    Request = request,
                    Start = request.Start,
                    RangeLength = length,
                    DocumentLength = docLength,
                    Candidates = candidates,
                    LengthEstimated = true,
                    NotFound = true,
                    BytesRead = bytesRead,
                };
            }

            k = candidates[0].Length;
        }

        long count = Math.Min(request.RecordCount ?? long.MaxValue, length / k);
        long remainder = length - (count * k);
        if (count < TemplateInferenceRequest.MinRecords)
        {
            return new TemplateInferenceResult
            {
                Request = request,
                Start = request.Start,
                RangeLength = length,
                DocumentLength = docLength,
                Candidates = candidates,
                LengthEstimated = request.RecordLength is null,
                RecordLength = k,
                RecordCount = count,
                Remainder = remainder,
                Blocked = new InferenceMessage("TplInfer_Block_FourRecords"),
                BytesRead = bytesRead,
            };
        }

        RecordSample sample = RecordSample.Read(snapshot, request.Start, k, count, cancellationToken);
        bytesRead += sample.BytesRead;
        if (sample.Count < TemplateInferenceRequest.MinRecords)
        {
            return new TemplateInferenceResult
            {
                Request = request,
                Start = request.Start,
                RangeLength = length,
                DocumentLength = docLength,
                Candidates = candidates,
                LengthEstimated = request.RecordLength is null,
                RecordLength = k,
                RecordCount = count,
                Remainder = remainder,
                Blocked = new InferenceMessage("TplInfer_Block_Unreadable", sample.Count),
                SampleCount = sample.Count,
                UnreadableRecords = sample.UnreadableRecords,
                BytesRead = bytesRead,
            };
        }

        var inferrer = new FieldInferrer(sample, docLength, request.Endian);
        IReadOnlyList<InferredField> fields = inferrer.InferLayout(cancellationToken);
        return new TemplateInferenceResult
        {
            Request = request,
            Start = request.Start,
            RangeLength = length,
            DocumentLength = docLength,
            Candidates = candidates,
            LengthEstimated = request.RecordLength is null,
            RecordLength = k,
            RecordCount = count,
            Remainder = remainder,
            SampleCount = sample.Count,
            UnreadableRecords = sample.UnreadableRecords,
            BigEndian = inferrer.BigEndian,
            Fields = fields,
            BytesRead = bytesRead,
            Inferrer = inferrer,
        };
    }
}
