using System.Globalization;
using System.Text;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.Time;

namespace HexEditor.Core.Forensics.Reports;

/// <summary>寄稿者に渡す文脈。</summary>
public sealed record ReportContext(ReportInput Input, ReportOptions Options)
{
    public string T(string key) => Options.Text(key);
}

/// <summary>レポートの日時の書式。UTC を主として、ローカル時刻 (タイムゾーン付き) を併記する (FOR-05 の仕様 6)。</summary>
public static class ReportTime
{
    private static ForensicTimestamp Stamp(DateTime utc, long precision) => ForensicTimestamp.FromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), precision);

    public static string Utc(DateTime utc, long precisionTicks = TimeSpan.TicksPerSecond) => ForensicTimeFormat.Utc(Stamp(utc, precisionTicks));

    public static string Local(DateTime utc, TimeZoneInfo zone, long precisionTicks = TimeSpan.TicksPerSecond) => ForensicTimeFormat.Local(Stamp(utc, precisionTicks), zone);

    /// <summary><c>UTC (ローカル, タイムゾーン名)</c>。</summary>
    public static string Both(DateTime utc, TimeZoneInfo zone) => $"{Utc(utc)} ({Local(utc, zone)}, {ForensicTimeFormat.ZoneName(zone)})";
}

/// <summary>レポートの節を組み立てる (FOR-33 の仕様 1)。</summary>
internal static class ReportSectionsBuilder
{
    public static IEnumerable<ReportSection> Build(ReportContext context)
    {
        ReportOptions o = context.Options;
        ReportInput input = context.Input;
        foreach (string id in ReportSections.All)
        {
            if (!o.Sections.Contains(id))
            {
                continue;
            }

            var sections = new List<ReportSection>();
            switch (id)
            {
                case ReportSections.Cover:
                    sections.Add(Cover(context));
                    break;
                case ReportSections.Evidence:
                    if (input.Evidence.Count > 0)
                    {
                        sections.Add(EvidenceSection(context));
                    }

                    break;
                case ReportSections.Bookmarks:
                    if (input.Bookmarks.Count > 0)
                    {
                        sections.Add(BookmarkSection(context));
                    }

                    break;
                case ReportSections.Carving:
                    if (CarvingSection(context) is { } carving)
                    {
                        sections.Add(carving);
                    }

                    break;
                case ReportSections.Audit:
                    if (AuditSection(context) is { } audit)
                    {
                        sections.Add(audit);
                    }

                    break;
            }

            foreach (IReportContributor c in (input.Registry ?? ReportRegistry.Shared).For(id))
            {
                if (c.Build(context) is { } section)
                {
                    sections.Add(section);
                }
            }

            foreach (ReportSection s in sections)
            {
                yield return s;
            }
        }
    }

    private static ReportSection Cover(ReportContext c)
    {
        CaseInfo info = c.Input.Case;
        TimeZoneInfo zone = c.Options.Zone;
        var items = new List<KeyValuePair<string, string>>
        {
            new(c.T("CaseNumber"), info.Number),
            new(c.T("CaseName"), info.Name),
            new(c.T("Investigator"), info.Investigator),
            new(c.T("Organization"), info.Organization),
            new(c.T("Description"), info.Description),
            new(c.T("CreatedAt"), ReportTime.Both(info.CreatedUtc, zone)),
            new(c.T("GeneratedAt"), ReportTime.Both(c.Input.NowUtc, zone)),
            new(c.T("TimeZone"), ForensicTimeFormat.ZoneName(zone)),
            new(c.T("GeneratedBy"), $"{c.Input.AppName} {c.Input.AppVersion}"),
        };
        return new ReportSection(ReportSections.Cover, c.T("Section_cover"), [new ReportKeyValues(items.Where(i => i.Value.Length > 0).ToList())]);
    }

    private static string KindName(ReportContext c, EvidenceKind kind) => c.T("Kind_" + kind);

    private static ReportSection EvidenceSection(ReportContext c)
    {
        TimeZoneInfo zone = c.Options.Zone;
        string[] columns =
        [
            c.T("Evidence_Id"), c.T("Evidence_Description"), c.T("Evidence_Type"), c.T("Evidence_Path"), c.T("Evidence_Model"), c.T("Evidence_Serial"),
            c.T("Evidence_Capacity"), c.T("Evidence_Sectors"), c.T("Evidence_SectorSize"), c.T("Evidence_Hashes"), c.T("Evidence_Verification"),
            c.T("Evidence_Added"), c.T("Evidence_AddedLocal"),
        ];
        IEnumerable<IReadOnlyList<string>> rows = c.Input.Evidence.Select<EvidenceItem, IReadOnlyList<string>>(e =>
        [
            e.Id, e.Description, KindName(c, e.Kind), e.Path, e.Model ?? string.Empty, e.Serial ?? string.Empty,
            e.CapacityBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, e.SectorCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            e.SectorSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            e.Hashes.Count == 0 ? c.T("Hash_NotComputed") : string.Join('\n', e.Hashes.Select(h => $"{h.Algorithm}: {h.Value}")),
            c.T("Verification_" + (e.Verification ?? "none")), ReportTime.Utc(e.AddedUtc), ReportTime.Local(e.AddedUtc, zone),
        ]);
        return new ReportSection(ReportSections.Evidence, c.T("Section_evidence"), [new ReportTable(columns, rows)]);
    }

