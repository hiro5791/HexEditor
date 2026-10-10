using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Statistics;

namespace HexEditor.App.ViewModels;

/// <summary>判定の候補の一覧の 1 行 (ANA-17 の仕様 3)。</summary>
public sealed class FileTypeCandidateViewModel(FileTypeCandidate candidate, long baseOffset)
{
    public FileTypeCandidate Candidate { get; } = candidate;

    public long BaseOffset { get; } = baseOffset;

    public string Name => Candidate.Name;

    public string Mime => Candidate.Mime;

    public string Extensions => string.Join(", ", Candidate.Extensions.Select(e => "." + e));

    public string Confidence => Candidate.Confidence.ToString("N0", CultureInfo.CurrentCulture) + "%";

    public string Source => Candidate.Source == FileTypeDatabase.BuiltInSource ? Loc.Get("FileType_Source_BuiltIn") : Candidate.Source;

    public override string ToString() => Loc.Format("FileType_CandidateName", Name, Mime, Confidence);
}

/// <summary>一致した条件の位置 (選んだ候補の詳細。その位置へ移動するリンク)。</summary>
public sealed class FileTypeMatchViewModel(long offset, int length)
{
    public long OffsetValue { get; } = offset;

    public string Offset => "0x" + OffsetValue.ToString("X", CultureInfo.InvariantCulture);

    public string Length => Loc.Format("Stats_Bytes", length.ToString("N0", CultureInfo.CurrentCulture));

    public override string ToString() => Loc.Format("FileType_MatchName", Offset, Length);
}

/// <summary>埋め込まれた形式の結果の 1 行 (ANA-17 の仕様 6)。</summary>
public sealed class EmbeddedFormatViewModel(EmbeddedFormat format)
{
    public EmbeddedFormat Format { get; } = format;

    public string Offset => "0x" + Format.Offset.ToString("X", CultureInfo.InvariantCulture);

    public string Name => Format.Name;

    public string Mime => Format.Mime;

    public string Confidence => Format.Confidence.ToString("N0", CultureInfo.CurrentCulture) + "%";

    public override string ToString() => Loc.Format("FileType_EmbeddedName", Offset, Name, Confidence);
}

/// <summary>
/// ファイル形式の判定 (ANA-17)。ドキュメントを開いたときの自動判定 (先頭と末尾の 64 KB だけを読む)、「ファイル形式」パネルの候補の一覧、
/// 「ここからの形式を判定」、「埋め込まれた形式を探す」。データベースはアプリ全体で 1 つ (<see cref="Detector"/>)。
/// </summary>
public sealed partial class FileTypeViewModel(OperationCenter operations) : ObservableObject
{
    /// <summary>自動判定の設定 (ANA-17 の仕様 4: 設定でオフにできる)。</summary>
    public const string AutoDetectKey = "analysis.fileType.autoDetect";

    private static readonly ConditionalWeakTable<Document, FileTypeReport> Reports = new();
    private DocumentViewModel? _target;
    private LongRunningOperation? _embedded;

    /// <summary>判定に使う (内蔵 + 利用者の magic/。起動時に設定する)。</summary>
    public static FileTypeDetector Detector { get; set; } = new(FileTypeDatabase.BuiltIn);

    /// <summary>開いたときの自動判定が終わった (ステータスバーの表示を更新する)。</summary>
    public static event EventHandler<Document>? Detected;

    /// <summary>ドキュメントの自動判定の結果 (まだなら null)。</summary>
    public static FileTypeReport? ReportOf(Document document) => Reports.TryGetValue(document, out FileTypeReport? r) ? r : null;

    /// <summary>自動判定の読んだバイト数 (テスト用。TC-ANA-17-03)。</summary>
    public static long LastAutoBytesRead { get; private set; }

    /// <summary>
    /// 開いたときの自動判定 (ANA-17 の仕様 4)。バックグラウンドで先頭 64 KB と末尾 64 KB だけを読み、開く処理を待たせない。
    /// </summary>
    public static void DetectInBackground(Document document, string? path)
    {
        DocumentSnapshot snapshot = document.Current;
        string? extension = path is null ? null : Path.GetExtension(path);
        _ = Task.Run(() =>
        {
            try
            {
                var data = new HeadTailMagicData(snapshot);
                FileTypeReport report = Detector.Detect(data, extension);
                LastAutoBytesRead = data.BytesRead;
                Reports.AddOrUpdate(document, report);
                Detected?.Invoke(null, document);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                AppLog.Warning("File type detection failed: " + e.Message);
            }
        });
    }

    /// <summary>ステータスバーの形式表示 (最も確度の高い形式名。なければテキストか「不明な形式 (データ)」)。</summary>
    public static string StatusText(FileTypeReport? report) => report switch
    {
        null => string.Empty,
        { Best: { } best } => best.Name,
        { Text: { } text } => Loc.Get("FileType_Text_" + text),
        _ => Loc.Get("FileType_Unknown"),
    };

    /// <summary>ドキュメント上の位置へ移動する。</summary>
    public Action<long>? GoTo { get; set; }

    /// <summary>共通の注釈レイヤー (INSP-32) に埋め込まれた形式を渡す口。null なら渡さない。</summary>
    public IAnnotationSink? Annotations { get; set; }

    public ObservableCollection<FileTypeCandidateViewModel> Candidates { get; } = [];

    public ObservableCollection<FileTypeMatchViewModel> Matches { get; } = [];

    public ObservableCollection<EmbeddedFormatViewModel> Embedded { get; } = [];

