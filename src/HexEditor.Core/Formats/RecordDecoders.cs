using System.Text;

namespace HexEditor.Core.Formats;

/// <summary>
/// Intel HEX と S-record のインポート (TOOL-05 の仕様 2、TOOL-06 の仕様 2、ENG-38 のデコード)。行ごとに読み、データは
/// <see cref="SparseImageBuilder"/> の一時ファイルに書く。誤りのある行は一覧に入れ、チェックサムの誤りの行のデータは使う
/// (「誤りを無視して読み込む」を選んだ場合に読み込まれる)。
/// </summary>
public static class RecordDecoders
{
    /// <summary>アドレスの上限 (4 GiB。ENG-38 の仕様 3)。</summary>
    public const long MaxAddress = 0x1_0000_0000;

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>16 進の並びをバイト列にする。不正な文字の位置 (0 から) を <paramref name="bad"/> に返す。</summary>
    private static byte[]? ParseHex(string text, int start, out int bad)
    {
        bad = -1;
        int digits = text.Length - start;
        for (int i = start; i < text.Length; i++)
        {
            if (HexValue(text[i]) < 0)
            {
                bad = i;
                return null;
            }
        }

        if (digits % 2 != 0)
        {
            bad = text.Length;
            return null;
        }

        byte[] bytes = new byte[digits / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(HexValue(text[start + i * 2]) << 4 | HexValue(text[start + i * 2 + 1]));
        }

        return bytes;
    }

    /// <summary>16 進の英字が大文字か (英字がなければ null)。</summary>
    private static bool? CaseOf(string line, int start)
    {
        for (int i = start; i < line.Length; i++)
        {
            char c = line[i];
            if (c is >= 'A' and <= 'F')
            {
                return true;
            }

            if (c is >= 'a' and <= 'f')
            {
                return false;
            }
        }

        return null;
    }

    /// <summary>最も多く使われたデータ長 (判定できなければ 16)。</summary>
    private static int MostCommon(Dictionary<int, long> lengths) =>
        lengths.Count == 0 ? 16 : lengths.OrderByDescending(p => p.Value).ThenByDescending(p => p.Key).First().Key;

