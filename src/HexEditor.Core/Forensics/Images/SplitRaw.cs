using System.Globalization;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Forensics.Images;

/// <summary>分割 raw のファイル名 (<c>名前.001</c>、<c>名前.002</c> …。FOR-20 の仕様 2、FOR-21 の仕様 1)。</summary>
public static class SplitRawNaming
{
    /// <summary>連番の最大 (拡張子の桁が 3 桁を超えたら 4 桁にする)。</summary>
    public const int MaxSegments = 99_999;

    /// <summary><paramref name="basePath"/> (拡張子なし) の <paramref name="number"/> 番目 (1 から) のセグメント。</summary>
    public static string SegmentPath(string basePath, int number) =>
        basePath + "." + number.ToString(number < 1000 ? "D3" : "D4", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>.001</c> などの連番の拡張子なら、拡張子を除いたパスと番号を返す。
    /// </summary>
    public static bool TryParse(string path, out string basePath, out int number)
    {
        string ext = Path.GetExtension(path);
        basePath = path;
        number = 0;
        if (ext.Length is < 4 or > 6 || ext.AsSpan(1).ContainsAnyExceptInRange('0', '9')
            || !int.TryParse(ext.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 1)
        {
            return false;
        }

        basePath = path[..^ext.Length];
        return true;
    }

    /// <summary><c>.001</c> で始まる分割 raw の最初のセグメントか。</summary>
    public static bool IsFirstSegment(string path) => TryParse(path, out _, out int n) && n == 1;

    /// <summary>
    /// 存在するセグメントを探す。同じフォルダの <c>名前.NNN</c> の最大の番号までを順に並べ、途中で欠けているものを <paramref name="missing"/> に返す。
    /// </summary>
    public static IReadOnlyList<string> Discover(string firstSegment, out IReadOnlyList<string> missing)
    {
        if (!TryParse(firstSegment, out string basePath, out _))
        {
            missing = [];
            return [firstSegment];
        }

        string dir = Path.GetDirectoryName(Path.GetFullPath(basePath)) ?? ".";
        string prefix = Path.GetFileName(basePath) + ".";
        int max = 0;
        foreach (string file in Directory.EnumerateFiles(dir, prefix + "*"))
        {
            if (TryParse(file, out string b, out int n) && string.Equals(Path.GetFileName(b) + ".", prefix, StringComparison.OrdinalIgnoreCase)
                && n <= MaxSegments)
            {
                max = Math.Max(max, n);
            }
        }

        // すべての番号のパス (足りないものを含む。読み込みの位置の計算に使う) と、足りないもの。
        var all = new List<string>();
        var lost = new List<string>();
        for (int i = 1; i <= Math.Max(1, max); i++)
        {
            string p = SegmentPath(Path.Combine(dir, Path.GetFileName(basePath)), i);
            all.Add(p);
            if (!File.Exists(p))
            {
                lost.Add(p);
            }
        }

        missing = lost;
        return all;
    }
}

/// <summary>
/// 分割 raw をつないだデータソース (FOR-21 の仕様 1)。足りないセグメントは、1 つ目のセグメントと同じ長さの読めない範囲として扱う
/// (「ある分だけ開く」)。
/// </summary>
public sealed class SplitRawByteSource : ImageSourceBase
{
    private readonly SafeFileHandle?[] _handles;
    private readonly long[] _starts;
    private readonly long[] _lengths;

    private SplitRawByteSource(string path, ImageInfo info, SafeFileHandle?[] handles, long[] starts, long[] lengths) : base(path, info)
    {
        _handles = handles;
        _starts = starts;
        _lengths = lengths;
    }

    /// <summary>分割 raw を開く。<paramref name="allowMissing"/> が偽で足りないセグメントがあれば <see cref="MissingSegmentsException"/>。</summary>
    public static SplitRawByteSource Open(string firstSegment, int sectorSize = 512, bool allowMissing = false)
    {
        IReadOnlyList<string> segments = SplitRawNaming.Discover(firstSegment, out IReadOnlyList<string> missing);
        if (missing.Count > 0 && !allowMissing)
        {
            throw new MissingSegmentsException(ImageFormat.SplitRaw, missing);
        }

        var handles = new SafeFileHandle?[segments.Count];
        long[] starts = new long[segments.Count];
        long[] lengths = new long[segments.Count];
        try
        {
            long regular = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                if (File.Exists(segments[i]))
                {
                    handles[i] = File.OpenHandle(segments[i], FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    lengths[i] = RandomAccess.GetLength(handles[i]!);
                    if (regular < 0)
                    {
                        regular = lengths[i];
                    }
                }
            }

            long at = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                if (handles[i] is null)
                {
                    lengths[i] = Math.Max(0, regular);
                }

                starts[i] = at;
                at += lengths[i];
            }

            var info = new ImageInfo
            {
                Format = ImageFormat.SplitRaw,
                Files = [.. segments.Where(File.Exists)],
                MediaSize = at,
                BytesPerSector = sectorSize,
                MissingSegments = missing,
            };
            return new SplitRawByteSource(firstSegment, info, handles, starts, lengths);
        }
        catch
        {
            foreach (SafeFileHandle? h in handles)
            {
                h?.Dispose();
            }

            throw;
        }
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < count)
        {
            long pos = offset + done;
            int index = Array.BinarySearch(_starts, pos);
            if (index < 0)
            {
                index = ~index - 1;
            }

            // 長さ 0 のセグメントを飛ばす。
            while (index < _lengths.Length - 1 && pos >= _starts[index] + _lengths[index])
            {
                index++;
            }

            long inSegment = pos - _starts[index];
            int n = (int)Math.Min(count - done, _lengths[index] - inSegment);
            if (n <= 0)
            {
                break;
            }

            Span<byte> part = buffer.Slice(done, n);
            if (_handles[index] is { } handle)
            {
                int read = RandomAccess.Read(handle, part, inSegment);
                if (read < n)
                {
                    part[read..].Clear();
                    (bad ??= []).Add(new UnreadableRange(pos + read, n - read, UnreadableReason.IoError));
                }
            }
            else
            {
                part.Clear();
                (bad ??= []).Add(new UnreadableRange(pos, n, UnreadableReason.Unallocated, 2 /* ERROR_FILE_NOT_FOUND */));
            }

            done += n;
        }

        return new ReadResult(count, bad);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (SafeFileHandle? h in _handles)
            {
                h?.Dispose();
            }
        }
    }
}
