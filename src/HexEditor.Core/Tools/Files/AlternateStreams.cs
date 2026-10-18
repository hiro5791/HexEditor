using System.Runtime.InteropServices;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Tools.Files;

/// <summary>代替データストリーム 1 つ。<see cref="Name"/> は <c>Zone.Identifier</c> のような名前 (既定のストリームは空)。</summary>
public sealed record StreamInfo(string Name, long Size)
{
    /// <summary>メインの (名前のない) ストリーム <c>::$DATA</c>。</summary>
    public bool IsDefault => Name.Length == 0;

    /// <summary>一覧に出す名前 (既定のストリームは <c>::$DATA</c>)。</summary>
    public string DisplayName => IsDefault ? "::$DATA" : Name;
}

/// <summary>ストリームの操作の失敗の種類 (TOOL-21 の「エラー」)。</summary>
public enum StreamError
{
    InvalidName,
    AlreadyExists,
    NotFound,
    NotSupported,
    AccessDenied,
    InUse,
    TooLargeOnRefs,
    DefaultStream,
    Io,
}

public sealed class StreamException(StreamError error, string message, Exception? inner = null) : IOException(message, inner)
{
    public StreamError Error { get; } = error;
}

/// <summary>
/// NTFS (と ReFS) の代替データストリーム (ENG-37、TOOL-21)。<c>FindFirstStreamW</c> で列挙し、ストリームは <c>ファイル名:ストリーム名</c> のパスで
/// 通常のファイルと同じように開く。名前の変更は API がないためコピーして元を削除する。
/// </summary>
public static class AlternateStreams
{
    /// <summary>先頭のバイトの Hex に出すバイト数 (TOOL-21 の仕様 2)。</summary>
    public const int HeadBytes = 16;

    private const int InvalidHandle = -1;
    private const int ErrorHandleEof = 38;
    private const int BufferSize = 1 << 20;

    // ---- パス ----

