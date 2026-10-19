using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Tools.Checksums;
using HexEditor.Core.Forensics.WriteBlock;

namespace HexEditor.Core.Tools.Files;

/// <summary>分け方 (TOOL-13 の仕様 2)。</summary>
public enum SplitMode
{
    /// <summary>1 ファイルのサイズで分ける。最後のファイルは残りの長さ。</summary>
    Size,

    /// <summary>個数で分ける (2〜10,000)。各ファイルのサイズは切り上げで均等にする。</summary>
    Count,

    /// <summary>オフセットで分ける (ブックマークの位置を含む)。</summary>
    Positions,
}

/// <summary>分割の条件 (TOOL-13 の仕様 2〜4)。</summary>
public sealed record SplitOptions
{
    public SplitMode Mode { get; init; } = SplitMode.Size;

    /// <summary><see cref="SplitMode.Size"/>: 1 ファイルのサイズ。</summary>
    public long PartSize { get; init; }

    /// <summary><see cref="SplitMode.Count"/>: 個数。</summary>
    public int Count { get; init; } = 2;

    /// <summary><see cref="SplitMode.Count"/>: 各ファイルのサイズをこのバイト数の倍数にそろえる (既定 1)。</summary>
    public long Alignment { get; init; } = 1;

    /// <summary><see cref="SplitMode.Positions"/>: 分ける位置 (オフセット)。</summary>
    public IReadOnlyList<long> Positions { get; init; } = [];

    /// <summary>出力のファイル名の形式 (<see cref="FileNameTemplate"/>)。</summary>
    public string NameFormat { get; init; } = FileNameTemplate.SplitDefault;

    /// <summary>出力フォルダ。null なら入力と同じフォルダ。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>結合用のコマンドファイル (<c>copy /b</c> を使う <c>.cmd</c>) を作る (既定: 作らない)。</summary>
    public bool CreateJoinScript { get; init; }

    /// <summary>チェックサムファイル (TOOL-18 の形式。元のファイルと分割後の各ファイル) を作る (既定: 作らない)。</summary>
    public bool CreateChecksumFile { get; init; }
}

public sealed record SplitPart(int Index, long Offset, long Length, string FileName);

/// <summary>分割の計画: 実行前の要約 (数・最初と最後のファイル名・合計サイズ。仕様 5) と上書きの確認に使う。</summary>
public sealed record SplitPlan(IReadOnlyList<SplitPart> Parts, string OutputDirectory, string SourceName, long TotalBytes,
    IReadOnlyList<string> ExtraFiles, string ChecksumFileName)
{
    public int FileCount => Parts.Count;

    public string FirstName => Parts[0].FileName;

    public string LastName => Parts[^1].FileName;

    public string PathOf(SplitPart part) => Path.Combine(OutputDirectory, part.FileName);

    /// <summary>作るファイルすべてのパス (部品と、結合用のコマンドファイル・チェックサムファイル)。</summary>
    public IReadOnlyList<string> AllOutputs => [.. Parts.Select(PathOf), .. ExtraFiles];

    /// <summary>出力先にすでにあるファイル (仕様 5: 上書きの確認を出す)。</summary>
    public IReadOnlyList<string> ExistingFiles => [.. AllOutputs.Where(File.Exists)];
}

/// <summary>分割の結果。キャンセル・失敗でも、それまでに作ったファイルを <see cref="Completed"/> に残す (TOOL-13 の「エラー」)。</summary>
public sealed record SplitResult(IReadOnlyList<string> Completed, bool Cancelled, FileToolException? Error)
{
    public bool Succeeded => !Cancelled && Error is null;
}

/// <summary>ファイルの分割 (TOOL-13)。</summary>
public static class FileSplitter
{
    public const int MaxCount = 10_000;

    /// <summary>計画の部品の数の上限 (サイズで分けるとき、極端に小さなサイズで膨大なファイルを作らない)。</summary>
    public const int MaxParts = 1_000_000;

    private const int BufferSize = 4 * 1024 * 1024;

