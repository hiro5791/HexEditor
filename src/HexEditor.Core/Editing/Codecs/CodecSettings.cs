namespace HexEditor.Core.Editing.Codecs;

/// <summary>暗号化・圧縮の設定のキー (03-editing.md の 11 章)。</summary>
public static class CodecSettings
{
    /// <summary>展開の確認を出す大きさ (MiB。EDIT-41 の仕様 7)。</summary>
    public const string DecompressConfirmKey = "edit.decompress.confirmSizeMiB";

    public const int DefaultDecompressConfirmMiB = 4096;

    public const int MaxDecompressConfirmMiB = 1024 * 1024;
}