    [ObservableProperty]
    public partial FileTypeCandidateViewModel? SelectedCandidate { get; set; }

    /// <summary>判定の結果の欄 (形式名、または「不明な形式」、拡張子の食い違い)。</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>「拡張子と内容が一致しません」(ANA-17 の仕様 8)。</summary>
    [ObservableProperty]
    public partial string MismatchText { get; set; } = string.Empty;

    /// <summary>判定の基準の位置 (「ここからの形式を判定」では選択範囲の先頭)。</summary>
    [ObservableProperty]
    public partial string BaseText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsSearching { get; set; }

    public bool IsIdle => !IsSearching;

    [ObservableProperty]
    public partial string EmbeddedStatus { get; set; } = string.Empty;

    public DocumentViewModel? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value))
            {
                return;
            }

            _embedded?.Cancel();
            _target = value;
            Embedded.Clear();
            EmbeddedStatus = string.Empty;
            OnPropertyChanged();
            ShowReport(value is null ? null : ReportOf(value.Document), 0);
        }
    }

    /// <summary>ドキュメント全体を判定し直す (先頭と末尾の 64 KB)。</summary>
    public async Task DetectAsync()
    {
        if (_target is not { } doc)
        {
            return;
        }

        DocumentSnapshot snapshot = doc.Document.Current;
        string? extension = doc.FilePath is null ? null : Path.GetExtension(doc.FilePath);
        FileTypeReport report = await Task.Run(() => Detector.Detect(new HeadTailMagicData(snapshot), extension));
        Reports.AddOrUpdate(doc.Document, report);
        if (ReferenceEquals(_target, doc))
        {
            ShowReport(report, 0);
        }
    }

    /// <summary>「ここからの形式を判定」: カーソル (または選択範囲の先頭) をオフセット 0 とみなす (ANA-17 の仕様 5)。</summary>
    public async Task DetectHereAsync()
    {
        if (_target is not { } doc)
        {
            return;
        }

        long at = doc.Editor.HasSelection ? doc.Editor.SelectionStart : doc.Editor.Cursor;
        DocumentSnapshot snapshot = doc.Document.Current;
        FileTypeReport report = await Task.Run(() => Detector.Detect(new HeadTailMagicData(snapshot, at)));
        if (ReferenceEquals(_target, doc))
        {
            ShowReport(report, at);
        }
    }

    /// <summary>自動判定の結果が出た (表示中のドキュメントなら表示する)。</summary>
    public void Refresh()
    {
        if (_target is { } doc && ReportOf(doc.Document) is { } report)
        {
            ShowReport(report, 0);
        }
    }

    private void ShowReport(FileTypeReport? report, long baseOffset)
    {
        Candidates.Clear();
        Matches.Clear();
        MismatchText = string.Empty;
        BaseText = baseOffset == 0 ? string.Empty : Loc.Format("FileType_Base", "0x" + baseOffset.ToString("X", CultureInfo.InvariantCulture));
        if (report is null)
        {
            Summary = _target is null ? string.Empty : Loc.Get("FileType_Detecting");
            return;
        }

        foreach (FileTypeCandidate c in report.Candidates)
        {
            Candidates.Add(new FileTypeCandidateViewModel(c, baseOffset));
        }

        Summary = StatusText(report);
        MismatchText = report.ExtensionMismatch && baseOffset == 0 ? Loc.Get("FileType_Mismatch") : string.Empty;
        SelectedCandidate = Candidates.FirstOrDefault();
    }

    partial void OnSelectedCandidateChanged(FileTypeCandidateViewModel? value)
    {
        Matches.Clear();
        if (value is null)
        {
            return;
        }

        foreach ((long offset, int length) in value.Candidate.Matches)
        {
            Matches.Add(new FileTypeMatchViewModel(value.BaseOffset + offset, length));
        }
    }

    /// <summary>「埋め込まれた形式を探す」(ANA-17 の仕様 6): 長時間処理。キャンセルしたら見つかった分を残さない。</summary>
    public async Task FindEmbeddedAsync(IReadOnlyList<HashRange> ranges)
    {
        if (_target is not { } doc)
        {
            return;
        }

        _embedded?.Cancel();
        Embedded.Clear();
        EmbeddedStatus = string.Empty;
        IsSearching = true;
        DocumentSnapshot snapshot = doc.Document.Current;
        try
        {
            IReadOnlyList<EmbeddedFormat> found = await operations.RunAsync(Loc.Get("Operation_FindEmbedded"), OperationKind.ReadOnly, doc.Document, null, op =>
            {
                _embedded = op;
                return Task.FromResult(Detector.FindEmbedded(snapshot, ranges, op));
            });
            if (!ReferenceEquals(_target, doc))
            {
                return;
            }

            foreach (EmbeddedFormat f in found)
            {
                Embedded.Add(new EmbeddedFormatViewModel(f));
            }

            EmbeddedStatus = Loc.Format("FileType_EmbeddedCount", found.Count.ToString("N0", CultureInfo.CurrentCulture));
            Annotations?.SetAnnotations(doc.Document, AnalysisAnnotationSources.EmbeddedFormats,
                [.. found.Select(f => new AnalysisAnnotation(f.Offset, 1, f.Name, "format." + f.Mime))]);
        }
        catch (OperationCanceledException)
        {
            EmbeddedStatus = Loc.Get("Stats_Cancelled");
        }
        finally
        {
            IsSearching = false;
        }
    }

    public void Cancel() => _embedded?.Cancel();
}
