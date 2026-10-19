using System.Runtime.CompilerServices;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;

namespace HexEditor.App.ViewModels;

/// <summary>結果一覧パネルの一覧の種類 (FOR-08 のスキャン、FOR-10 の $LogFile、FOR-15 の逆引き、FOR-09 のファイルの検索)。</summary>
internal enum ForensicListKind
{
    LostPartitions,
    LogFile,
    Owners,
    FileSearch,
}

/// <summary>結果一覧の 1 行 (表示の文字列と、選んだときに移動するドキュメント上の位置)。</summary>
internal sealed record ForensicRow(string Text, long? Offset, object? Item = null)
{
    public override string ToString() => Text;
}

/// <summary>結果一覧 1 つ (見出しの列と行)。</summary>
internal sealed class ForensicList(ForensicListKind kind, string title, string header)
{
    public ForensicListKind Kind { get; } = kind;
    public string Title { get; set; } = title;
    public string Header { get; } = header;
    public List<ForensicRow> Rows { get; } = [];

    /// <summary>行が増えた・変わった (UI に知らせる)。</summary>
    public int Version { get; set; }

    /// <summary>キャンセルした・完了した処理の状態の表示 (「キャンセルしました」など)。</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// ドキュメントごとのフォレンジックの状態 (FOR-06〜FOR-17): パーティションの解析結果、ファイルシステムの解析 (<see cref="FileSystemSession"/>)、
/// 強調、ファイルのクラスタの強調、結果一覧、パーティション・ファイルのタブの元の情報。ドキュメントが閉じられたら一緒に消える
/// (<see cref="ConditionalWeakTable{TKey, TValue}"/>)。
/// </summary>
internal sealed class FileSystemDocumentState
{
    private static readonly ConditionalWeakTable<Document, FileSystemDocumentState> States = [];

    public static FileSystemDocumentState For(Document document) => States.GetValue(document, _ => new FileSystemDocumentState());

    public static FileSystemDocumentState? Find(Document document) => States.TryGetValue(document, out FileSystemDocumentState? s) ? s : null;

    // ---- FOR-06 ----

    public PartitionAnalysis? Partitions { get; set; }
    public bool PartitionsRunning { get; set; }
    public int PartitionsSectorSize { get; set; }

    /// <summary>セクタサイズの提案を出した (同じドキュメントで繰り返さない)。</summary>
    public bool SuggestionShown { get; set; }

    // ---- FOR-09 ----

    public FileSystemSession? Session { get; set; }

    /// <summary>ファイルシステムを開けなかった理由 (パネルに出す)。</summary>
    public string? FileSystemMessage { get; set; }

    // ---- FOR-16 ----

    public bool HighlightOn { get; set; }
    public bool TailsRunning { get; set; }

    // ---- FOR-15 ----

    public FsEntry? HighlightedEntry { get; set; }
    public IReadOnlyList<(long Offset, long Length, FsExtent Extent)> HighlightedFragments { get; set; } = [];
    public bool OwnersRunning { get; set; }

    // ---- 結果一覧 ----

    public Dictionary<ForensicListKind, ForensicList> Lists { get; } = [];
    public ForensicListKind? ActiveList { get; set; }
    public LostPartitionScanResult? LastScan { get; set; }

    // ---- パーティションのタブ・ファイルのタブ ----

    /// <summary>パーティションのタブの元のディスクのドキュメントとパーティション (FOR-06 の仕様 6)。</summary>
    public DocumentViewModel? PartitionParent { get; set; }

    public PartitionRow? PartitionRow { get; set; }

    /// <summary>ファイル・未割り当て領域のタブ: タブのオフセット → 元のディスク上のオフセット (FOR-09 の仕様 6、FOR-16 の仕様 5)。</summary>
    public Func<long, long?>? DiskOffsetOf { get; set; }

    /// <summary>状態が変わった (パネルとステータスバーを更新する)。</summary>
    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
