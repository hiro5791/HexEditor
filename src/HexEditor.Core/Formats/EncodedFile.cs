using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Formats;

/// <summary>
/// エンコード形式のファイル (Intel HEX・S-record・Base64) をデコードして開く (ENG-38) と、元の形式で保存する (TOOL-11)。
/// </summary>
public static class EncodedFile
{
    /// <summary>「開く」でデコードする形式か (ENG-38 の仕様 1・8)。</summary>
    public static bool IsOpenable(string format) => format is FormatIds.IntelHex or FormatIds.SRecord or FormatIds.Base64;

    /// <summary>デコードして開くための設定 (ENG-38 の仕様 3: 最小のアドレスをベースアドレスにし、隙間は塗りつぶしの値)。</summary>
    public static ImportOptions OpenOptions(string format, byte gapFill = 0xFF) => new()
    {
        Format = format,
        Placement = AddressPlacement.Lowest,
        GapFill = gapFill,
    };

    /// <summary>
    /// デコードした内容からドキュメントを作る。Intel HEX・S-record は長さ固定 (上書きのみ)、Base64 は長さを変えられる (ENG-38 の仕様 4)。
    /// 元のデータソースには書き込めない (保存は <see cref="Save"/> で元の形式に変換する)。
    /// </summary>
    public static Document CreateDocument(ImportResult result, string format, DocumentOptions? options = null)
    {
        SparseImage image = result.TakeImage();
        image.Resizable = format == FormatIds.Base64;
        return new Document(image, options);
    }

    /// <summary>元の形式への変換の設定 (TOOL-11 の仕様 1)。</summary>
    public static ExportOptions ExportOptionsFor(EncodedFileSettings s) => s.Format switch
    {
        FormatIds.IntelHex or FormatIds.SRecord => new ExportOptions
        {
            Format = s.Format,
            NewLine = s.NewLine,
            FinalNewLine = s.FinalNewLine,
            Records = new RecordExportOptions
            {
                RecordLength = s.RecordLength,
                IntelMode = s.IntelMode,
                SRecordMode = s.SRecordMode,
                ExecAddress = s.Format == FormatIds.IntelHex ? s.StartAddress : s.StartAddress ?? 0,
                ExecIsSegment = s.StartIsSegment,
                Header = s.Header ?? string.Empty,
                WriteCount = s.WriteCount,
                UpperCase = s.UpperCase,
                LeadingExtendedRecord = s.LeadingExtendedRecord,
            },
        },
        _ => new ExportOptions
        {
            Format = FormatIds.Base64,
            NewLine = s.NewLine,
            FinalNewLine = s.FinalNewLine,
            Copy = new CopyOptions { Base64LineLength = s.LineLength, Base64UrlSafe = s.UrlSafe, Base64Padding = s.Padding },
        },
    };

    /// <summary>ドキュメントの内容を出力するための元のデータ (隙間はデータのある範囲から除く)。</summary>
    public static ExportSource SourceOf(DocumentSnapshot snapshot, long baseAddress, string fileName) => new()
    {
        Read = (offset, destination) => snapshot.Read(offset, destination),
        Length = snapshot.Length,
        BaseAddress = baseAddress,
        DataRanges = snapshot.DataRanges,
        FileName = fileName,
    };

    /// <summary>
    /// 元の形式で表せるかを確かめる (TOOL-11 の「エラー」)。表せなければ <see cref="FormatLimitException"/>。書き込みを始める前に呼ぶ。
    /// </summary>
    public static void Validate(DocumentSnapshot snapshot, long baseAddress, EncodedFileSettings settings)
    {
        if (settings.Format is FormatIds.IntelHex or FormatIds.SRecord)
        {
            RecordExporter.Validate(settings.Format, ExportOptionsFor(settings).Records, [.. snapshot.DataRanges(0, snapshot.Length)], baseAddress);
        }
    }

    /// <summary>
    /// 元の形式に変換して書き出す (TOOL-11 の仕様 2。安全な保存と同じく一時ファイルに書いてから置き換える)。隙間は出力しない。
    /// </summary>
    public static void Save(DocumentSnapshot snapshot, long baseAddress, EncodedFileSettings settings, string path,
        LongRunningOperation? operation = null)
    {
        Validate(snapshot, baseAddress, settings);
        ExportOptions options = ExportOptionsFor(settings);
        ExportSource source = SourceOf(snapshot, baseAddress, Path.GetFileName(path));
        operation?.SetTotal(snapshot.Length);
        Exporter.WriteFile(path, stream => Exporter.Write(source, [(0, snapshot.Length)], options, stream,
            operation?.CancellationToken ?? default, done => operation?.Report(done)));
    }
}
