using HexEditor.Core.Expressions;

namespace HexEditor.Core.Editing;

/// <summary>「範囲を選択」ダイアログの入力欄 (EDIT-04 の仕様 1)。</summary>
public enum RangeField
{
    Start,

    /// <summary>終了 (このバイトを含む)。入力式の <c>sel.last</c> に当たる (仕様 2)。</summary>
    End,

    Length,
}

/// <summary>入力欄の誤り (EDIT-04 の「エラー」)。</summary>
public enum RangeFieldIssue
{
    None,

    /// <summary>式の構文エラー、存在しないブックマーク名など (<see cref="RangeSelectionModel.ErrorOf"/> に位置と理由)。</summary>
    Expression,

    /// <summary>開始がドキュメントの長さ以上。</summary>
    StartBeyondEnd,

    /// <summary>終了がドキュメントの末尾を越える (「末尾までにする」で直せる)。</summary>
    EndBeyondEnd,

    /// <summary>開始 &gt; 終了、または長さが負。</summary>
    EndBeforeStart,
}

/// <summary>選択モード (EDIT-04 の仕様 5)。</summary>
public enum RangeSelectionMode
{
    /// <summary>新しい選択 (既定)。</summary>
    New,

    /// <summary>現在の選択を広げる (既存の範囲と入力した範囲を両方含む最小の範囲)。</summary>
    Extend,
}

/// <summary>
/// 「範囲を選択」(EDIT-04) の入力の状態。開始・終了・長さのうち最後に編集した 2 つから残りの 1 つを計算する (仕様 3)。
/// UI に依存しない。ダイアログは各欄の文字列を <see cref="SetText"/> で渡し、<see cref="TextOf"/>・<see cref="IssueOf"/> を表示する。
/// </summary>
public sealed class RangeSelectionModel
{
    private readonly IExpressionContext _context;
    private readonly Dictionary<RangeField, string> _text = new();
    private readonly Dictionary<RangeField, long?> _value = new();
    private readonly Dictionary<RangeField, ExpressionException?> _error = new();
    private readonly List<RangeField> _edited = [];

    /// <param name="context">入力式の名前 (end、sel.start など) の値。</param>
    /// <param name="documentLength">ドキュメントの長さ。</param>
    /// <param name="selection">現在の選択 (なければ null)。</param>
    /// <param name="cursor">カーソル位置。</param>
    public RangeSelectionModel(IExpressionContext context, long documentLength, (long Start, long Length)? selection, long cursor)
    {
        _context = context;
        DocumentLength = documentLength;
        foreach (RangeField field in Enum.GetValues<RangeField>())
        {
            _text[field] = string.Empty;
            _value[field] = null;
            _error[field] = null;
        }

        // 初期値 (仕様 4): 選択がある場合はその開始・終了・長さ。ない場合は開始 = カーソル位置、長さ = 空。
        if (selection is { Length: > 0 } s)
        {
            SetComputed(RangeField.Start, s.Start);
            SetComputed(RangeField.Length, s.Length);
            SetComputed(RangeField.End, s.Start + s.Length - 1);
        }
        else
        {
            SetComputed(RangeField.Start, cursor);
        }

        _edited.AddRange([RangeField.Start, RangeField.Length]);
    }

    public long DocumentLength { get; }

    /// <summary>計算で求めた欄 (斜体で表示する。仕様 3)。</summary>
    public RangeField Computed => Enum.GetValues<RangeField>().First(f => !_edited.Contains(f));

    public RangeSelectionMode Mode { get; set; } = RangeSelectionMode.New;

    /// <summary>選択後に開始位置へ移動する (仕様 6。既定オン)。</summary>
    public bool ScrollToStart { get; set; } = true;

    public string TextOf(RangeField field) => _text[field];

    /// <summary>欄の値 (解釈結果。仕様 9)。空・誤りなら null。</summary>
    public long? ValueOf(RangeField field) => _value[field];

    /// <summary>式の誤り (位置と理由)。</summary>
    public ExpressionException? ErrorOf(RangeField field) => _error[field];

    public long? Start => _value[RangeField.Start];

