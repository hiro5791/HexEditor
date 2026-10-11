namespace HexEditor.Core.Devices;

/// <summary>
/// 開いたデバイス (ディスク・ボリューム・光学ドライブ) のハンドル。読み書きはセクタ境界に揃えた範囲だけを受け付ける
/// (<see cref="DeviceGeometry.LogicalSectorSize"/> の倍数の位置と長さ)。失敗は Win32 のエラーコードで返す (0 = 成功)。
/// スレッドセーフであること (ブロックキャッシュが複数のスレッドから読む)。
/// </summary>
public interface IDeviceHandle : IDisposable
{
    string Path { get; }

    /// <summary>読み書きのアクセス権で開いた。</summary>
    bool Writable { get; }

    DeviceGeometry Geometry { get; }

    /// <summary>[offset, offset + buffer.Length) を読む。</summary>
    int ReadSectors(long offset, Span<byte> buffer);

    /// <summary>[offset, offset + data.Length) に書く。</summary>
    int WriteSectors(long offset, ReadOnlySpan<byte> data);

    /// <summary><c>FSCTL_LOCK_VOLUME</c> (ボリュームのハンドルだけ)。</summary>
    int LockVolume();

    /// <summary><c>FSCTL_DISMOUNT_VOLUME</c>。</summary>
    int DismountVolume();

    /// <summary><c>FSCTL_UNLOCK_VOLUME</c>。</summary>
    int UnlockVolume();

    /// <summary>書き込みのディスクへの反映 (<c>FlushFileBuffers</c>)。</summary>
    int Flush();
}

/// <summary>
/// デバイスの一覧と開く処理。実装は 3 つ: UI のプロセスで直接 (Win32)、昇格した補助プロセス経由 (ENG-28)、テスト用の偽のデバイス。
/// </summary>
public interface IDeviceAccess
{
    /// <summary>物理ディスク・ボリューム・光学ドライブの一覧 (ENG-29 の仕様 1)。取得できない情報は null。</summary>
    DeviceCatalog Enumerate();

    /// <summary>
    /// デバイスを開く。<paramref name="path"/> は <see cref="DevicePath"/> の形式だけ。
    /// </summary>
    /// <exception cref="DeviceException">開けない (<see cref="DeviceException.ErrorCode"/> はアクセス拒否・使用中・メディアなしなど)。</exception>
    IDeviceHandle Open(string path, bool writable);
}
