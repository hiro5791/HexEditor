using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Forensics.Audit;

namespace HexEditor.Core.Forensics.Reports;

/// <summary>
/// レポートの作成 (12 の FOR-33)。ケース情報・証拠一覧・ブックマーク・カービングの結果・監査ログと、他の領域が <see cref="IReportContributor"/> で
/// 提供する節をまとめて、HTML または PDF のファイルを作る。作ったファイルの SHA-256 を計算して監査ログに記録する (仕様 4)。
/// PDF が作れなかった場合 (書体がないなど) は HTML だけを作り、理由を <see cref="ReportResult.PdfError"/> に入れる。
/// </summary>
public static class ReportBuilder
{
    /// <summary>ファイル名に使えない文字を除く。</summary>
    public static string SafeName(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        string name = sb.ToString().Trim('_', '.');
        return name.Length == 0 ? "case" : name;
    }

    public static ReportResult Create(ReportInput input, ReportOptions options, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.OutputFolder);
        string baseName = options.FileName ?? FormattableString.Invariant($"report-{SafeName(input.Case.Number)}-{input.NowUtc:yyyyMMdd-HHmmss}");
        string prefix = baseName + "-";
        var context = new ReportContext(input, options);
        string extension = options.Format == ReportFormat.Pdf ? ".pdf" : ".html";
        string finalPath = Path.Combine(options.OutputFolder, baseName + extension);

        AuditScope? audit = options.Audit?.Begin(new AuditRecord(AuditOperations.ReportCreate)
        {
            Path = finalPath,
            Parameters = AuditRecord.Args(("format", options.Format.ToString()), ("sections", string.Join(',', options.Sections.Order(StringComparer.Ordinal))),
                ("paper", options.Paper.ToString())),
        });
        try
        {
            var attachments = new List<string>();
            string? pdfError = null;
            bool truncated = false;
            int pages = 0;
            ReportFormat actual = options.Format;
            // 書き込み保護モード (FOR-01): 保護の対象 (開いているデータソース・証拠) には書かない。
            HexEditor.Core.Forensics.WriteBlock.WriteBlockGate.Current.EnsureFileWrite(finalPath);
            string temp = finalPath + ".tmp";
            if (options.Format == ReportFormat.Pdf)
            {
                try
                {
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        pages = ReportPdf.Write(stream, context, ReportSectionsBuilder.Build(context), options.OutputFolder, prefix, attachments, out truncated, cancellationToken);
                    }

                    File.Move(temp, finalPath, overwrite: true);
                }
                catch (OperationCanceledException)
                {
                    TryDelete(temp);
                    throw;
                }
                catch (Exception ex) when (ex is ReportPdfException or IOException or ArgumentException or InvalidOperationException or NotSupportedException or IndexOutOfRangeException)
                {
                    // PDF を作れなかった: HTML だけを作る。
                    TryDelete(temp);
                    pdfError = ex.Message;
                    actual = ReportFormat.Html;
                    attachments.Clear();
                    finalPath = Path.Combine(options.OutputFolder, baseName + ".html");
                    temp = finalPath + ".tmp";
                }
            }

            if (actual == ReportFormat.Html)
            {
                try
                {
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024))
                    {
                        ReportHtml.Write(writer, context, ReportSectionsBuilder.Build(context), options.OutputFolder, prefix, attachments, cancellationToken);
                    }

                    File.Move(temp, finalPath, overwrite: true);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
            }

            string sha256;
            using (FileStream read = File.OpenRead(finalPath))
            {
                sha256 = Convert.ToHexStringLower(SHA256.HashData(read));
            }

            var result = new ReportResult
            {
                Path = finalPath,
                Format = actual,
                Sha256 = sha256,
                Bytes = new FileInfo(finalPath).Length,
                Attachments = attachments,
                PdfError = pdfError,
                PdfTruncated = truncated,
                Pages = pages,
            };
            audit?.Complete(AuditResult.Success, string.Empty, AuditRecord.Args(("file", Path.GetFileName(finalPath)), ("sha256", sha256),
                ("bytes", result.Bytes.ToString(CultureInfo.InvariantCulture))));
            return result;
        }
        catch (OperationCanceledException)
        {
            audit?.Complete(AuditResult.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            audit?.Complete(AuditResult.Failure, ex.Message);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
