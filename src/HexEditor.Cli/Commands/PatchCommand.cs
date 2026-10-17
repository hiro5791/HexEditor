using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Hashing;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Cli.Commands;

/// <summary>
/// <c>hexed patch</c>: パッチの適用 (AUTO-43)。適用は GUI と同じ Core の <see cref="IpsPatch"/> と、書き出しは安全な保存
/// (<see cref="DocumentSaver"/>: 一時ファイルに書いてから置き換える) を使う。
/// フェーズ 3 で扱う形式は IPS / IPS32 だけ。UPS・BPS・.hexpatch はフェーズ 4 (F4-03) なので、終了コード 2 (この版では使えません)。
/// </summary>
internal sealed class PatchCommand : ISubcommand
{
    public string Name => "patch";

    /// <summary>-o はパッチ適用後のファイルの出力先 (標準出力のリダイレクトではない)。</summary>
    public bool RedirectsOutput => false;

    public IReadOnlyList<OptionSpec> Options { get; } =
    [
        OptionSpec.Value("output", "o"),
        OptionSpec.Flag("in-place"),
        OptionSpec.Value("verify-before"),
        OptionSpec.Value("verify-after"),
        OptionSpec.Flag("dry-run"),
    ];

    public int Run(CommandContext ctx)
    {
        // patch の --format はパッチの形式 (AUTO-43 の仕様 1。共通の text / json ではない)。
        string declared = ctx.Format == "text" && !ctx.Args.Has("format") ? "auto" : ctx.Format.ToLowerInvariant();
        if (declared is not ("auto" or "ips" or "ips32" or "ups" or "bps" or "hexpatch"))
        {
            throw CliException.Usage("Cli_Error_PatchFormat", ctx.Format);
        }

        if (declared is "ups" or "bps" or "hexpatch")
        {
            throw CliException.Usage("Cli_Error_PatchFormatLater", ctx.Args.Positional.Count > 1 ? ctx.Args.Positional[1] : declared);
        }

        if (ctx.Args.Positional.Count != 2)
        {
            throw CliException.Usage("Cli_Error_PatchUsage");
        }

        string? outputArg = ctx.Args.Value("output");
        bool inPlace = ctx.Args.Flag("in-place");
        bool dryRun = ctx.Args.Flag("dry-run");
        if (!dryRun && outputArg is null && !inPlace)
        {
            throw CliException.Usage("Cli_Error_PatchNeedsOutput");
        }

        if (outputArg is not null && inPlace)
        {
            throw CliException.Usage("Cli_Error_PatchOutputAndInPlace");
        }

        string targetPath = ctx.Host.Resolve(ctx.Args.Positional[0]);
        string patchPath = ctx.Host.Resolve(ctx.Args.Positional[1]);
        string? outputPath = outputArg is null ? null : ctx.Host.Resolve(outputArg);
        if (outputPath is not null && (string.Equals(outputPath, targetPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputPath, patchPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw CliException.Usage("Cli_Error_PatchOutputIsInput", outputArg);
        }

        (HashAlgorithmInfo Algorithm, ExpectedHash Expected)? before = ParseVerify(ctx, "--verify-before");
        (HashAlgorithmInfo Algorithm, ExpectedHash Expected)? after = ParseVerify(ctx, "--verify-after");
        if (!File.Exists(targetPath))
        {
            throw CliException.Io(Directory.Exists(targetPath) ? "Cli_Error_IsDirectory" : "Cli_Error_FileNotFound", ctx.Args.Positional[0]);
        }

        IpsPatchData patch = ReadPatch(ctx, patchPath, declared);
        using Document document = CliFiles.OpenDocument(targetPath);

        // 適用前の確認 (--verify-before): 合わなければ何も書かずに終了コード 1。
        if (before is { } b && !Matches(ctx, document.Current, b))
        {
            ctx.Err.WriteLine(ctx.M.Format("Cli_Patch_VerifyBeforeFailed", ctx.Args.Positional[0]));
            return ExitCodes.Negative;
        }

        long originalLength = document.Length;
        long resultLength = IpsPatch.ResultLength(patch, originalLength);
        long changedBytes = patch.Records.Sum(r => (long)r.Length);
        if (dryRun)
        {
            WriteSummary(ctx, patch, changedBytes, originalLength, resultLength);
            return ExitCodes.Success;
        }

        IpsPatch.ApplyTo(document, patch);
        if (after is { } a && !Matches(ctx, document.Current, a))
        {
            ctx.Err.WriteLine(ctx.M.Format("Cli_Patch_VerifyAfterFailed", ctx.Args.Positional[0]));
            return ExitCodes.Negative;
        }

        string destination = outputPath ?? targetPath;
        try
        {
            ctx.Run(Path.GetFileName(destination), document.Length, op =>
            {
                using FileByteSource saved = DocumentSaver.Save(document.Current, destination, op);
                return 0;
            });
        }
        catch (InsufficientSpaceException ex)
        {
            throw CliException.Io("Cli_Error_NoSpace", ex.Drive, ex.Required, ex.Available);
        }
        catch (FileSizeLimitException ex)
        {
            throw CliException.Io("Cli_Error_FileSizeLimit", ex.Drive, ex.FileSystem);
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Denied, "Cli_Error_AccessDenied", destination);
        }
        catch (IOException ex)
        {
            throw CliException.Io("Cli_Error_CannotWrite", destination, ex.Message);
        }

        if (!ctx.Quiet)
        {
            ctx.Err.WriteLine(ctx.M.Format("Cli_Patch_Applied", patch.Records.Count, changedBytes, resultLength - originalLength, destination));
        }

        return ExitCodes.Success;
    }

    private static IpsPatchData ReadPatch(CommandContext ctx, string patchPath, string declared)
    {
        byte[] head = new byte[8];
        int count;
        try
        {
            using var stream = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            count = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw CliException.Io("Cli_Error_FileNotFound", ctx.Args.Positional[1]);
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Denied, "Cli_Error_AccessDenied", ctx.Args.Positional[1]);
        }
        catch (IOException ex)
        {
            throw CliException.Io("Cli_Error_CannotRead", ctx.Args.Positional[1], ex.Message);
        }

        ReadOnlySpan<byte> span = head.AsSpan(0, count);
        bool isIps = span.StartsWith("PATCH"u8) || span.StartsWith("IPS32"u8);
        if (!isIps)
        {
            // フェーズ 4 の形式 (EDIT-42・EDIT-43 は F4-03) は、見分けて「この版では使えません」を返す。
            if (span.StartsWith("UPS1"u8) || span.StartsWith("BPS1"u8) || span.StartsWith("PK"u8))
            {
                throw CliException.Usage("Cli_Error_PatchFormatLater", ctx.Args.Positional[1]);
            }

            throw new CliException(ExitCodes.Format, "Cli_Error_PatchUnknown", ctx.Args.Positional[1]);
        }

        bool isIps32 = span.StartsWith("IPS32"u8);
        if (declared == "ips" && isIps32 || declared == "ips32" && !isIps32)
        {
            throw new CliException(ExitCodes.Format, "Cli_Error_PatchFormatMismatch", ctx.Args.Positional[1], declared);
        }

        try
        {
            using var stream = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return IpsPatch.Read(stream);
        }
        catch (IpsFormatException ex)
        {
            throw new CliException(ExitCodes.Format, "Cli_Error_PatchBroken", ctx.Args.Positional[1], ex.Record + 1, $"0x{ex.FileOffset:X}");
        }
        catch (IOException ex)
        {
            throw CliException.Io("Cli_Error_CannotRead", ctx.Args.Positional[1], ex.Message);
        }
    }