    public static SplitPlan Plan(long length, string sourceName, string defaultDirectory, SplitOptions options)
    {
        if (length <= 0)
        {
            throw new FileToolException(FileToolError.NothingToDo, "分割するデータがありません。");
        }

        var offsets = new List<long>();
        switch (options.Mode)
        {
            case SplitMode.Size:
                if (options.PartSize <= 0)
                {
                    throw new FileToolException(FileToolError.InvalidArgument, "サイズは 1 バイト以上にしてください。");
                }

                if ((length - 1) / options.PartSize + 1 > MaxParts)
                {
                    throw new FileToolException(FileToolError.InvalidArgument, "ファイルの数が多すぎます。");
                }

                for (long p = 0; p < length; p += options.PartSize)
                {
                    offsets.Add(p);
                }

                break;
            case SplitMode.Count:
                if (options.Count is < 2 or > MaxCount)
                {
                    throw new FileToolException(FileToolError.InvalidArgument, "個数は 2〜10,000 にしてください。");
                }

                long align = Math.Max(1, options.Alignment);
                long size = (length + options.Count - 1) / options.Count;
                size = (size + align - 1) / align * align;
                for (long p = 0; p < length; p += size)
                {
                    offsets.Add(p);
                }

                break;
            default:
                offsets.Add(0);
                foreach (long p in options.Positions.Where(p => p > 0 && p < length).Distinct().Order())
                {
                    offsets.Add(p);
                }

                if (offsets.Count < 2)
                {
                    throw new FileToolException(FileToolError.InvalidArgument, "分ける位置がありません。");
                }

                break;
        }

        IReadOnlyList<string>? names = FileNameTemplate.ExpandAll(options.NameFormat, sourceName, offsets);
        if (names is null)
        {
            throw new FileToolException(FileToolError.InvalidNames, "ファイル名の形式から、重ならない名前を作れません。");
        }

        var parts = new List<SplitPart>(offsets.Count);
        for (int i = 0; i < offsets.Count; i++)
        {
            long end = i + 1 < offsets.Count ? offsets[i + 1] : length;
            parts.Add(new SplitPart(i + 1, offsets[i], end - offsets[i], names[i]));
        }

        string directory = Path.GetFullPath(options.OutputDirectory is { Length: > 0 } d ? d : defaultDirectory);
        var extras = new List<string>();
        if (options.CreateJoinScript)
        {
            extras.Add(Path.Combine(directory, JoinScriptName(sourceName)));
        }

        string checksumName = ChecksumWriter.DefaultFileName(ChecksumFormat.Gnu, HashCatalog.Get("sha256"), new DirectoryInfo(directory).Name);
        if (options.CreateChecksumFile)
        {
            extras.Add(Path.Combine(directory, checksumName));
        }

        return new SplitPlan(parts, directory, sourceName, length, extras, checksumName);
    }

    /// <summary>ブックマークの位置 (開始オフセット) を分ける位置にする (仕様 2)。</summary>
    public static IReadOnlyList<long> PositionsFromBookmarks(Bookmarks.BookmarkCollection bookmarks) =>
        [.. bookmarks.AllWithStart.Select(b => b.Start).Distinct().Order()];

    public static string JoinScriptName(string sourceName) => sourceName + ".join.cmd";

