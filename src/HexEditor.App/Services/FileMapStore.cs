using System.Runtime.CompilerServices;
using HexEditor.Core.Engine;
using HexEditor.Core.FileMap;
using HexEditor.Core.Operations;

namespace HexEditor.App.Services;

/// <summary>1 つのドキュメントのファイルマップ (ANA-33 の仕様 7: ドキュメントを閉じるまで持ち、ドキュメントに付随するデータには保存しない)。</summary>
public sealed class FileMapState
{
    /// <summary>最新の結果 (作っている間は途中の結果)。まだ作っていなければ null。</summary>
    public FileMapResult? Result { get; internal set; }

    /// <summary>作っている間の長時間処理 (なければ null)。</summary>
    public LongRunningOperation? Operation { get; internal set; }

    public bool IsBuilding => Operation is not null;

    /// <summary>実行中の段階 (なければ null)。</summary>
    public FileMapStage? RunningStage { get; internal set; }

    /// <summary>作り始めてから概観が出るまでの時間 (アプリの中で計る。TC-ANA-33-03)。</summary>
    public TimeSpan? OverviewElapsed { get; internal set; }

    /// <summary>作り始めた時刻 (Stopwatch のタイムスタンプ)。</summary>
    internal long StartedAt { get; set; }

    /// <summary>作った時点の変換ビューの表示 (「変換ビュー: XOR 5A」。なければ null)。</summary>
    public string? TransformLabel { get; internal set; }

    /// <summary>最後の作成で失敗した理由 (なければ null)。</summary>
    public string? Error { get; internal set; }

    /// <summary>作った後に編集されたか (作った時点のデータと今のデータが違う。0.2 の 3)。</summary>
    public bool IsStale(Document document) => Result is { } r && !IsBuilding && !ReferenceEquals(r.Snapshot, document.Current);
}

/// <summary>
/// ドキュメントごとのファイルマップ (アプリ全体。タブを別のウィンドウに移しても同じものを使う)。変わったら <see cref="Changed"/> を出す
/// (作っているスレッドから出ることがある。購読する側は UI スレッドに移し、不要になったら外す)。
/// </summary>
public static class FileMapStore
{
    private static readonly ConditionalWeakTable<Document, FileMapState> States = new();

    public static event EventHandler<Document>? Changed;

    /// <summary>ドキュメントのファイルマップ (まだ作っていなければ null)。</summary>
    public static FileMapState? Get(Document document) => States.TryGetValue(document, out FileMapState? s) ? s : null;

    /// <summary>今の結果 (ミニマップ・帯が描く)。</summary>
    public static FileMapResult? ResultOf(Document document) => Get(document)?.Result;

    internal static FileMapState GetOrCreate(Document document) => States.GetValue(document, _ => new FileMapState());

    internal static void Raise(Document document) => Changed?.Invoke(null, document);
}
