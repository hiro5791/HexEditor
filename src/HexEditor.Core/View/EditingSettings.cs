namespace HexEditor.Core.View;

/// <summary>
/// 編集の設定のキーと、設定の値から <see cref="EditingOptions"/> を作る処理 (09 の UI-22 の「編集」の区画。項目は 03-editing.md)。
/// </summary>
public static class EditingSettings
{
    /// <summary>新しく開いたドキュメントの入力モード (EDIT-10 の仕様 1: <c>overwrite</c> / <c>insert</c>)。</summary>
    public const string DefaultInputModeKey = "edit.defaultInputMode";

    /// <summary>上書きモードで選択範囲に入力したとき (EDIT-11 の仕様 3: <c>overwriteFromStart</c> / <c>zeroFirst</c>)。</summary>
    public const string OverwriteSelectionTypingKey = "edit.overwriteSelectionTyping";

    /// <summary>テキスト列での Enter (EDIT-12 の仕様 7: <c>none</c> / <c>crlf</c> / <c>lf</c> / <c>cr</c>)。</summary>
    public const string TextEnterKey = "edit.text.enterKey";

    /// <summary>上書きモードの Backspace (EDIT-13 の仕様 3: <c>moveCursor</c> / <c>zeroAndMove</c>)。</summary>
    public const string BackspaceInOverwriteKey = "edit.backspaceInOverwrite";

    /// <summary>上書きモードでは Delete で長さを変えない (EDIT-13 の仕様 4)。</summary>
    public const string DeleteKeepsLengthKey = "edit.deleteKeepsLengthInOverwrite";

    /// <summary>入力をまとめる間隔 (秒。EDIT-19 の仕様 5: 0〜10、0 はまとめない)。</summary>
    public const string UndoCoalesceSecondsKey = "edit.undo.coalesceSeconds";

    /// <summary>保存時に履歴を消す (EDIT-19 の仕様 9)。</summary>
    public const string ClearHistoryOnSaveKey = "edit.undo.clearOnSave";

    /// <summary>貼り付け後に貼り付けた範囲を選択する (EDIT-23 の仕様 7)。</summary>
    public const string SelectPastedKey = "edit.paste.selectPasted";

    /// <summary>上書き貼り付けで選択範囲の長さに合わせる (EDIT-23 の仕様 6)。</summary>
    public const string FitOverwritePasteKey = "edit.pasteOverwrite.fitSelection";

    /// <summary>クリップボードに入れる最大サイズ (MiB。EDIT-22 の仕様 4: 1 MiB〜2 GiB、既定 64 MiB)。</summary>
    public const string ClipboardMaxSizeKey = "clipboard.maxSizeMiB";

    public const int DefaultClipboardMaxMiB = 64;

    public const int MaxClipboardMaxMiB = 2048;

    /// <summary>コピーする Hex 文字列の書式 (EDIT-22 の仕様 2): 区切り、大文字、改行を入れるバイト数。</summary>
    public const string HexSeparatorKey = "clipboard.hex.separator";

    public const string HexUpperCaseKey = "clipboard.hex.upperCase";

    public const string HexBytesPerLineKey = "clipboard.hex.bytesPerLine";

    /// <summary>設定の値 (<paramref name="getString"/>・<paramref name="getBool"/>・<paramref name="getInt"/> で読む) から編集の設定を作る。</summary>
    public static EditingOptions Read(Func<string, string, string> getString, Func<string, bool, bool> getBool, Func<string, int, int> getInt) => new()
    {
        ZeroSelectionBeforeTyping = getString(OverwriteSelectionTypingKey, "overwriteFromStart") == "zeroFirst",
        TextEnter = getString(TextEnterKey, "none") switch
        {
            "crlf" => TextEnterAction.CrLf,
            "lf" => TextEnterAction.Lf,
            "cr" => TextEnterAction.Cr,
            _ => TextEnterAction.None,
        },
        BackspaceZeroesInOverwrite = getString(BackspaceInOverwriteKey, "moveCursor") == "zeroAndMove",
        DeleteKeepsLengthInOverwrite = getBool(DeleteKeepsLengthKey, false),
        SelectPasted = getBool(SelectPastedKey, true),
        FitOverwritePasteToSelection = getBool(FitOverwritePasteKey, false),
        HexCopy = new HexCopyFormat
        {
            Separator = getString(HexSeparatorKey, " "),
            UpperCase = getBool(HexUpperCaseKey, true),
            BytesPerLine = Math.Clamp(getInt(HexBytesPerLineKey, 0), 0, 1024),
        } is var hex && hex == HexCopyFormat.Default ? HexCopyFormat.Default : hex,
    };

    /// <summary>クリップボードの上限 (バイト)。範囲外の値は範囲に収める。</summary>
    public static long ClipboardLimitBytes(int mib) => (long)Math.Clamp(mib, 1, MaxClipboardMaxMiB) * 1024 * 1024;

    /// <summary>入力をまとめる間隔。範囲外の値は範囲に収める。</summary>
    public static TimeSpan CoalesceInterval(int seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 10));
}
