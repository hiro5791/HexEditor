using HexEditor.Core.Devices;
using HexEditor.Core.Tools.Files;

namespace HexEditor.Core.Forensics.WriteBlock;

/// <summary>書き込み保護モード (12 の FOR-01) で拒否した書き込みの種類。</summary>
public enum WriteBlockTarget
{
    /// <summary>ドキュメントの保存 (元の場所への保存、開いている証拠・データソースへの「名前を付けて保存」)。</summary>
    FileSave,

    /// <summary>ファイルへの書き込み (書き出し・スクリプト・ツール) のうち、保護の対象 (開いているデータソース・証拠) へのもの。</summary>
    File,

    /// <summary>ディスク・ボリュームへの書き込み、読み書きでのオープン、ロック、ディスマウント。</summary>
    Device,

    /// <summary>プロセスメモリへの書き込み、保護属性の変更、読み書きでのオープン。</summary>
    ProcessMemory,

    /// <summary>リモートの対象 (GDB サーバー、SFTP) への書き込み。</summary>
    Remote,

    /// <summary>プラグインのデータソースへの書き込み。</summary>
    PluginSource,

    /// <summary>モードが有効な間は常に拒否する操作 (<see cref="BlockedOperation"/>)。</summary>
    Operation,
}

/// <summary>書き込み保護モードが有効な間は常に拒否する操作 (FOR-01 の仕様 2 の 5 つ目、共通の前提「破壊的な操作の確認」の 4)。</summary>
public enum BlockedOperation
{
    /// <summary>範囲・ディスク・空き領域・スラックの消去 (FOR-30〜32)。</summary>
    Wipe,

    /// <summary>ディスクのクローン (FOR-24)。</summary>
    Clone,

    /// <summary>セクタ範囲の復元 (FOR-25)。</summary>
    RestoreSectors,

    /// <summary>パーティション表の書き込み (FOR-08)。</summary>
    PartitionWrite,

    /// <summary>ファイルの安全な削除 (TOOL-17)。</summary>
    SecureDelete,

    /// <summary>代替データストリームの作成・削除・名前の変更・読み込み (TOOL-21)。</summary>
    AlternateStream,

    /// <summary>墨塗り (FOR-32 などの、元のデータへの書き込みを伴うもの)。</summary>
    Redact,
}

/// <summary>書き込み保護モードのために書き込めない (FOR-01 の「エラー」: 「書き込み保護モードのため書き込めません」)。</summary>
public sealed class WriteBlockedException : IOException
{
    public WriteBlockedException(WriteBlockTarget target, string? path, BlockedOperation? operation = null)
        : base(FormatMessage(target, path, operation))
    {
        Target = target;
        Path = path;
        Operation = operation;

        // ERROR_WRITE_PROTECT (19) の HRESULT。Win32 のエラーコードで状態を返す層 (補助プロセスなど) と同じ値。
        HResult = unchecked((int)0x80070000) | Win32Errors.WriteProtect;
    }

    public WriteBlockTarget Target { get; }

    public string? Path { get; }

    public BlockedOperation? Operation { get; }

    private static string FormatMessage(WriteBlockTarget target, string? path, BlockedOperation? operation) =>
        $"Write protection mode is on; the write was refused ({(operation is { } op ? op.ToString() : target.ToString())}{(path is null ? string.Empty : ": " + path)}).";
}

/// <summary>
/// 書き込み保護モード (FOR-01) の状態と、書き込みの関門の判定。アプリ全体で 1 つ (<see cref="WriteBlockGate.Global"/>)。
/// 書き込みをする部品 (保存、デバイス・プロセスへの書き込み、ファイルのツール、スクリプト) は、書き込む直前に <c>Ensure…</c> を呼ぶ。
/// モードが有効な間の規則:
/// <list type="bullet">
/// <item>ドキュメントの元の場所への保存は拒否する。ドキュメント単位の例外 (仕様 5) を設定したファイルだけは書ける。</item>
/// <item>ファイルへの書き込みは、保護の対象 (<see cref="IsProtectedPath"/>: 開いているドキュメントのデータソース、証拠) への書き込みだけを拒否する
/// (アプリ自身の書き込みと、証拠以外の場所への「名前を付けて保存」は制限しない。仕様 6)。</item>
/// <item>ディスク・ボリューム・プロセスメモリ・リモートの対象・プラグインのデータソースへの書き込みは、すべて拒否する (補助プロセスも同じく拒否する。仕様 2)。</item>
/// <item><see cref="BlockedOperation"/> の操作は常に拒否する。</item>
/// </list>
/// </summary>
public sealed class WriteBlockPolicy
{
    private readonly object _gate = new();
    private readonly HashSet<string> _exceptions = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;

    /// <summary>モードが有効か。</summary>
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    /// <summary>モード・例外が変わった (どのスレッドからも呼ばれうる)。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 保護の対象のファイルか (開いているドキュメントのデータソースで例外のないもの、証拠に登録したファイル)。アプリが設定する。null なら、
    /// ドキュメントの元の場所への保存だけを拒否する。
    /// </summary>
    public Func<string, bool>? IsProtectedPath { get; set; }

