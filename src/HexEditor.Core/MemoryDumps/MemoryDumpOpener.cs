using HexEditor.Core.Processes;
using HexEditor.Core.Sources;

namespace HexEditor.Core.MemoryDumps;

/// <summary>開いたメモリダンプの情報 (ドキュメントに出すダンプの種類と取得日時。ENG-36 の「画面」)。</summary>
public sealed record MemoryDumpInfo(MemoryDumpKind Kind, DateTime? CapturedUtc, string Path, long FileLength);

/// <summary>開いたダンプ (データソースと情報)。</summary>
public sealed record OpenedMemoryDump(IByteSource Source, MemoryDumpInfo Info);

/// <summary>
/// 「メモリダンプを開く」(01 の ENG-36)。先頭のシグネチャで形式を判定し (仕様 5)、物理アドレス空間 (raw・クラッシュダンプ) か、
/// プロセスの仮想アドレス空間 (ミニダンプ) のデータソースにする。どれも読み取り専用。形式が壊れている・この版で読めない形式は
/// <see cref="MemoryDumpFormatException"/> (呼び出し側が「raw として開く」を選ばせる)。
/// </summary>
public static class MemoryDumpOpener
{
    public static OpenedMemoryDump Open(string path, bool forceRaw = false)
    {
        long length = new FileInfo(path).Length;
        MemoryDumpDetection detection = forceRaw ? new MemoryDumpDetection(MemoryDumpKind.Raw) : MemoryDumpFormat.Detect(path);
        if (detection.IsUnsupported)
        {
            throw new MemoryDumpFormatException(detection.Unsupported == UnsupportedDumpKind.Hibernation
                ? "Hibernation files are compressed and cannot be opened as a memory dump."
                : $"This crash dump type ({detection.DumpType}) has no physical memory runs.");
        }

        switch (detection.Kind)
        {
            case MemoryDumpKind.Minidump:
            {
                SnapshotByteSource snapshot = MinidumpReader.Open(path);
                return new OpenedMemoryDump(snapshot, new MemoryDumpInfo(MemoryDumpKind.Minidump, snapshot.Metadata.CapturedUtc, path, length));
            }

            case MemoryDumpKind.CompleteDump or MemoryDumpKind.KernelDump or MemoryDumpKind.BitmapCompleteDump:
            {
                CrashDumpByteSource crash = CrashDumpByteSource.Open(path);
                return new OpenedMemoryDump(crash, new MemoryDumpInfo(crash.Header.Kind, crash.Header.SystemTimeUtc, path, length));
            }

            default:
            {
                // 物理メモリのイメージ (raw): ファイルとして開き、オフセット = 物理アドレス (仕様 2)。
                FileByteSource raw = FileByteSource.Open(path);
                return new OpenedMemoryDump(raw, new MemoryDumpInfo(MemoryDumpKind.Raw, null, path, length));
            }
        }
    }
}

/// <summary>
/// 動いている PC の物理メモリを直接読む経路 (01 の ENG-36 の仕様 1)。Windows ではカーネルドライバなしに物理メモリ (<c>\Device\PhysicalMemory</c>) を
/// 開けないため、このアプリは物理メモリを読むドライバを作らず、読み込み・同梱もしない。経路は抽象として置き、実機の実装は常に「使えない」と答える
/// (<see cref="UnavailablePhysicalMemoryAccess"/>)。テストは偽物 (<see cref="FakePhysicalMemoryAccess"/>) を使う。
/// </summary>
public interface IPhysicalMemoryAccess
{
    bool IsAvailable { get; }

    /// <summary>使えない理由 (英語の固定の文。画面には翻訳した文を出す)。</summary>
    string UnavailableReason { get; }

    /// <summary>物理アドレス空間を開く。使えなければ <see cref="NotSupportedException"/>。</summary>
    IByteSource Open();
}

/// <summary>実機: 物理メモリは読まない (ドライバが要るため。仕様 1)。</summary>
public sealed class UnavailablePhysicalMemoryAccess : IPhysicalMemoryAccess
{
    public static UnavailablePhysicalMemoryAccess Instance { get; } = new();

    public bool IsAvailable => false;

    public string UnavailableReason => "Reading physical memory requires a signed kernel driver, which this application does not provide. Open a memory dump taken by another tool instead.";

    public IByteSource Open() => throw new NotSupportedException(UnavailableReason);
}

/// <summary>テスト用の物理メモリ (メモリ上の内容)。</summary>
public sealed class FakePhysicalMemoryAccess(byte[] contents) : IPhysicalMemoryAccess
{
    public bool IsAvailable => true;

    public string UnavailableReason => string.Empty;

    public IByteSource Open() => new MemoryByteSource(contents, "Fake physical memory");
}
