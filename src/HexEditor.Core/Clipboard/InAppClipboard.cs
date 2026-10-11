using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Selection;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Clipboard;

/// <summary>アプリ内クリップボードの 1 回のコピー (EDIT-24 の仕様 1)。</summary>
public sealed class InAppClip
{
    internal InAppClip(long serial, SnapshotRange range, string sourceName, IReadOnlyList<ByteRange>? parts = null)
    {
        Serial = serial;
        Range = range;
        SourceName = sourceName;
        Parts = parts;
        Length = parts is null ? range.Length : parts.Sum(p => p.Length);
    }

    /// <summary>コピーの通し番号 (`HexEditor.Meta` の serial)。</summary>
    public long Serial { get; }

    /// <summary>コピーした範囲の参照。</summary>
    public SnapshotRange Range { get; }

    /// <summary>
    /// マルチ選択・矩形からのコピーのとき、各要素 (<see cref="Range"/> の先頭からの位置と長さ。オフセット順)。内容は要素を連結したもの
    /// (EDIT-07 の仕様 7)。単一の範囲なら null (<see cref="Range"/> 全体が内容)。
    /// </summary>
    public IReadOnlyList<ByteRange>? Parts { get; }

    /// <summary>内容の長さ (要素があれば要素の長さの合計)。</summary>
    public long Length { get; }

    /// <summary>内容を読むデータソース (要素があれば要素を連結して見せる。塗りつぶしの内容など)。</summary>
    public IByteSource Source => Parts is { } parts ? new ConcatenatedRangeSource(Range, parts) : Range;

    /// <summary>元ドキュメントの名前 (`HexEditor.Meta` の name)。</summary>
    public string SourceName { get; }
}

/// <summary>
/// アプリ内クリップボード (EDIT-24)。コピーのたびに選択範囲の参照 (<see cref="SnapshotRange"/>) を記録し、データは複製しない。
/// 参照元のドキュメントを閉じる前に、UI は <see cref="Document.PendingReferences"/> を見て実体化する (仕様 3・5・6)。
/// UI スレッドから使う。
/// </summary>
public sealed class InAppClipboard : IDisposable
{
    private long _serial;

    /// <summary>最後のコピー。なければ null。</summary>
    public InAppClip? Current { get; private set; }

    /// <summary>
    /// 範囲をコピーする。前のコピーは破棄する。ドキュメントの長さに関係なく一瞬で終わる (EDIT-22 の「巨大ファイル」)。
    /// </summary>
    public InAppClip Copy(Document document, long offset, long length)
    {
        SnapshotRange range = document.CreateRange(document.Current, offset, length);
        range.AddReference();
        Clear();
        Current = new InAppClip(++_serial, range, document.Source.DisplayName);
        return Current;
    }

    /// <summary>
    /// マルチ選択・矩形の要素をコピーする (要素を連結した内容。システムのクリップボードの上限を超える場合。EDIT-22 の仕様 5・8、EDIT-24)。
    /// 要素全体を含む範囲の参照を 1 つ持ち、要素はその中の位置で持つ (データは複製しない)。前のコピーは破棄する。
    /// </summary>
    public InAppClip CopyRanges(Document document, IReadOnlyList<ByteRange> ranges)
    {
        if (ranges.Count == 0)
        {
            throw new ArgumentException("要素がありません。", nameof(ranges));
        }

        long start = ranges[0].Start, end = ranges[^1].End;
        SnapshotRange range = document.CreateRange(document.Current, start, end - start);
        range.AddReference();
        Clear();
        ByteRange[] parts = [.. ranges.Select(r => new ByteRange(r.Start - start, r.Length))];
        Current = new InAppClip(++_serial, range, document.Source.DisplayName, parts);
        return Current;
    }

    /// <summary>
    /// システムのクリップボードの `HexEditor.Meta` の通し番号が最後のコピーと一致すればそれを返す。一致しない場合
    /// (他のアプリがクリップボードを書き換えた) はアプリ内クリップボードを破棄して null (仕様 2)。
    /// </summary>
    public InAppClip? Match(long serial)
    {
        if (Current is { } clip && clip.Serial == serial)
        {
            return clip;
        }

        Clear();
        return null;
    }

    /// <summary>
    /// 最後のコピーを一時ファイルに実体化する (長時間処理として呼ぶ。仕様 5)。キャンセル・失敗した場合は例外になり、
    /// 呼び出し側は <see cref="Clear"/> で破棄して InfoBar で知らせる。
    /// </summary>
    public void Materialize(string directory, LongRunningOperation? operation = null) =>
        Current?.Range.Materialize(directory, operation);

    /// <summary>アプリ内クリップボードを破棄する (参照を手放す)。</summary>
    public void Clear()
    {
        if (Current is { } clip)
        {
            Current = null;
            clip.Range.ReleaseReference();
        }
    }

    public void Dispose() => Clear();
}
