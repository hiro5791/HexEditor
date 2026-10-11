using HexEditor.Core.Devices;
using HexEditor.Core.Processes;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

/// <summary>ディスク・プロセスメモリへの書き込みの完了 (ENG-30 の仕様 5、ENG-34 の仕様 4)。</summary>
public sealed partial class Document
{
    private IReadOnlyList<(long Offset, long Length)> _lastDeviceWriteRanges = [];

    /// <summary>前回ディスクに書き込んだ範囲 (Undo 後に旧内容を書き戻すため。ENG-30 の仕様 5)。</summary>
    public IReadOnlyList<(long Offset, long Length)> LastDeviceWriteRanges => _lastDeviceWriteRanges;

    /// <summary>
    /// ディスク・ボリュームへの書き込みの完了 (ENG-30 の仕様 5): 退避した旧内容 (追加バッファ) を保存前の版の元データに重ね、
    /// 現在の版はデバイスをそのまま指す。Undo すると旧内容に戻り、再び保存するとデバイスに書き戻される。
    /// </summary>
    public void CompleteDeviceWrite(DeviceByteSource device, IReadOnlyList<SavedRange> ranges)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DocumentStorage before = _storage;
        var overlay = new OverlayByteSource(device, before.AddBuffer, ranges);
        if (_lastOverlay is { } previous)
        {
            previous.Inner = overlay;
        }

        _lastOverlay = overlay;
        before.Source = overlay;
        before.Cache.Dispose();
        before.Cache = NewCache(overlay);

        _storage = CreateStorage(device, before.AddBuffer);
        PieceTree tree = device.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, device.Length)) : PieceTree.Empty;
        History.ReplaceCurrent(new DocumentSnapshot(_storage, tree));
        History.MarkSaved();
        _lastDeviceWriteRanges = [.. ranges.Select(r => (r.Offset, r.Length))];
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Saved));
    }

    /// <summary>
    /// プロセスメモリへの書き込みの完了 (ENG-34 の仕様 4・5): 書き込めた範囲だけを「保存した時点」にし、失敗した範囲は「変更あり」で残す。
    /// ジャーナルは使わない (プロセスの終了で意味を失うため。ENG-34 の仕様 4)。書き込めた範囲の旧内容は追加バッファに退避してあり
    /// (Undo のため)、現在の版は読んだ値がプロセスの今の内容と同じになるよう、プロセスをそのまま指す。
    /// </summary>
    public void CompleteProcessWrite(ProcessMemoryByteSource memory, IReadOnlyList<SavedRange> written, bool allWritten)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (written.Count == 0)
        {
            return;
        }

        DocumentStorage before = _storage;
        var overlay = new OverlayByteSource(memory, before.AddBuffer, written);
        if (_lastOverlay is { } previous)
        {
            previous.Inner = overlay;
        }

        _lastOverlay = overlay;
        before.Source = overlay;
        before.Cache.Dispose();
        before.Cache = NewCache(overlay);

        if (allWritten)
        {
            _storage = CreateStorage(memory, before.AddBuffer);
            PieceTree tree = memory.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, memory.Length)) : PieceTree.Empty;
            History.ReplaceCurrent(new DocumentSnapshot(_storage, tree));
            History.MarkSaved();
        }
        else
        {
            // 一部だけ書けた場合 (ENG-34 の仕様 5): 書けた範囲はプロセスの今の内容と同じになるよう元データのピースに差し替え、
            // 失敗した範囲は変更のピースのまま残す (「変更あり」のまま)。現在の版は差し替え後の木にし、保存済みにはしない。
            _storage = CreateStorage(memory, before.AddBuffer);
            PieceTree tree = Current.Tree;
            foreach (SavedRange range in written.OrderBy(r => r.Offset))
            {
                tree = tree.Replace(range.Offset, range.Length, PieceTree.FromPiece(Piece.Original(range.Offset, range.Length)));
            }

            History.ReplaceCurrent(new DocumentSnapshot(_storage, tree));
        }

        _lastDeviceWriteRanges = [.. written.Select(r => (r.Offset, r.Length))];
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Saved));
    }
}