    public long? End => _value[RangeField.End];

    public long? Length => _value[RangeField.Length];

    /// <summary>欄を編集した。最後に編集した 2 つから残りを計算する。</summary>
    public void SetText(RangeField field, string text)
    {
        _text[field] = text;
        _edited.Remove(field);
        _edited.Add(field);
        while (_edited.Count > 2)
        {
            _edited.RemoveAt(0);
        }

        Evaluate(field);
        Recompute();
    }

    /// <summary>「末尾までにする」: 終了をドキュメントの最後のバイトにする (EDIT-04 の「エラー」)。</summary>
    public void ClampEndToDocument()
    {
        if (DocumentLength > 0)
        {
            SetText(RangeField.End, Format(DocumentLength - 1));
        }
    }

    /// <summary>欄の誤り。</summary>
    public RangeFieldIssue IssueOf(RangeField field)
    {
        if (_error[field] is not null)
        {
            return RangeFieldIssue.Expression;
        }

        long? start = Start, end = End, length = Length;
        switch (field)
        {
            case RangeField.Start when start is long s && (s >= DocumentLength || s < 0):
                return RangeFieldIssue.StartBeyondEnd;
            case RangeField.End when end is long e && start is long s2 && s2 < DocumentLength && e < s2:
                return RangeFieldIssue.EndBeforeStart;
            case RangeField.End when end is long e2 && e2 >= DocumentLength && start is long s3 && s3 < DocumentLength:
                return RangeFieldIssue.EndBeyondEnd;
            case RangeField.Length when length is < 0:
                return RangeFieldIssue.EndBeforeStart;
            default:
                return RangeFieldIssue.None;
        }
    }

    /// <summary>「選択」ボタンを押せるか (長さ 0 と誤りのある場合は押せない。仕様 8)。</summary>
    public bool CanConfirm =>
        Start is long s && Length is long n && n > 0
        && Enum.GetValues<RangeField>().All(f => IssueOf(f) == RangeFieldIssue.None);

    /// <summary>確定したときに選択する範囲 (<see cref="Mode"/> が広げるなら現在の選択との和)。</summary>
    public (long Start, long Length) Result((long Start, long Length)? current = null)
    {
        if (!CanConfirm)
        {
            throw new InvalidOperationException("入力に誤りがあります。");
        }

        long start = Start!.Value, length = Length!.Value;
        if (Mode == RangeSelectionMode.Extend && current is { Length: > 0 } c)
        {
            long from = Math.Min(start, c.Start);
            long to = Math.Max(start + length, c.Start + c.Length);
            return (from, to - from);
        }

        return (start, length);
    }

    /// <summary>計算した値の表示 (`0x11F`)。</summary>
    public static string Format(long value) => value < 0 ? "-0x" + (-value).ToString("X") : "0x" + value.ToString("X");

    private void Evaluate(RangeField field)
    {
        string text = _text[field].Trim();
        if (text.Length == 0)
        {
            _value[field] = null;
            _error[field] = null;
            return;
        }

        if (ExpressionEvaluator.TryEvaluate(text, _context, out long value, out ExpressionException? error))
        {
            _value[field] = value;
            _error[field] = null;
        }
        else
        {
            _value[field] = null;
            _error[field] = error;
        }
    }

    private void Recompute()
    {
        RangeField target = Computed;
        long? start = Start, end = End, length = Length;
        long? computed = target switch
        {
            // 長さ = 終了 − 開始 + 1 (仕様 3)。
            RangeField.End when start is long s && length is long n => SafeAdd(s, n - 1),
            RangeField.Length when start is long s && end is long e => SafeAdd(e - s, 1),
            RangeField.Start when end is long e && length is long n => SafeAdd(e, -(n - 1)),
            _ => null,
        };
        SetComputed(target, computed);
    }

    private void SetComputed(RangeField field, long? value)
    {
        _value[field] = value;
        _error[field] = null;
        _text[field] = value is long v ? Format(v) : string.Empty;
    }

    private static long? SafeAdd(long a, long b)
    {
        try
        {
            return checked(a + b);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
