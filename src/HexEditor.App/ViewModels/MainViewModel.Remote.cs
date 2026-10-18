using HexEditor.Core.Engine;
using HexEditor.Core.Remote.Gdb;
using HexEditor.Core.Remote.Sftp;
using HexEditor.Core.Remote.Udp;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>リモートのデータソースのタブを開く (ENG-40〜ENG-42)。</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// GDB サーバーの対象のメモリのタブを開く (ENG-40)。<paramref name="readOnly"/> (「読み取り専用で開く」、既定オン) なら読み取り専用。
    /// 復旧はしない (対象の状態は変わるため)。切断しても編集の内容は残し (仕様 8)、読み取り専用にはしない。
    /// </summary>
    public DocumentViewModel OpenGdb(GdbByteSource source, bool readOnly)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, CurrentOptions);
        DocumentViewModel vm = Add(doc, null, source.DisplayName, recovery: false);
        doc.SetReadOnly(readOnly || !source.Writable ? ReadOnlyReason.OpenedReadOnly : ReadOnlyReason.None);
        WatchRemote(vm, source);
        return vm;
    }

    /// <summary>
    /// SFTP のリモートのファイルのタブを開く (ENG-41)。最近使ったファイルに <c>sftp://ユーザー@ホスト:ポート/パス</c> で記録する (仕様 8)。
    /// </summary>
    public DocumentViewModel OpenSftp(SftpByteSource source, bool readOnly = false)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, CurrentOptions);
        DocumentViewModel vm = Add(doc, null, source.DisplayName, recovery: false);
        if (readOnly)
        {
            doc.SetReadOnly(ReadOnlyReason.OpenedReadOnly);
        }

        WatchRemote(vm, source);
        if (_files is not null)
        {
            Recent.Record(source.Uri.ToString(), source.DisplayName, _files.UtcNow(), Core.Files.RecentItemKind.Sftp);
        }

        return vm;
    }

    /// <summary>UDP の受信のタブを開く (ENG-42)。読み取り専用 (書き戻す先がない。仕様 3)。</summary>
    public DocumentViewModel OpenUdp(UdpByteSource source)
    {
        var doc = new Document(source, CurrentOptions);
        DocumentViewModel vm = Add(doc, null, source.DisplayName, recovery: false);
        doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        return vm;
    }

    /// <summary>
    /// リモートの切断を UI に知らせる (読み取り専用にはしない。未保存の変更は残る)。保存・再読み込みで元データを差し替えたら、新しい元データにも呼ぶ。
    /// </summary>
    public void WatchRemote(DocumentViewModel vm, IByteSource source)
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        source.Changed += (_, e) =>
        {
            if (e.Kind != SourceChangeKind.Disconnected)
            {
                return;
            }

            void Handle()
            {
                if (!vm.Document.IsDisposed)
                {
                    vm.NotifyStatusChanged();
                    (vm.Owner ?? this).SourceDisconnected?.Invoke(vm.Owner ?? this, vm);
                }
            }

            if (queue is null || queue.HasThreadAccess)
            {
                Handle();
            }
            else
            {
                queue.TryEnqueue(Handle);
            }
        };
    }
}