    /// <summary>モードを切り替える。変わったら true。無効にすると例外も消える (モードが有効な間だけの例外のため)。</summary>
    public bool SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled)
            {
                return false;
            }

            _enabled = enabled;
            if (!enabled)
            {
                _exceptions.Clear();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>ドキュメント単位の例外を設定したファイルの一覧 (完全パス)。</summary>
    public IReadOnlyList<string> Exceptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _exceptions];
            }
        }
    }

    /// <summary>
    /// ドキュメント単位の例外 (仕様 5) を設定する。例外はファイルのドキュメントだけに設定できる (ディスク・ボリューム・プロセスは補助プロセスが
    /// 書き込みを拒否するため対象外。仕様書の注記)。証拠に登録したファイルかどうかの判定は呼び出し側で行う。設定したら true。
    /// </summary>
    public bool AddException(string filePath)
    {
        string key = Normalize(filePath);
        bool added;
        lock (_gate)
        {
            added = _enabled && _exceptions.Add(key);
        }

        if (added)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    /// <summary>例外を解除する (ドキュメントを閉じた)。解除したら true。</summary>
    public bool RemoveException(string filePath)
    {
        bool removed;
        lock (_gate)
        {
            removed = _exceptions.Remove(Normalize(filePath));
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public bool IsExcepted(string filePath)
    {
        string key = Normalize(filePath);
        lock (_gate)
        {
            return _exceptions.Contains(key);
        }
    }

    /// <summary>ドキュメントの保存 (<paramref name="ownSourcePath"/> はそのドキュメントのデータソースのファイル。なければ null)。拒否するなら理由。</summary>
    public WriteBlockTarget? CheckFileSave(string targetPath, string? ownSourcePath)
    {
        if (!IsEnabled)
        {
            return null;
        }

        string target = Normalize(targetPath);
        if (ownSourcePath is not null && Same(target, Normalize(ownSourcePath)) && !IsExcepted(target))
        {
            return WriteBlockTarget.FileSave;
        }

        return IsProtectedFile(target) ? WriteBlockTarget.FileSave : null;
    }

    /// <summary>ドキュメントの保存を確かめる。拒否なら <see cref="WriteBlockedException"/>。</summary>
    public void EnsureFileSave(string targetPath, string? ownSourcePath)
    {
        if (CheckFileSave(targetPath, ownSourcePath) is { } target)
        {
            throw new WriteBlockedException(target, targetPath);
        }
    }

    /// <summary>ファイルへの書き込み・削除 (書き出し、スクリプト、ツール) を確かめる。保護の対象なら <see cref="WriteBlockedException"/>。</summary>
    public void EnsureFileWrite(string path)
    {
        if (IsEnabled && IsProtectedFile(Normalize(path)))
        {
            throw new WriteBlockedException(WriteBlockTarget.File, path);
        }
    }

    /// <summary>ファイルへの書き込みが拒否されるか。</summary>
    public bool BlocksFileWrite(string path) => IsEnabled && IsProtectedFile(Normalize(path));

    /// <summary>ディスク・ボリュームへの書き込み (読み書きでのオープン・ロック・ディスマウントを含む)。</summary>
    public void EnsureDeviceWrite(string devicePath)
    {
        if (IsEnabled)
        {
            throw new WriteBlockedException(WriteBlockTarget.Device, devicePath);
        }
    }

    /// <summary>プロセスメモリへの書き込み (読み書きでのオープン・保護属性の変更を含む)。</summary>
    public void EnsureProcessWrite(int pid)
    {
        if (IsEnabled)
        {
            throw new WriteBlockedException(WriteBlockTarget.ProcessMemory, "PID " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>リモートの対象 (GDB サーバー・SFTP) への書き込み。</summary>
    public void EnsureRemoteWrite(string target)
    {
        if (IsEnabled)
        {
            throw new WriteBlockedException(WriteBlockTarget.Remote, target);
        }
    }

    /// <summary>プラグインのデータソースへの書き込み。</summary>
    public void EnsurePluginWrite(string identity)
    {
        if (IsEnabled)
        {
            throw new WriteBlockedException(WriteBlockTarget.PluginSource, identity);
        }
    }

    /// <summary>常に拒否する操作が今拒否されるか (メニューの無効表示)。</summary>
    public bool Blocks(BlockedOperation operation) => IsEnabled;

    /// <summary>常に拒否する操作を確かめる。</summary>
    public void EnsureOperation(BlockedOperation operation, string? path = null)
    {
        if (IsEnabled)
        {
            throw new WriteBlockedException(WriteBlockTarget.Operation, path, operation);
        }
    }

    private bool IsProtectedFile(string normalized)
    {
        if (IsExcepted(normalized))
        {
            return false;
        }

        Func<string, bool>? isProtected = IsProtectedPath;
        if (isProtected is null)
        {
            return false;
        }

        if (isProtected(normalized))
        {
            return true;
        }

        // 代替データストリーム (file:stream) への書き込みは、元のファイルを変える。元のファイルが保護の対象なら拒否する。
        return AlternateStreams.SplitStreamPath(normalized) is { } split && !IsExcepted(split.File) && isProtected(Normalize(split.File));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>比較用の完全パス。</summary>
    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}

/// <summary>
/// 書き込みの関門の入口。アプリ全体の状態は <see cref="Global"/>。テストは <see cref="Use"/> で、その非同期の流れの中だけ別の状態を使う
/// (並列に動く他のテストに影響しない)。
/// </summary>
public static class WriteBlockGate
{
    private static readonly AsyncLocal<WriteBlockPolicy?> s_override = new();

    /// <summary>アプリ全体の書き込み保護モード。</summary>
    public static WriteBlockPolicy Global { get; } = new();

    /// <summary>今の流れで使う状態。</summary>
    public static WriteBlockPolicy Current => s_override.Value ?? Global;

    /// <summary>この非同期の流れの中だけ <paramref name="policy"/> を使う (テスト用)。</summary>
    public static IDisposable Use(WriteBlockPolicy policy)
    {
        WriteBlockPolicy? before = s_override.Value;
        s_override.Value = policy;
        return new Restore(before);
    }

    private sealed class Restore(WriteBlockPolicy? before) : IDisposable
    {
        public void Dispose() => s_override.Value = before;
    }
}
