using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Statistics;

namespace HexEditor.Core.FileMap;

/// <summary>エクスポートの形式 (ANA-34 の仕様 9)。</summary>
public enum FileMapExportFormat
{
    Json,
    Csv,
    Text,
}

/// <summary>エクスポートに書くドキュメントの情報。</summary>
public sealed record FileMapDocumentInfo(string Name, string? Path, long Length, string? TransformView);

/// <summary>
/// ファイルマップのエクスポート (ANA-34 の仕様 9): JSON・CSV・テキストのレポート。領域を 1 つずつストリームに書く (領域が 100 万個でも
/// 全体を文字列にしない)。<c>kind</c>・<c>confidence</c> などの語は表示言語によらない英語の語、<c>label</c> と <c>evidence</c> は表示言語で書く。
/// </summary>
public static class FileMapExport
{
    /// <summary>CSV の列 (この順)。</summary>
    public const string CsvHeader = "offset,length,kind,lane,label,confidence,stage,template,evidence";

    /// <summary>
    /// ファイルに書き出す。書けなかった場合は書きかけのファイルを消して例外をそのまま投げる (ANA-34 の「エラー」)。
    /// </summary>
    public static void ToFile(string path, FileMapExportFormat format, FileMapResult map, FileMapDocumentInfo info, Func<string, string> lookup,
        CultureInfo culture, CultureInfo uiCulture, CancellationToken cancellationToken = default)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                Write(stream, format, map, info, lookup, culture, uiCulture, cancellationToken);
            }
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せなければそのまま (元の例外を伝える)。
            }

            throw;
        }
    }

    public static void Write(Stream stream, FileMapExportFormat format, FileMapResult map, FileMapDocumentInfo info, Func<string, string> lookup,
        CultureInfo culture, CultureInfo uiCulture, CancellationToken cancellationToken = default)
    {
        switch (format)
        {
            case FileMapExportFormat.Json:
                WriteJson(stream, map, info, lookup, culture, uiCulture, cancellationToken);
                break;
            case FileMapExportFormat.Csv:
                WriteCsv(stream, map, lookup, culture, uiCulture, cancellationToken);
                break;
            default:
                WriteText(stream, map, info, lookup, culture, uiCulture, cancellationToken);
                break;
        }
    }

    private static string Label(FileMapResult map, int i, Func<string, string> lookup, CultureInfo culture, CultureInfo uiCulture) =>
        FileMapText.Format(map.LabelOf(i), lookup, culture, uiCulture);

    private static string Evidence(FileMapResult map, int i, Func<string, string> lookup, CultureInfo culture, CultureInfo uiCulture) =>
        FileMapText.Join(map.EvidenceOf(i), lookup, culture, uiCulture);

    private static string? TemplateOf(FileMapResult map, int i, CultureInfo uiCulture) =>
        map.Regions[i].Detail >= 0 && map.Details[map.Regions[i].Detail].Template is { } t ? t.DisplayName(uiCulture) : null;

    // ---- JSON ----

    private static void WriteJson(Stream stream, FileMapResult map, FileMapDocumentInfo info, Func<string, string> lookup, CultureInfo culture,
        CultureInfo uiCulture, CancellationToken token)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteStartObject("document");
        w.WriteString("name", info.Name);
        w.WriteString("path", info.Path);
        w.WriteNumber("length", info.Length);
        w.WriteString("transformView", info.TransformView);
        w.WriteNumber("start", map.Start);
        w.WriteNumber("targetLength", map.Length);
        w.WriteEndObject();
        w.WriteString("createdUtc", map.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        w.WriteBoolean("complete", map.Complete);
        w.WriteStartObject("stages");
        w.WriteString("sample", FileMapWords.StageState(map.Stages.Sample));
        w.WriteString("scan", FileMapWords.StageState(map.Stages.Scan));
        w.WriteString("structure", FileMapWords.StageState(map.Stages.Structure));
        w.WriteNumber("scanFraction", Math.Round(map.Stages.ScanFraction, 4));
        w.WriteEndObject();
        w.WriteNumber("blockSize", map.BlockSize);
        w.WriteNumber("omittedRegions", map.OmittedRegions);
        w.WriteStartArray("regions");
        for (int i = 0; i < map.Regions.Count; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
                w.Flush();
            }

            FileMapRegion r = map.Regions[i];
            w.WriteStartObject();
            w.WriteNumber("offset", r.Offset);
            w.WriteNumber("length", r.Length);
            w.WriteString("kind", FileMapWords.Kind(r.Kind));
            w.WriteString("lane", FileMapWords.Lane(r.Lane));
            w.WriteString("label", Label(map, i, lookup, culture, uiCulture));
            w.WriteString("confidence", FileMapWords.Confidence(r.Confidence));
            w.WriteString("evidence", Evidence(map, i, lookup, culture, uiCulture));
            w.WriteString("stage", FileMapWords.Stage(r.Stage));
            w.WriteString("template", TemplateOf(map, i, uiCulture));
            if (r.Parent < 0)
            {
                w.WriteNull("parent");
            }
            else
            {
                w.WriteNumber("parent", r.Parent);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartObject("legend");
        foreach (FileMapKind kind in Enum.GetValues<FileMapKind>())
        {
            w.WriteNumber(FileMapWords.Kind(kind), map.BytesOf(kind));
        }

        w.WriteEndObject();
        w.WriteEndObject();
        w.Flush();
    }

    // ---- CSV (TPL-25 の 3 と同じ: UTF-8 (BOM 付き)、CRLF、RFC 4180 の引用) ----

    private static void WriteCsv(Stream stream, FileMapResult map, Func<string, string> lookup, CultureInfo culture, CultureInfo uiCulture, CancellationToken token)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        w.WriteLine(CsvHeader);
        for (int i = 0; i < map.Regions.Count; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            FileMapRegion r = map.Regions[i];
            w.Write(r.Offset.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(r.Length.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(FileMapWords.Kind(r.Kind));
            w.Write(',');
            w.Write(FileMapWords.Lane(r.Lane));
            w.Write(',');
            w.Write(Quote(Label(map, i, lookup, culture, uiCulture)));
            w.Write(',');
            w.Write(FileMapWords.Confidence(r.Confidence));
            w.Write(',');
            w.Write(FileMapWords.Stage(r.Stage));
            w.Write(',');
            w.Write(Quote(TemplateOf(map, i, uiCulture) ?? string.Empty));
            w.Write(',');
            w.WriteLine(Quote(Evidence(map, i, lookup, culture, uiCulture)));
        }
    }

    /// <summary>RFC 4180 の引用 (カンマ・引用符・改行を含むものだけを囲む)。</summary>
    public static string Quote(string field) =>
        field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;

    // ---- テキストのレポート (UTF-8、BOM なし) ----

    private static void WriteText(Stream stream, FileMapResult map, FileMapDocumentInfo info, Func<string, string> lookup, CultureInfo culture,
        CultureInfo uiCulture, CancellationToken token)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        string F(FileMapMessage m) => FileMapText.Format(m, lookup, culture, uiCulture);
        w.WriteLine(F(new FileMapMessage("FileMap_EstimateNote")));
        w.WriteLine();
        w.WriteLine(F(new FileMapMessage("FileMap_Report_Document", info.Name, info.Path ?? "-", info.Length)));
        w.WriteLine(F(new FileMapMessage("FileMap_Report_Range", new FileMapHex(map.Start), new FileMapHex(Math.Max(map.Start, map.End - 1)), map.Length)));
        if (info.TransformView is { Length: > 0 } transform)
        {
            w.WriteLine(F(new FileMapMessage("FileMap_TransformView", transform)));
        }

        w.WriteLine(F(new FileMapMessage(map.Complete ? "FileMap_Report_Complete" : "FileMap_Report_Incomplete")));
        w.WriteLine();
        w.WriteLine(F(new FileMapMessage("FileMap_Report_Legend")));
        long total = Math.Max(1, map.Length);
        foreach (FileMapKind kind in Enum.GetValues<FileMapKind>())
        {
            long bytes = map.BytesOf(kind);
            if (bytes > 0)
            {
                w.WriteLine("  " + F(new FileMapMessage("FileMap_Report_LegendLine", F(new FileMapMessage("FileMap_Kind_" + kind)), bytes,
                    new FileMapPercent((double)bytes / total))));
            }
        }

        w.WriteLine();
        w.WriteLine(F(new FileMapMessage("FileMap_Report_Regions", (long)map.Regions.Count)));
        for (int i = 0; i < map.Regions.Count; i++)
        {
            if ((i & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            FileMapRegion r = map.Regions[i];
            string indent = r.Parent < 0 ? string.Empty : "  ";
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{indent}0x{r.Offset:X8}-0x{r.Last:X8}\t{r.Length.ToString("N0", culture)}\t{F(new FileMapMessage("FileMap_Kind_" + r.Kind))}\t{Label(map, i, lookup, culture, uiCulture)}\t{F(new FileMapMessage("FileMap_Confidence_" + r.Confidence))}\t{F(new FileMapMessage("FileMap_Stage_" + r.Stage))}\t{Evidence(map, i, lookup, culture, uiCulture)}"));
        }

        if (map.OmittedRegions > 0)
        {
            w.WriteLine(F(new FileMapMessage("FileMap_Omitted", map.OmittedRegions)));
        }
    }

    /// <summary>種類・確度の表示名のキー (画面と同じ)。</summary>
    public static string ConfidenceKey(Confidence c) => "FileMap_Confidence_" + c;
}
