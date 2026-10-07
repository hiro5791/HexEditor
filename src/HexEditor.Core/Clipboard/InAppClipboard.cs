using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Clipboard;

/// <summary>アプリ内クリップボードの 1 回のコピー (EDIT-24 の仕様 1)。</summary>
public sealed class InAppClip
{
    internal InAppClip(long serial, SnapshotRange range, string sourceName)
    {
        Serial = serial;
        Range = range;
        SourceName = sourceName;
    }

    /// <summary>コピーの通し番号 (`HexEditor.Meta` の serial)。</summary>
    public long Serial { get; }

    /// <summary>コピーした範囲の参照。</summary>
    public SnapshotRange Range { get; }

    public long Length => Range.Length;

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
