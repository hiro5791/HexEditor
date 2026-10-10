using HexEditor.App.Services;
using HexEditor.Core.Devices;
using HexEditor.Core.Processes;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>ディスク・プロセスメモリのドキュメントの判別と状態 (ENG-29、ENG-32、ENG-34)。</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>ディスク・ボリュームのデータソース。</summary>
    public DeviceByteSource? Device => Document.Source as DeviceByteSource;

    /// <summary>プロセスメモリのデータソース。</summary>
    public ProcessMemoryByteSource? ProcessMemory => Document.Source as ProcessMemoryByteSource;

    /// <summary>スナップショットのデータソース (ENG-35)。</summary>
    public SnapshotByteSource? Snapshot => Document.Source as SnapshotByteSource;

    /// <summary>ディスク・ボリュームのドキュメントか。</summary>
    public bool IsDevice => Device is not null;

    /// <summary>プロセスメモリのドキュメントか (メモリマップのパネルを自動で出す。ENG-33)。</summary>
    public bool IsProcessMemory => ProcessMemory is not null;

    /// <summary>セッションに記録するデバイスのパス (ディスクのみ。UI-31 の仕様 5)。</summary>
    public string? DeviceSessionPath => Device?.Path;

    /// <summary>即時書き込みモード (プロセスメモリのみ。ENG-34 の仕様 2。ドキュメントごと、既定オフ)。</summary>
    public bool ImmediateWrite { get; set; }

    /// <summary>プロセスへの書き込みの確認を 1 回出したか (ENG-34 の仕様 1。2 回目以降は確認しない)。</summary>
    public bool ProcessWriteConfirmed { get; set; }

    /// <summary>自動更新の間隔 (ENG-18 の仕様 2。null ならオフ)。</summary>
    public TimeSpan? AutoRefreshInterval { get; set; }

    /// <summary>ステータスバー・タブの表示を更新する (即時書き込みの切り替えなど)。</summary>
    public void NotifyStatusChanged() => OnPropertyChanged(string.Empty);

    /// <summary>即時書き込みモードの表示 (ENG-34 の「画面」)。</summary>
    public string? ImmediateWriteText => IsProcessMemory && ImmediateWrite ? Loc.Get("Status_ImmediateWrite") : null;
}
