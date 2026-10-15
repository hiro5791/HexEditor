namespace HexEditor.App.ViewModels;

/// <summary>スクリプトから見たドキュメントの種類 (08 の AUTO-07 の <c>kind</c>)。</summary>
public sealed partial class DocumentViewModel
{
    /// <summary><c>file</c>・<c>disk</c>・<c>volume</c>・<c>process</c>・<c>memory</c> (新規)。</summary>
    public string ScriptKind =>
        Device is { } device ? (device.Info.Path.Contains("PhysicalDrive", StringComparison.OrdinalIgnoreCase) ? "disk" : "volume")
        : IsProcessMemory ? "process"
        : FilePath is null ? "memory"
        : "file";
}
