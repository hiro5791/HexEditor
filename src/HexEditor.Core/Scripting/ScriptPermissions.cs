namespace HexEditor.Core.Scripting;

/// <summary>
/// JavaScript の許可の種類 (AUTO-34 の仕様 3)。許可なしで、開いているドキュメントの読み書き・スクリプト自身のフォルダの読み取り・
/// 利用者がダイアログで選んだファイル・<c>script.storage</c> が使える。
/// </summary>
public static class ScriptPermissions
{
    public const string FileRead = "file-read";
    public const string FileWrite = "file-write";
    public const string Network = "network";
    public const string Process = "process";
    public const string Disk = "disk";
    public const string ClipboardRead = "clipboard-read";
    public const string Settings = "settings";

    public static IReadOnlyList<string> All { get; } = [FileRead, FileWrite, Network, Process, Disk, ClipboardRead, Settings];

    public static bool IsKnown(string name) => All.Contains(name, StringComparer.Ordinal);

    /// <summary>許可の説明 (確認ダイアログの一覧に出す文。App はリソースのキー <c>Script_Permission_&lt;名前&gt;</c> で表示する)。</summary>
    public static string ResourceKey(string name) => "Script_Permission_" + name.Replace("-", "_", StringComparison.Ordinal);
}