    /// <summary>パスがストリームを指すか (<c>C:\dir\file:name</c> のように、ドライブの後ろにも <c>:</c> がある)。</summary>
    public static bool IsStreamPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string name = path.Length >= 2 && path[1] == ':' ? path[2..] : path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length > 6)
        {
            name = path[6..];
        }

        int slash = name.LastIndexOfAny(['\\', '/']);
        return name.IndexOf(':', slash + 1) >= 0;
    }

    /// <summary>ストリームのパスを、ファイルのパスとストリーム名に分ける。ストリームのパスでなければ null。</summary>
    public static (string File, string Stream)? SplitStreamPath(string path)
    {
        if (!IsStreamPath(path))
        {
            return null;
        }

        int slash = path.LastIndexOfAny(['\\', '/']);
        int colon = path.IndexOf(':', slash + 1);
        string stream = path[(colon + 1)..];
        int type = stream.IndexOf(':');
        if (type >= 0)
        {
            stream = stream[..type];
        }

        return (path[..colon], stream);
    }

    /// <summary>ストリームを開くパス。既定のストリームはファイルそのもの。</summary>
    public static string PathOf(string file, string stream) => stream.Length == 0 ? file : file + ":" + stream;

    /// <summary>ストリームの名前として使えるか (TOOL-21 の仕様 3: <c>:</c> <c>\</c> <c>/</c> と制御文字は使えない)。</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > 255 || name.Trim().Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is ':' or '\\' or '/' || char.IsControl(c) || c is '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }

        return true;
    }

    // ---- ボリュームの対応 ----

    /// <summary>ファイルのあるボリュームが代替データストリームに対応するか。ボリュームの情報が取れなければ対応するものとして扱う。</summary>
    public static bool IsSupported(string path, IVolumeInfoProvider? volumes = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string full = Path.GetFullPath(path);
        string? folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (folder is null)
        {
            return true;
        }

        VolumeInfo? volume = (volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(folder);
        return volume is null || volume.SupportsNamedStreams;
    }

    public static bool IsRefs(string path, IVolumeInfoProvider? volumes = null)
    {
        string full = Path.GetFullPath(path);
        string? folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        return folder is not null && string.Equals((volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(folder)?.FileSystem, "ReFS",
            StringComparison.OrdinalIgnoreCase);
    }

    // ---- 列挙 ----

    /// <summary>ファイル (またはフォルダ) のストリームを列挙する。メインのストリームを先頭に含める (フォルダには無い)。</summary>
    public static IReadOnlyList<StreamInfo> List(string path)
    {
        var result = new List<StreamInfo>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        string full = Path.GetFullPath(path);
        IntPtr handle = FindFirstStreamW(full, 0, out FindStreamData data, 0);
        if (handle == InvalidHandle)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorHandleEof)
            {
                return result;
            }

            throw Translate(error, full);
        }

        try
        {
            do
            {
                if (ParseName(data.StreamName) is { } name)
                {
                    result.Add(new StreamInfo(name, data.StreamSize));
                }
            }
            while (FindNextStreamW(handle, out data));
        }
        finally
        {
            FindClose(handle);
        }

        return [.. result.OrderBy(s => s.IsDefault ? 0 : 1)];
    }

    /// <summary>列挙の名前 (<c>:Zone.Identifier:$DATA</c>、既定は <c>::$DATA</c>) からストリーム名を取り出す。データのストリーム以外は null。</summary>
    internal static string? ParseName(string raw)
    {
        if (!raw.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase) || raw.Length < 6 || raw[0] != ':')
        {
            return null;
        }

        return raw[1..^6];
    }

    /// <summary>ストリームの先頭の <paramref name="count"/> バイト。読めなければ空。</summary>
    public static byte[] ReadHead(string file, string stream, int count = HeadBytes)
    {
        try
        {
            using SafeFileHandle handle = OpenHandle(file, stream, FileMode.Open, FileAccess.Read);
            byte[] buffer = new byte[count];
            int n = RandomAccess.Read(handle, buffer, 0);
            return buffer[..n];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>既知のストリームの説明の識別子 (UI が <c>Ads_Known_&lt;識別子&gt;</c> の文字列を引く)。知らなければ null。</summary>
    public static string? KnownKey(string name) => name switch
    {
        "Zone.Identifier" => "ZoneIdentifier",
        "\u0005SummaryInformation" => "SummaryInformation",
        "\u0005DocumentSummaryInformation" => "DocumentSummaryInformation",
        "Afp_AfpInfo" or "Afp_Resource" => "Afp",
        "encryptable" => "Encryptable",
        "favicon" => "Favicon",
        "OECustomProperty" => "OutlookExpress",
        _ when name.StartsWith("$", StringComparison.Ordinal) => "System",
        _ => null,
    };

    // ---- 操作 ----

    /// <summary>空のストリームを作る (同名のストリームがあれば作らない)。</summary>
    public static void Create(string file, string stream)
    {
        if (!IsValidName(stream))
        {
            throw new StreamException(StreamError.InvalidName, "使えない文字が含まれています。");
        }

        if (List(file).Any(s => string.Equals(s.Name, stream, StringComparison.OrdinalIgnoreCase)))
        {
            throw new StreamException(StreamError.AlreadyExists, "同じ名前のストリームがあります。");
        }

        using SafeFileHandle handle = OpenHandle(file, stream, FileMode.CreateNew, FileAccess.Write);
    }

    /// <summary>ストリームを削除する。メインのストリームは削除できない。</summary>
    public static void Delete(string file, string stream)
    {
        if (stream.Length == 0)
        {
            throw new StreamException(StreamError.DefaultStream, "メインのストリームは削除できません。");
        }

        string path = PathOf(Path.GetFullPath(file), stream);
        if (!DeleteFileW(path))
        {
            throw Translate(Marshal.GetLastWin32Error(), path);
        }
    }

    /// <summary>名前を変える。新しい名前でコピーしてから元を削除する (NTFS にストリームの名前変更の API がないため)。</summary>
    public static void Rename(string file, string stream, string newName, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        if (stream.Length == 0)
        {
            throw new StreamException(StreamError.DefaultStream, "メインのストリームは名前を変えられません。");
        }

        if (!IsValidName(newName))
        {
            throw new StreamException(StreamError.InvalidName, "使えない文字が含まれています。");
        }

        if (List(file).Any(s => string.Equals(s.Name, newName, StringComparison.OrdinalIgnoreCase) && !string.Equals(s.Name, stream, StringComparison.Ordinal)))
        {
            throw new StreamException(StreamError.AlreadyExists, "同じ名前のストリームがあります。");
        }

        if (string.Equals(stream, newName, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Copy(file, stream, file, newName, operation, cancellationToken);
        }
        catch
        {
            TryDelete(file, newName);
            throw;
        }

        Delete(file, stream);
    }

    /// <summary>ストリームの内容をファイルに書き出す。</summary>
    public static void Export(string file, string stream, string destination, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        using SafeFileHandle source = OpenHandle(file, stream, FileMode.Open, FileAccess.Read);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        CopyHandle(source, output, operation, cancellationToken);
    }

    /// <summary>ファイルの内容でストリームの内容を置き換える。ストリームがなければ作る。</summary>
    public static void Import(string file, string stream, string source, LongRunningOperation? operation = null, CancellationToken cancellationToken = default,
        IVolumeInfoProvider? volumes = null)
    {
        if (stream.Length != 0 && !IsValidName(stream))
        {
            throw new StreamException(StreamError.InvalidName, "使えない文字が含まれています。");
        }

        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using SafeFileHandle handle = OpenHandle(file, stream, FileMode.OpenOrCreate, FileAccess.Write);
            RandomAccess.SetLength(handle, 0);
            operation?.SetTotal(input.Length);
            byte[] buffer = new byte[BufferSize];
            long position = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RandomAccess.Write(handle, buffer.AsSpan(0, n), position);
                position += n;
                operation?.Report(position);
            }
        }
        catch (IOException ex) when (ex is not StreamException && IsRefs(file, volumes))
        {
            throw new StreamException(StreamError.TooLargeOnRefs, ex.Message, ex);
        }
    }

    /// <summary>ストリームの内容を別のストリームにコピーする。</summary>
    public static void Copy(string file, string stream, string toFile, string toStream, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        using SafeFileHandle source = OpenHandle(file, stream, FileMode.Open, FileAccess.Read);
        using SafeFileHandle target = OpenHandle(toFile, toStream, FileMode.CreateNew, FileAccess.Write);
        long length = RandomAccess.GetLength(source);
        operation?.SetTotal(length);
        byte[] buffer = new byte[BufferSize];
        long position = 0;
        while (position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = RandomAccess.Read(source, buffer, position);
            if (n <= 0)
            {
                break;
            }

            RandomAccess.Write(target, buffer.AsSpan(0, n), position);
            position += n;
            operation?.Report(position);
        }
    }

    private static void CopyHandle(SafeFileHandle source, Stream output, LongRunningOperation? operation, CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(source);
        operation?.SetTotal(length);
        byte[] buffer = new byte[BufferSize];
        long position = 0;
        while (position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = RandomAccess.Read(source, buffer, position);
            if (n <= 0)
            {
                break;
            }

            output.Write(buffer, 0, n);
            position += n;
            operation?.Report(position);
        }
    }

    private static void TryDelete(string file, string stream)
    {
        try
        {
            Delete(file, stream);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>ストリーム (またはファイル、フォルダ) のハンドルを開く。フォルダのストリームも開けるようバックアップセマンティクスで開く。</summary>
    public static SafeFileHandle OpenHandle(string file, string stream, FileMode mode, FileAccess access)
    {
        string path = PathOf(Path.GetFullPath(file), stream);
        uint desired = access switch
        {
            FileAccess.Read => 0x80000000,
            FileAccess.Write => 0x40000000,
            _ => 0xC0000000,
        };
        uint disposition = mode switch
        {
            FileMode.CreateNew => 1,
            FileMode.Create => 2,
            FileMode.Open => 3,
            FileMode.OpenOrCreate => 4,
            FileMode.Truncate => 5,
            _ => 3,
        };
        SafeFileHandle handle = CreateFileW(path, desired, 7, IntPtr.Zero, disposition, 0x02000000 | 0x10000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw Translate(error, path);
        }

        return handle;
    }

    private static StreamException Translate(int error, string path) => error switch
    {
        2 or 3 => new StreamException(StreamError.NotFound, $"見つかりません: {path}"),
        5 => new StreamException(StreamError.AccessDenied, "アクセスが拒否されました。"),
        32 or 33 => new StreamException(StreamError.InUse, "他のプログラムが使用中です。"),
        80 or 183 => new StreamException(StreamError.AlreadyExists, "同じ名前のストリームがあります。"),
        1 or 87 or 50 => new StreamException(StreamError.NotSupported, "このボリュームは代替データストリームに対応していません。"),
        _ => new StreamException(StreamError.Io, new System.ComponentModel.Win32Exception(error).Message),
    };

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out FindStreamData data, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr handle, out FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
