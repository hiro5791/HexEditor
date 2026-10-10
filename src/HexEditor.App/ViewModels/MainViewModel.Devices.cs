using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Processes;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>ディスク・プロセスメモリ・スナップショットのタブを開く (ENG-29、ENG-32、ENG-35)。</summary>
public sealed partial class MainViewModel
{
    /// <summary>デバイスが切断された・プロセスが終了した (UI は InfoBar を出す。ENG-29 の仕様 11、ENG-32 の仕様 9)。</summary>
    public event EventHandler<DocumentViewModel>? SourceDisconnected;

    /// <summary>同じ識別子のデータソースが既に開いていればそのタブを選ぶ (ENG-29 の仕様 8)。</summary>
    private DocumentViewModel? FindSameSource(string identity) =>
        Documents.FirstOrDefault(d => d.Document.Source.Identity == identity);

    /// <summary>ディスク・ボリュームのタブを開く (ENG-29)。既定で読み取り専用 (ENG-14)。</summary>
    public DocumentViewModel OpenDevice(DeviceByteSource source)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, _options);
        DocumentViewModel vm = Add(doc, null, source.DisplayName);
        doc.SetReadOnly(ReadOnlyReason.Device);
        WatchDisconnect(vm, source);

        // 開いたディスク・ボリュームを最近使ったファイルに記録する (ENG-29 の仕様 12、ENG-16、UI-32)。
        if (_files is not null)
        {
            Recent.Record(source.Path, source.DisplayName, _files.UtcNow(), Core.Files.RecentItemKind.Disk);
        }

        return vm;
    }

    /// <summary>プロセスメモリのタブを開く (ENG-32)。既定で読み取り専用。</summary>
    public DocumentViewModel OpenProcess(ProcessMemoryByteSource source, bool readOnly)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, _options);
        DocumentViewModel vm = Add(doc, null, source.DisplayName);
        doc.SetReadOnly(readOnly ? ReadOnlyReason.Device : ReadOnlyReason.None);
        WatchDisconnect(vm, source);
        return vm;
    }

    /// <summary>ディスクイメージとして開く (ENG-31)。長さ固定ならオプションで上書きのみ。</summary>
    public DocumentViewModel OpenDiskImageSource(DiskImageByteSource source)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, _options);
        DocumentViewModel vm = Add(doc, null, source.DisplayName);

        // ディスクイメージとして開いたファイルを、セクタサイズ付きで最近使ったファイルに記録する (ENG-31、ENG-16)。
        if (_files is not null)
        {
            Recent.Record(source.Path, source.DisplayName, _files.UtcNow(), Core.Files.RecentItemKind.DiskImage,
                new Core.Files.RecentOpenOptions { SectorSize = source.LogicalSectorSize });
        }

        return vm;
    }

    /// <summary>保存済み・作成したスナップショット (`.hexsnap`) のタブを開く (ENG-35 の仕様 4)。読み取り専用 (書き戻す先がない)。</summary>
    public DocumentViewModel OpenSnapshotSource(SnapshotByteSource source)
    {
        if (FindSameSource(source.Identity) is { } existing)
        {
            source.Dispose();
            Selected = existing;
            return existing;
        }

        var doc = new Document(source, _options);
        DocumentViewModel vm = Add(doc, null, source.DisplayName);
        doc.SetReadOnly(ReadOnlyReason.NoWriteTarget);
        return vm;
    }

    /// <summary>切断・終了を監視し、起きたらドキュメントを読み取り専用にして知らせる (ENG-29 の仕様 11、ENG-32 の仕様 9)。</summary>
    private void WatchDisconnect(DocumentViewModel vm, IByteSource source)
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
                if (!vm.Document.IsDisposed && vm.Document.ReadOnlyReason == ReadOnlyReason.None)
                {
                    vm.Document.SetReadOnly(ReadOnlyReason.Device);
                }

                (vm.Owner ?? this).SourceDisconnected?.Invoke(vm.Owner ?? this, vm);
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