    /// <summary>Intel HEX を読む。</summary>
    public static ImportResult DecodeIntelHex(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        long upper = 0;          // 拡張アドレス (02 は segment * 16、04 は value << 16)
        bool segmentMode = false;
        bool sawSegment = false, sawLinear = false, sawData = false, leadingExtended = false, ended = false, foreign = false;
        bool? upperCase = null;
        long? start = null;
        bool startSegment = false;
        var lengths = new Dictionary<int, long>();
        int lineNo = 0;
        while (text.ReadLine(out bool tooLong) is { } raw)
        {
            lineNo++;
            if (lineNo % 4096 == 0)
            {
                progress?.CancellationToken.ThrowIfCancellationRequested();
                progress?.Report(text.BytesRead);
            }

            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (ended)
            {
                issues.Add(new ImportIssue(lineNo, 1, ImportIssueKind.DataAfterEnd, Clip(line)));
                continue;
            }

            if (line[0] != ':' || tooLong)
            {
                foreign = true;
                issues.Add(new ImportIssue(lineNo, 1, ImportIssueKind.Syntax, Clip(line), ":"));
                continue;
            }

            byte[]? bytes = ParseHex(line, 1, out int bad);
            if (bytes is null)
            {
                issues.Add(new ImportIssue(lineNo, bad + 1, bad >= line.Length ? ImportIssueKind.OddDigits : ImportIssueKind.InvalidCharacter, Clip(line)));
                continue;
            }

            if (bytes.Length < 5 || bytes[0] + 5 != bytes.Length)
            {
                issues.Add(new ImportIssue(lineNo, 2, ImportIssueKind.LengthMismatch, Clip(line),
                    bytes.Length >= 1 ? (bytes[0] + 5).ToString() : null, bytes.Length.ToString()));
                continue;
            }

            upperCase ??= CaseOf(line, 1);
            int count = bytes[0];
            int address16 = bytes[1] << 8 | bytes[2];
            byte type = bytes[3];
            ReadOnlySpan<byte> data = bytes.AsSpan(4, count);
            byte sum = 0;
            foreach (byte b in bytes.AsSpan(0, bytes.Length - 1))
            {
                sum += b;
            }

            byte expected = (byte)-sum;
            if (expected != bytes[^1])
            {
                issues.Add(new ImportIssue(lineNo, line.Length - 1, ImportIssueKind.Checksum, Clip(line), expected.ToString("X2"), bytes[^1].ToString("X2")));
            }

            switch (type)
            {
                case 0:
                {
                    long address = segmentMode ? upper + ((address16 + 0) & 0xFFFF) : upper + address16;
                    if (address + count > MaxAddress)
                    {
                        issues.Add(new ImportIssue(lineNo, 4, ImportIssueKind.AddressOverflow, Clip(line)));
                        continue;
                    }

                    sawData = true;
                    if (count > 0)
                    {
                        lengths[count] = lengths.GetValueOrDefault(count) + 1;
                    }

                    if (builder.Add(address, data) && !options.PreferLater)
                    {
                        issues.Add(new ImportIssue(lineNo, 4, ImportIssueKind.Overlap, Clip(line)));
                    }

                    break;
                }

                case 1:
                    ended = true;
                    break;
                case 2 when count == 2:
                    upper = (long)(data[0] << 8 | data[1]) << 4;
                    segmentMode = true;
                    sawSegment = true;
                    leadingExtended |= !sawData;
                    break;
                case 4 when count == 2:
                    upper = (long)(data[0] << 8 | data[1]) << 16;
                    segmentMode = false;
                    sawLinear = true;
                    leadingExtended |= !sawData;
                    break;
                case 3 when count == 4:
                    start = (long)((uint)data[0] << 24 | (uint)data[1] << 16 | (uint)data[2] << 8 | data[3]);
                    startSegment = true;
                    break;
                case 5 when count == 4:
                    start = (long)((uint)data[0] << 24 | (uint)data[1] << 16 | (uint)data[2] << 8 | data[3]);
                    startSegment = false;
                    break;
                case 2 or 3 or 4 or 5:
                    issues.Add(new ImportIssue(lineNo, 2, ImportIssueKind.LengthMismatch, Clip(line)));
                    break;
                default:
                    foreign = true;
                    issues.Add(new ImportIssue(lineNo, 8, ImportIssueKind.UnknownRecord, Clip(line), null, type.ToString("X2")));
                    break;
            }
        }

        if (!ended && lineNo > 0)
        {
            issues.Add(new ImportIssue(lineNo, 1, ImportIssueKind.MissingEnd, string.Empty));
        }

        progress?.Report(text.BytesRead);
        var settings = new EncodedFileSettings
        {
            Format = FormatIds.IntelHex,
            NewLine = text.NewLine ?? "\r\n",
            FinalNewLine = text.EndsWithNewLine,
            HasForeignLines = foreign,
            RecordLength = MostCommon(lengths),
            IntelMode = sawLinear ? IntelHexAddressMode.I32Hex : sawSegment ? IntelHexAddressMode.I16Hex : IntelHexAddressMode.I8Hex,
            StartAddress = start,
            StartIsSegment = startSegment,
            UpperCase = upperCase ?? true,
            LeadingExtendedRecord = leadingExtended && (sawLinear || sawSegment),
        };
        return Build(builder, options, issues, settings, displayName, start, null);
    }