    private static (HashAlgorithmInfo Algorithm, ExpectedHash Expected)? ParseVerify(CommandContext ctx, string option)
    {
        if (ctx.Args.Value(option.TrimStart('-')) is not { } text)
        {
            return null;
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || HashCatalog.Find(text[..colon]) is not { } algorithm || !ExpectedHash.TryParseValue(text[(colon + 1)..], out byte[]? value))
        {
            throw CliException.Usage("Cli_Error_VerifySyntax", option, text);
        }

        return (algorithm, new ExpectedHash(value!));
    }

    private static bool Matches(CommandContext ctx, DocumentSnapshot snapshot, (HashAlgorithmInfo Algorithm, ExpectedHash Expected) verify)
    {
        var request = new HashRequest { Algorithms = [new HashAlgorithmChoice(verify.Algorithm)] };
        HashComputation computation = ctx.Run(verify.Algorithm.Id, HashEngine.TotalBytes(snapshot, request),
            op => HashEngine.Compute(snapshot, request, op, ctx.Cancellation));
        return verify.Expected.Compare(computation.Rows[0]) == HashMatch.Match;
    }

    private static void WriteSummary(CommandContext ctx, IpsPatchData patch, long changedBytes, long originalLength, long resultLength)
    {
        string kind = patch.Ips32 ? "ips32" : "ips";
        ctx.Out.Write(string.Create(CultureInfo.InvariantCulture, $"{ctx.M.Get("Cli_Patch_Format")}: {kind}\n"));
        ctx.Out.Write(ctx.M.Format("Cli_Patch_DryRun", patch.Records.Count, changedBytes, resultLength - originalLength, resultLength) + "\n");
    }
}