    /// <summary>出力先の空き容量を確かめる (仕様 6)。足りなければ不足の内容を返す。取得できなければ null (確認を省く)。</summary>
    public static SpaceShortage? CheckSpace(SplitPlan plan, IVolumeInfoProvider? volumes = null)
    {
        Directory.CreateDirectory(plan.OutputDirectory);
        VolumeInfo? volume = (volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(plan.OutputDirectory);

        // 上書きするファイルの分は空く。
        long reusable = 0;
        foreach (string path in plan.ExistingFiles)
        {
            reusable += new FileInfo(path).Length;
        }

        long required = plan.TotalBytes - reusable;
        if (volume?.AvailableFreeSpace is long available && available < required)
        {
            return new SpaceShortage(volume.Name, required, available);
        }

        return null;
    }

    /// <summary>
    /// 分割する。ストリームでコピーするため、メモリ使用量は入力の大きさによらない。キャンセル・失敗したら作りかけのファイルを消し、
    /// 完成したファイルは <see cref="SplitResult.Completed"/> に残す (残すかどうかの確認は呼び出し側)。
    /// </summary>
    public static SplitResult Execute(SplitPlan plan, IContentReader input, SplitOptions options, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        var completed = new List<string>();
        operation?.SetTotal(plan.TotalBytes);
        Directory.CreateDirectory(plan.OutputDirectory);
        bool wantHashes = options.CreateChecksumFile;
        using IncrementalHash? whole = wantHashes ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var hashes = new List<ChecksumItem>();
        byte[] buffer = new byte[BufferSize];
        long done = 0;
        string? temp = null;
        try
        {
            foreach (SplitPart part in plan.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = plan.PathOf(part);
                WriteBlockGate.Current.EnsureFileWrite(target);
                temp = target + ".~part";
                operation?.ReportDetail(part.FileName);
                using IncrementalHash? partHash = wantHashes ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
                {
                    long remaining = part.Length;
                    long position = part.Offset;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(buffer.Length, remaining);
                        int n = input.Read(position, buffer.AsSpan(0, want));
                        if (n <= 0)
                        {
                            throw new IOException("入力を最後まで読めませんでした。");
                        }

                        output.Write(buffer, 0, n);
                        whole?.AppendData(buffer, 0, n);
                        partHash?.AppendData(buffer, 0, n);
                        position += n;
                        remaining -= n;
                        done += n;
                        operation?.Report(done);
                    }

                    output.Flush(flushToDisk: true);
                }

                File.Move(temp, target, overwrite: true);
                temp = null;
                completed.Add(target);
                if (partHash is not null)
                {
                    hashes.Add(new ChecksumItem(part.FileName, partHash.GetHashAndReset()));
                }
            }

            if (options.CreateJoinScript)
            {
                string script = plan.ExtraFiles.First(f => f.EndsWith(".join.cmd", StringComparison.OrdinalIgnoreCase));
                File.WriteAllText(script, BuildJoinScript(plan), new UTF8Encoding(false));
                completed.Add(script);
            }

            if (wantHashes)
            {
                var items = new List<ChecksumItem> { new(plan.SourceName, whole!.GetHashAndReset()) };
                items.AddRange(hashes);
                string content = ChecksumWriter.Build(ChecksumFormat.Gnu, HashCatalog.Get("sha256"), items, ChecksumSeparator.Slash,
                    ChecksumLineEnding.Lf, binaryMark: false);
                string path = Path.Combine(plan.OutputDirectory, plan.ChecksumFileName);
                ChecksumWriter.WriteFile(path, content);
                completed.Add(path);
            }

            return new SplitResult(completed, false, null);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(temp);
            return new SplitResult(completed, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(temp);
            return new SplitResult(completed, false, new FileToolException(FileToolError.WriteFailed, ex.Message, completed, ex));
        }
    }

    /// <summary>結合用のコマンドファイル。<c>copy /b</c> の引数が長くなりすぎないよう、50 個ずつ追記する。</summary>
    public static string BuildJoinScript(SplitPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        string output = Quote(plan.SourceName);
        for (int i = 0; i < plan.Parts.Count; i += 50)
        {
            IEnumerable<string> names = plan.Parts.Skip(i).Take(50).Select(p => Quote(p.FileName));
            sb.Append("copy /b ").Append(i == 0 ? string.Empty : output + "+").Append(string.Join('+', names)).Append(' ').Append(output).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Quote(string name) => "\"" + name.Replace("%", "%%", StringComparison.Ordinal) + "\"";

    private static void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>キャンセル・失敗で「完成したファイルを残さない」を選んだときに消す。</summary>
    public static void DeleteCompleted(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            DeleteQuietly(file);
        }
    }
}
