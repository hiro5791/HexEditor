namespace HexEditor.Core.Processes;

/// <summary>
/// 開いたプロセスのメモリ (ENG-32〜ENG-34)。読み書きは仮想アドレスで指定し、失敗は Win32 のエラーコードで返す (0 = 成功)。
/// スレッドセーフであること。
/// </summary>
public interface IProcessMemory : IDisposable
{
    int Pid { get; }

    /// <summary>実行ファイルの名前 (<c>notepad.exe</c>)。</summary>
    string Name { get; }

    ProcessArchitecture Architecture { get; }

    /// <summary>ユーザーモードのアドレス空間の上限 + 1 (ENG-32 の仕様 5)。</summary>
    long AddressLimit { get; }

    /// <summary>読み書きのアクセス権で開いた。</summary>
    bool Writable { get; }

    /// <summary>プロセスが終了した。</summary>
    bool HasExited { get; }

    /// <summary>プロセスが終了した (任意のスレッドから呼ばれる)。</summary>
    event EventHandler? Exited;

    /// <summary>
    /// メモリ領域の一覧 (アドレスの昇順。空き・予約を含め、0〜<see cref="AddressLimit"/> を隙間なく覆う必要はない)。
    /// </summary>
    IReadOnlyList<MemoryRegion> QueryRegions();

    IReadOnlyList<ProcessModule> EnumModules();

    /// <summary>読む。<paramref name="read"/> は読めたバイト数 (先頭から連続)。</summary>
    int Read(long address, Span<byte> buffer, out int read);

    int Write(long address, ReadOnlySpan<byte> data, out int written);

    /// <summary>保護属性を変える (<c>VirtualProtectEx</c>)。<paramref name="oldProtect"/> は変える前の値。</summary>
    int Protect(long address, long size, uint protect, out uint oldProtect);
}

/// <summary>プロセスの一覧と開く処理。実装: UI のプロセスで直接 (Win32)、補助プロセス経由、テスト用の偽のプロセス。</summary>
public interface IProcessAccess
{
    IReadOnlyList<ProcessEntry> Enumerate();

    /// <exception cref="ProcessAccessException">開けない (アクセス拒否・保護されたプロセス・終了した)。</exception>
    IProcessMemory Open(int pid, bool writable);
}

/// <summary>プロセスを開けない理由。</summary>
public enum ProcessOpenFailure
{
    AccessDenied,
    Protected,
    NotFound,
    Other,
}

/// <summary>プロセスを開けない (ENG-32 の「エラー」)。</summary>
public sealed class ProcessAccessException(ProcessOpenFailure failure, int errorCode, string message) : IOException(message)
{
    public ProcessOpenFailure Failure { get; } = failure;

    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// 対象のプロセスのスレッドを一時停止・再開できるメモリ (ENG-35 の仕様 2「取得中は対象のプロセスを一時停止する」)。実装していない経路
/// (補助プロセス経由など) では一時停止できないものとして扱い、警告して続ける。停止と再開は対で呼ぶ。
/// </summary>
public interface IProcessSuspend
{
    /// <summary>すべてのスレッドを停止する。0 = 成功、それ以外は Win32 のエラーコード。</summary>
    int Suspend();

    /// <summary><see cref="Suspend"/> で停止したスレッドを再開する。</summary>
    int Resume();
}

/// <summary>
/// スレッド 1 つの状態 (ミニダンプのスレッドの一覧。ENG-35 の仕様 6)。<see cref="Context"/> は、このアプリのプロセスのアーキテクチャの
/// <c>CONTEXT</c> 構造体のバイト列 (<see cref="Architecture"/>)。
/// </summary>
public sealed record ProcessThreadState(int ThreadId, long Teb, long StackPointer, byte[] Context, ProcessArchitecture Architecture);

/// <summary>スレッドの状態を取れるメモリ (ミニダンプをデバッガでスタックまで追えるようにする)。取れない経路では空のスレッドで代用する。</summary>
public interface IProcessThreads
{
    /// <summary>スレッドの ID・TEB・レジスタ。取れなかったスレッドは除く。各スレッドはレジスタを読む間だけ停止する。</summary>
    IReadOnlyList<ProcessThreadState> CaptureThreads();
}
