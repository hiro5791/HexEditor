using System.Runtime.CompilerServices;
using HexEditor.Core.Engine;
using HexEditor.Core.Templates.Inference;

namespace HexEditor.App.Templates;

/// <summary>
/// ドキュメントごとのテンプレートの推定の状態 (入力・推定の結果・表の変更・下書き)。ドキュメントを閉じるまで持ち、ドキュメントに
/// 付随するデータには保存しない (TPL-40 の仕様 10)。ウィンドウ間でドキュメントを移しても同じ状態を使う。
/// </summary>
public sealed class TemplateInferenceSession
{
    private static readonly ConditionalWeakTable<Document, TemplateInferenceSession> Sessions = new();

    private TemplateInferenceSession(Document document)
    {
        Document = document;
        Table.Changed += (_, _) => TableChanged?.Invoke(this, EventArgs.Empty);
    }

    public static TemplateInferenceSession For(Document document) => Sessions.GetValue(document, d => new TemplateInferenceSession(d));

    public static TemplateInferenceSession? Find(Document document) => Sessions.TryGetValue(document, out TemplateInferenceSession? s) ? s : null;

    public Document Document { get; }

    // ───────────── 入力 (TPL-38 の仕様 1) ─────────────

    /// <summary>開始 (入力式)。</summary>
    public string StartText { get; set; } = "0x0";

    /// <summary>長さ (入力式)。</summary>
    public string LengthText { get; set; } = string.Empty;

    /// <summary>レコード長 (空欄は「自動」)。</summary>
    public string RecordLengthText { get; set; } = string.Empty;

    /// <summary>レコードの数 (空欄は「範囲いっぱい」)。</summary>
    public string RecordCountText { get; set; } = string.Empty;

    public InferenceEndian Endian { get; set; } = InferenceEndian.Auto;

    /// <summary>入力を一度でも入れたか (パネルを開いたときに既定の範囲を入れるかどうか)。</summary>
    public bool Configured { get; set; }

    // ───────────── 結果 ─────────────

    public InferenceTable Table { get; } = new();

    public TemplateInferenceResult? Result { get; set; }

    /// <summary>レコード長の候補の一覧 (候補を選んで推定し直しても、自動で推定したときの一覧を残す)。</summary>
    public IReadOnlyList<RecordLengthCandidate> Candidates { get; set; } = [];

    /// <summary>今の下書き (表を変えたら 300 ms 後に作り直す。TPL-40 の仕様 5)。</summary>
    public string Draft { get; set; } = string.Empty;

    /// <summary>下書きを作った表の版 (表の変更の回数)。</summary>
    public int DraftVersion { get; set; }

    /// <summary>表の変更の回数。</summary>
    public int TableVersion { get; set; }

    /// <summary>推定中か。</summary>
    public bool Running { get; set; }

    /// <summary>推定の回数 (テスト用の命令)。</summary>
    public int Runs { get; set; }

    /// <summary>実行中の推定の取り消し (入力を変えたら前の推定を取り消す。TPL-38 の仕様 7)。</summary>
    public CancellationTokenSource? Cancellation { get; set; }

    /// <summary>「適用」で加えた適用 (もう一度「適用」すると置き換える)。</summary>
    public TemplateApplicationEntry? Applied { get; set; }

    /// <summary>推定の内部エラー (下書きの構文エラー) の記録。</summary>
    public string? InternalError { get; set; }

    public event EventHandler? TableChanged;
}
