using System.Text;

namespace HexEditor.Core.View;

/// <summary>テキスト列で Enter を押したときに書き込むもの (EDIT-12 の仕様 7)。</summary>
public enum TextEnterAction
{
    /// <summary>何もしない (既定)。</summary>
    None,
    CrLf,
    Lf,
    Cr,
}

/// <summary>
/// コピーする Hex 文字列の書式 (EDIT-22 の仕様 2 の設定「コピーする Hex 文字列の書式」)。既定は <c>DE AD BE EF</c> (空白区切り、
/// 大文字、改行なし)。<see cref="BytesPerLine"/> が 0 なら改行を入れない。
/// </summary>
public sealed record HexCopyFormat
{
    public static HexCopyFormat Default { get; } = new();

    /// <summary>バイトの間の区切り。</summary>
    public string Separator { get; init; } = " ";

    public bool UpperCase { get; init; } = true;

    /// <summary>改行を入れるバイト数 (0 は入れない)。改行は CRLF。</summary>
    public int BytesPerLine { get; init; }

    /// <summary>書式に従って Hex 文字列にする。</summary>
    public string Format(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (this == Default)
        {
            return Clipboard.HexText.Format(bytes);
        }

        var sb = new StringBuilder((int)Math.Min(int.MaxValue, TextLength(bytes.Length)));
        string format = UpperCase ? "X2" : "x2";
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(BytesPerLine > 0 && i % BytesPerLine == 0 ? "\r\n" : Separator);
            }

            sb.Append(bytes[i].ToString(format, System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary><paramref name="length"/> バイトを Hex 文字列にしたときの文字数 (クリップボードの上限の判定。EDIT-22 の仕様 4)。</summary>
    public long TextLength(long length)
    {
        if (length <= 0)
        {
            return 0;
        }

        long breaks = BytesPerLine > 0 ? (length - 1) / BytesPerLine : 0;
        long separators = length - 1 - breaks;
        return (length * 2) + (separators * Separator.Length) + (breaks * 2);
    }
}

/// <summary>
/// 編集の設定 (09 の UI-22 の「編集」の区画。各項目は 03-editing.md で定める)。アプリが設定から作り、ビュー (<see cref="EditorState.Options"/>)
/// に渡す。
/// </summary>
public sealed record EditingOptions
{
    public static EditingOptions Default { get; } = new();

    /// <summary>上書きモードで選択範囲に入力したとき、選択範囲を 00 にしてから先頭から上書きする (EDIT-11 の仕様 3。既定オフ: 先頭から上書き)。</summary>
    public bool ZeroSelectionBeforeTyping { get; init; }

    /// <summary>テキスト列での Enter (EDIT-12 の仕様 7。既定は何もしない)。</summary>
    public TextEnterAction TextEnter { get; init; } = TextEnterAction.None;

    /// <summary>上書きモードの Backspace で、直前のバイトを 00 にして戻る (EDIT-13 の仕様 3。既定オフ: カーソルを戻すだけ)。</summary>
    public bool BackspaceZeroesInOverwrite { get; init; }

    /// <summary>上書きモードでは Delete で長さを変えず、00 で塗りつぶす (EDIT-13 の仕様 4。既定オフ)。</summary>
    public bool DeleteKeepsLengthInOverwrite { get; init; }

    /// <summary>貼り付けた範囲を選択する (EDIT-23 の仕様 7。既定オン。オフならカーソルを直後に置く)。</summary>
    public bool SelectPasted { get; init; } = true;

    /// <summary>上書き貼り付けで、選択範囲があれば内容を選択範囲の長さで切り詰める (EDIT-23 の仕様 6。既定オフ)。</summary>
    public bool FitOverwritePasteToSelection { get; init; }

    /// <summary>コピーする Hex 文字列の書式 (EDIT-22 の仕様 2)。</summary>
    public HexCopyFormat HexCopy { get; init; } = HexCopyFormat.Default;
}