    /// <summary>S-record を読む。</summary>
    public static ImportResult DecodeSRecord(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        string? header = null;
        long? start = null;
        long? declaredCount = null;
        int countLine = 0;
        long dataRecords = 0;
        bool ended = false, foreign = false;
        bool? upperCase = null;
        var types = new HashSet<char>();
        var lengths = new Dictionary<int, long>();
        int lineNo = 0;
        while (text.ReadLine(out bool tooLong) is { } raw)
        {
            lineNo++;
            if (lineNo % 4096 == 0)
            {
                progress?.CancellationToken.ThrowIfCancellationRequested();
                progress?.Report(text.BytesRead);
            }

            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            if (ended)
            {
                issues.Add(new ImportIssue(lineNo, 1, ImportIssueKind.DataAfterEnd, Clip(line)));
                continue;
            }

            if (line.Length < 2 || line[0] is not ('S' or 's') || tooLong)
            {
                foreign = true;
                issues.Add(new ImportIssue(lineNo, 1, ImportIssueKind.Syntax, Clip(line), "S"));
                continue;
            }

            char type = line[1];
            int addressBytes = type switch
            {
                '0' or '1' or '5' or '9' => 2,
                '2' or '6' or '8' => 3,
                '3' or '7' => 4,
                _ => -1,
            };
            if (addressBytes < 0)
            {
                foreign = true;
                issues.Add(new ImportIssue(lineNo, 2, ImportIssueKind.UnknownRecord, Clip(line), null, "S" + type));
                continue;
            }

            byte[]? bytes = ParseHex(line, 2, out int bad);
            if (bytes is null)
            {
                issues.Add(new ImportIssue(lineNo, bad + 1, bad >= line.Length ? ImportIssueKind.OddDigits : ImportIssueKind.InvalidCharacter, Clip(line)));
                continue;
            }

            if (bytes.Length < 1 || bytes[0] + 1 != bytes.Length || bytes[0] < addressBytes + 1)
            {
                issues.Add(new ImportIssue(lineNo, 3, ImportIssueKind.LengthMismatch, Clip(line),
                    bytes.Length >= 1 ? (bytes[0] + 1).ToString() : null, bytes.Length.ToString()));
                continue;
            }

            upperCase ??= CaseOf(line, 2);
            byte sum = 0;
            foreach (byte b in bytes.AsSpan(0, bytes.Length - 1))
            {
                sum += b;
            }

            byte expected = (byte)~sum;
            if (expected != bytes[^1])
            {
                issues.Add(new ImportIssue(lineNo, line.Length - 1, ImportIssueKind.Checksum, Clip(line), expected.ToString("X2"), bytes[^1].ToString("X2")));
            }

            long address = 0;
            for (int i = 0; i < addressBytes; i++)
            {
                address = address << 8 | bytes[1 + i];
            }

            ReadOnlySpan<byte> data = bytes.AsSpan(1 + addressBytes, bytes[0] - addressBytes - 1);
            switch (type)
            {
                case '0':
                    header = Encoding.ASCII.GetString(data).TrimEnd('\0');
                    break;
                case '1' or '2' or '3':
                    if (address + data.Length > MaxAddress)
                    {
                        issues.Add(new ImportIssue(lineNo, 5, ImportIssueKind.AddressOverflow, Clip(line)));
                        continue;
                    }

                    types.Add(type);
                    dataRecords++;
                    if (data.Length > 0)
                    {
                        lengths[data.Length] = lengths.GetValueOrDefault(data.Length) + 1;
                    }

                    if (builder.Add(address, data) && !options.PreferLater)
                    {
                        issues.Add(new ImportIssue(lineNo, 5, ImportIssueKind.Overlap, Clip(line)));
                    }

                    break;
                case '5' or '6':
                    declaredCount = address;
                    countLine = lineNo;
                    break;
                default:
                    start = address;
                    ended = true;
                    break;
            }
        }

        if (declaredCount is { } declared && declared != dataRecords)
        {
            issues.Add(new ImportIssue(countLine, 5, ImportIssueKind.CountMismatch, string.Empty, declared.ToString(), dataRecords.ToString()));
        }

        if (!ended && lineNo > 0)
        {
            issues.Add(new ImportIssue(lineNo, 1, ImportIssueKind.MissingEnd, string.Empty));
        }

        progress?.Report(text.BytesRead);
        var settings = new EncodedFileSettings
        {
            Format = FormatIds.SRecord,
            NewLine = text.NewLine ?? "\r\n",
            FinalNewLine = text.EndsWithNewLine,
            HasForeignLines = foreign,
            RecordLength = MostCommon(lengths),
            SRecordMode = types.Contains('3') ? SRecordAddressMode.S3 : types.Contains('2') ? SRecordAddressMode.S2 : SRecordAddressMode.S1,
            StartAddress = start,
            Header = header,
            WriteCount = declaredCount is not null,
            UpperCase = upperCase ?? true,
        };
        return Build(builder, options, issues, settings, displayName, start, header);
    }

    private static ImportResult Build(SparseImageBuilder builder, ImportOptions options, ImportIssueList issues, EncodedFileSettings settings,
        string displayName, long? start, string? header)
    {
        (long, long)? range = builder.MinAddress is long min ? (min, builder.EndAddress!.Value - 1) : null;
        SparseImage image = builder.Build(options.Placement == AddressPlacement.Lowest, options.GapFill, displayName);
        return new ImportResult(image, issues, settings)
        {
            Length = image.Length,
            DataBytes = image.DataBytes,
            AddressRange = range,
            StartAddress = start,
            Header = header,
        };
    }

    /// <summary>一覧に出す行の内容 (長すぎる行は先頭だけ)。</summary>
    internal static string Clip(string line) => line.Length <= 200 ? line : line[..200] + "…";
}
