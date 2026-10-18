using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Tools.Checksums;

/// <summary>チェックサムファイルを作る条件 (TOOL-18 の仕様 2〜5)。</summary>
public sealed record ChecksumCreateOptions
{
    /// <summary>アルゴリズム (既定 SHA-256)。</summary>
    public string AlgorithmId { get; init; } = "sha256";

    public ChecksumFormat Format { get; init; } = ChecksumFormat.Gnu;

    /// <summary>フォルダを対象にしたときにサブフォルダを含めるか。</summary>
    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>GNU 形式の行にバイナリモードの印 <c>*</c> を付けるか (既定: 付けない)。</summary>
    public bool BinaryMark { get; init; }

    public ChecksumSeparator Separator { get; init; } = ChecksumSeparator.Slash;

    public ChecksumLineEnding LineEnding { get; init; } = ChecksumLineEnding.Lf;

    /// <summary>出力先のフォルダ。null なら対象のフォルダ (ファイルだけを選んだときは共通の親フォルダ)。サイドカーでは使わない。</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>出力のファイル名。null なら既定の名前 (<see cref="ChecksumWriter.DefaultFileName"/>)。サイドカーでは使わない。</summary>
    public string? OutputFileName { get; init; }
}

/// <summary>読めずに飛ばしたファイルとその理由 (TOOL-18 の「エラー」)。</summary>
public sealed record ChecksumSkipped(string Path, string Reason);

/// <summary>作成の計画: 対象のファイル、相対パスの基準のフォルダ、書き出すファイル (上書きの確認に使う)。</summary>
public sealed record ChecksumPlan(IReadOnlyList<string> Files, string BaseDirectory, IReadOnlyList<string> OutputPaths, long TotalBytes)
{
    /// <summary>すでに存在する出力先 (仕様 6: 上書きの確認を出す)。</summary>
    public IReadOnlyList<string> ExistingOutputs => [.. OutputPaths.Where(File.Exists)];
}

public sealed record ChecksumCreateResult(IReadOnlyList<string> Written, IReadOnlyList<ChecksumSkipped> Skipped, int FileCount);

/// <summary>チェックサムファイルの作成 (TOOL-18)。</summary>
public static class ChecksumCreator
{
    /// <summary>対象のファイルを集めて、出力先を決める。</summary>
    public static ChecksumPlan Plan(IReadOnlyList<string> inputs, ChecksumCreateOptions options)
    {
        HashAlgorithmInfo algorithm = HashCatalog.Get(options.AlgorithmId);
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? singleFolder = inputs.Count == 1 && Directory.Exists(inputs[0]) ? Path.GetFullPath(inputs[0]) : null;
        foreach (string input in inputs)
        {
            string full = Path.GetFullPath(input);
            if (Directory.Exists(full))
            {
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = options.IncludeSubfolders,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (string file in Directory.EnumerateFiles(full, "*", enumeration).Order(StringComparer.Ordinal))
                {
                    if (seen.Add(file))
                    {
                        files.Add(file);
                    }
                }
            }
            else if (seen.Add(full))
            {
                files.Add(full);
            }
        }

        string baseDirectory = options.OutputDirectory is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : singleFolder ?? CommonDirectory(files);

        var outputs = new List<string>();
        if (options.Format == ChecksumFormat.Sidecar)
        {
            outputs.AddRange(files.Select(f => f + ChecksumWriter.SidecarExtension(algorithm)));
        }
        else
        {
            string name = options.OutputFileName is { Length: > 0 } n
                ? n
                : ChecksumWriter.DefaultFileName(options.Format, algorithm, new DirectoryInfo(baseDirectory).Name);
            outputs.Add(Path.Combine(baseDirectory, name));
        }

        // 以前に作ったチェックサムファイルそのものは、ハッシュの対象にしない。
        var outputSet = new HashSet<string>(outputs, StringComparer.OrdinalIgnoreCase);
        files.RemoveAll(outputSet.Contains);
        long total = 0;
        foreach (string file in files)
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new ChecksumPlan(files, baseDirectory, outputs, total);
    }

    private static string CommonDirectory(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return Directory.GetCurrentDirectory();
        }

        string common = Path.GetDirectoryName(files[0]) ?? files[0];
        foreach (string file in files.Skip(1))
        {
            string directory = Path.GetDirectoryName(file) ?? string.Empty;
            while (common.Length > 0 && !IsUnder(directory, common))
            {
                common = Path.GetDirectoryName(common) ?? string.Empty;
            }
        }

        return common.Length == 0 ? Path.GetPathRoot(files[0]) ?? string.Empty : common;
    }

    private static bool IsUnder(string path, string directory)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// ハッシュを計算してチェックサムファイルを書く。読めないファイルは飛ばして一覧に残す (チェックサムファイルには含めない)。
    /// 上書きの確認は呼び出し側 (<see cref="ChecksumPlan.ExistingOutputs"/>)。
    /// </summary>
    public static ChecksumCreateResult Create(ChecksumPlan plan, ChecksumCreateOptions options, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        HashAlgorithmInfo algorithm = HashCatalog.Get(options.AlgorithmId);
        if (options.Format == ChecksumFormat.Sfv && !ChecksumWriter.SupportsSfv(algorithm.Id))
        {
            throw new ArgumentException("SFV is only for CRC-32.", nameof(options));
        }

        operation?.SetTotal(plan.TotalBytes);
        var skipped = new List<ChecksumSkipped>();
        var items = new List<ChecksumItem>();
        var written = new List<string>();
        long done = 0;
        int count = 0;
        foreach (string file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation?.ReportDetail(file);
            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
                byte[] value = FileHashing.Compute(file, algorithm, operation, done, cancellationToken);
                done += size;
                operation?.Report(done);
                count++;
                if (options.Format == ChecksumFormat.Sidecar)
                {
                    // サイドカーは対象のファイルと同じ場所に、ファイル名だけを書く。
                    string content = ChecksumWriter.Build(ChecksumFormat.Gnu, algorithm, [new ChecksumItem(Path.GetFileName(file), value)],
                        options.Separator, options.LineEnding, options.BinaryMark);
                    string output = file + ChecksumWriter.SidecarExtension(algorithm);
                    ChecksumWriter.WriteFile(output, content);
                    written.Add(output);
                }
                else
                {
                    items.Add(new ChecksumItem(Path.GetRelativePath(plan.BaseDirectory, file), value));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                done += size;
                skipped.Add(new ChecksumSkipped(file, ex.Message));
            }
        }

        if (options.Format != ChecksumFormat.Sidecar && items.Count > 0)
        {
            string content = ChecksumWriter.Build(options.Format, algorithm, items, options.Separator, options.LineEnding, options.BinaryMark);
            string output = plan.OutputPaths[0];
            ChecksumWriter.WriteFile(output, content);
            written.Add(output);
        }

        return new ChecksumCreateResult(written, skipped, count);
    }
}