    private static ReportSection BookmarkSection(ReportContext c)
    {
        string[] columns = [c.T("Bookmark_Document"), c.T("Bookmark_Name"), c.T("Bookmark_Offset"), c.T("Bookmark_Lba"), c.T("Bookmark_Comment")];
        IReadOnlyList<ReportBookmark> bookmarks = c.Input.Bookmarks;
        int dump = Math.Clamp(c.Options.DumpBytes, 0, 4096);
        var blocks = new List<ReportBlock>
        {
            new ReportTable(columns, bookmarks.Select<ReportBookmark, IReadOnlyList<string>>(b =>
            [
                b.Document, b.Name, OffsetText(b.Offset), b.Lba?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, b.Comment,
            ])),
        };
        if (dump > 0)
        {
            foreach (ReportBookmark b in bookmarks)
            {
                blocks.Add(new ReportDump($"{b.Name} ({OffsetText(b.Offset)})", b.Offset, b.Dump.Length > dump ? b.Dump[..dump] : b.Dump));
            }
        }

        return new ReportSection(ReportSections.Bookmarks, c.T("Section_bookmarks"), blocks);
    }

    /// <summary>オフセットは 16 進と 10 進の両方 (<c>0x1BE (446)</c>)。</summary>
    public static string OffsetText(long offset) => FormattableString.Invariant($"0x{offset:X} ({offset})");

    private static ReportSection? CarvingSection(ReportContext c)
    {
        var blocks = new List<ReportBlock>();
        int index = 0;
        foreach (string manifest in c.Input.CarvingManifests.Where(File.Exists))
        {
            index++;
            long total = CountRows(manifest);
            blocks.Add(new ReportKeyValues([new KeyValuePair<string, string>(c.T("Carving_Total"), total.ToString(CultureInfo.InvariantCulture))]));
            string[] columns =
            [
                c.T("Carving_Number"), c.T("Carving_Type"), c.T("Carving_Offset"), c.T("Carving_Lba"), c.T("Carving_Length"), c.T("Carving_Method"), "MD5", "SHA-256",
                c.T("Carving_Output"),
            ];
            blocks.Add(new ReportTable(columns, ReadManifest(manifest))
            {
                MaxRows = ReportTable.DefaultMaxRows,
                AttachmentName = index == 1 ? "carving-manifest.csv" : $"carving-manifest-{index}.csv",
                TotalRows = total,
                AttachmentSource = manifest,
            });
        }

        return blocks.Count == 0 ? null : new ReportSection(ReportSections.Carving, c.T("Section_carving"), blocks);
    }

    private static long CountRows(string manifest)
    {
        long count = 0;
        bool first = true;
        foreach (string line in File.ReadLines(manifest))
        {
            if (first)
            {
                first = false;
                continue;
            }

            if (line.Length > 0 && line[0] != '#')
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<IReadOnlyList<string>> ReadManifest(string manifest)
    {
        bool first = true;
        foreach (string line in File.ReadLines(manifest))
        {
            if (first)
            {
                first = false;
                continue;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            yield return CsvRow.Parse(line);
        }
    }

    private static ReportSection? AuditSection(ReportContext c)
    {
        if (c.Input.AuditLogPath is not { } path || !File.Exists(path))
        {
            return null;
        }

        var reader = new AuditLogReader(path);
        TimeZoneInfo zone = c.Options.Zone;
        string[] columns =
        [
            c.T("Audit_Seq"), c.T("Audit_TimeUtc"), c.T("Audit_TimeLocal"), c.T("Audit_User"), c.T("Audit_Computer"), c.T("Audit_Operation"),
            c.T("Audit_Evidence"), c.T("Audit_Result"), c.T("Audit_Message"),
        ];
        IEnumerable<IReadOnlyList<string>> Rows()
        {
            using (reader)
            {
                foreach (AuditEntry e in reader.Enumerate(c.Options.AuditFromUtc, c.Options.AuditToUtc))
                {
                    DateTime? t = e.TimeUtc;
                    string local = t is { } u ? ReportTime.Local(u, zone, 1) : string.Empty;
                    string detail = e.ParametersText.Length > 0 ? (e.Message.Length > 0 ? $"{e.Message} [{e.ParametersText}]" : e.ParametersText) : e.Message;
                    yield return [e.Seq.ToString(CultureInfo.InvariantCulture), e.Time, local, e.User, e.Computer, e.Operation, e.EvidenceId ?? string.Empty,
                        c.T("Result_" + AuditExporter.ResultName(e.Result)), detail];
                }
            }
        }

        return new ReportSection(ReportSections.Audit, c.T("Section_audit"), [new ReportTable(columns, Rows()) { TotalRows = reader.Count }]);
    }
}

/// <summary>CSV の 1 行の解析と書き出し (ダブルクォートで囲んだ値を扱う)。</summary>
internal static class CsvRow
{
    public static string[] Parse(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        cells.Add(sb.ToString());
        return [.. cells];
    }
}
