using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Time;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 監査ログのパネル (12 の FOR-04 の仕様 7)。ログを結果一覧で表示する (行の位置だけを索引にして必要な行を読むので、100 万行を超えても仮想化して表示できる)。
/// 日時の列は UTC とローカル (設定が「UTC」ならローカルの列は出さない)。「検証」で連鎖の切れを確かめ、CSV / JSON へエクスポートできる。
/// </summary>
public sealed class AuditLogPanel : UserControl, IPanelContent
{
    private readonly MainWindow _window;
    private readonly VirtualTable _table = new("AuditLog_List");
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly Border _verifyBox = new() { BorderThickness = new Thickness(2), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(12, 0, 12, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _verifyText = new() { TextWrapping = TextWrapping.Wrap };
    private AuditLogReader? _reader;
    private string? _readerPath;
    private bool _refreshQueued;

    public AuditLogPanel(MainWindow window)
    {
        _window = window;
        AutomationProperties.SetAutomationId(_summary, "AuditLog_Summary");
        AutomationProperties.SetAutomationId(_empty, "AuditLog_Empty");
        AutomationProperties.SetAutomationId(_verifyBox, "AuditLog_VerifyResult");
        AutomationProperties.SetLiveSetting(_verifyText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _verifyBox.Child = _verifyText;

        Button Make(string id, string key, string command)
        {
            var b = new Button { Content = Loc.Get(key) };
            AutomationProperties.SetAutomationId(b, id);
            b.Click += async (_, _) => await window.Commands.ExecuteAsync(command);
            return b;
        }

        Button verify = Make("AuditLog_Verify", "AuditLog_Verify", "forensics.audit.verify");
        var csv = new Button { Content = Loc.Get("AuditLog_ExportCsv") };
        var json = new Button { Content = Loc.Get("AuditLog_ExportJson") };
        AutomationProperties.SetAutomationId(csv, "AuditLog_ExportCsv");
        AutomationProperties.SetAutomationId(json, "AuditLog_ExportJson");
        csv.Click += async (_, _) => await window.ExportAuditAsync(json: false);
        json.Click += async (_, _) => await window.ExportAuditAsync(json: true);
        var bar = new Grid { Padding = new Thickness(12, 6, 12, 6), ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        bar.Children.Add(_summary);
        foreach ((Button button, int column) in new[] { (verify, 1), (csv, 2), (json, 3) })
        {
            Grid.SetColumn(button, column);
            bar.Children.Add(button);
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_verifyBox, 1);
        Grid.SetRow(_table, 2);
        Grid.SetRow(_empty, 2);
        root.Children.Add(bar);
        root.Children.Add(_verifyBox);
        root.Children.Add(_table);
        root.Children.Add(_empty);
        Content = root;

        _table.RowProvider = RowOf;
        ForensicsService.CaseChanged += OnCaseChanged;
        ForensicsService.TimeDisplayChanged += OnCaseChanged;
        ForensicsService.Audit.Recorded += OnRecorded;
        Unloaded += (_, _) =>
        {
            ForensicsService.CaseChanged -= OnCaseChanged;
            ForensicsService.TimeDisplayChanged -= OnCaseChanged;
            ForensicsService.Audit.Recorded -= OnRecorded;
            _reader?.Dispose();
            _reader = null;
        };
        Loaded += (_, _) => Rebuild();
        Rebuild();
    }

    private void OnCaseChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void OnRecorded(object? sender, AuditEntry e)
    {
        if (_refreshQueued)
        {
            return;
        }

        // 書き込みのスレッドから呼ばれる。連続する記録は 1 回の更新にまとめる。
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>テスト用: 行の数。</summary>
    internal long RowCount => _reader?.Count ?? 0;

    internal IReadOnlyList<string> ColumnHeaders { get; private set; } = [];

    internal IReadOnlyList<string> Cells(long index) => RowOf(index);

    /// <summary>テスト用: 検証の結果の文。</summary>
    internal string VerifyResultText => _verifyBox.Visibility == Visibility.Visible ? _verifyText.Text : string.Empty;

    internal bool VerifyResultIsError { get; private set; }

    /// <summary>ケースが変わった: 読み出しを作り直し、列を作る。</summary>
    private void Rebuild()
    {
        string? path = ForensicsService.Cases.CurrentCase?.AuditPath;
        if (path != _readerPath)
        {
            _reader?.Dispose();
            _reader = null;
            _readerPath = path;
            _verifyBox.Visibility = Visibility.Collapsed;
            if (path is not null && File.Exists(path))
            {
                try
                {
                    _reader = new AuditLogReader(path);
                }
                catch (IOException)
                {
                    _reader = null;
                }
            }
        }

        ForensicTimeContext time = ForensicsService.TimeContext;
        var columns = new List<VirtualColumn> { new(Loc.Get("AuditLog_Col_Seq"), 60, true) };
        foreach (TimeColumn column in time.Columns)
        {
            columns.Add(new VirtualColumn(Loc.Get(column == TimeColumn.Utc ? "AuditLog_Col_TimeUtc" : "AuditLog_Col_TimeLocal"), 240, true));
        }

        columns.AddRange([new(Loc.Get("AuditLog_Col_User"), 110), new(Loc.Get("AuditLog_Col_Operation"), 150, true), new(Loc.Get("AuditLog_Col_Evidence"), 70, true),
            new(Loc.Get("AuditLog_Col_Result"), 90), new(Loc.Get("AuditLog_Col_Message"), 400)]);
        ColumnHeaders = [.. columns.Select(c => c.Header)];
        _table.SetColumns(columns);
        Refresh();
    }

    public void Refresh()
    {
        if (_reader is null && ForensicsService.Cases.CurrentCase is { } c && File.Exists(c.AuditPath))
        {
            try
            {
                _reader = new AuditLogReader(c.AuditPath);
                _readerPath = c.AuditPath;
            }
            catch (IOException)
            {
            }
        }

        _reader?.Refresh();
        long count = _reader?.Count ?? 0;
        bool follow = _table.Count == 0 || _table.TopIndex + _table.VisibleRows >= _table.Count;
        _table.Refresh(count);
        if (follow && count > 0 && _table.SelectedIndex < 0)
        {
            _table.ScrollIntoView(count - 1);
        }

        _summary.Text = ForensicsService.Cases.CurrentCase is null ? string.Empty : Loc.Format("AuditLog_Summary", count);
        _empty.Text = Loc.Get(ForensicsService.Cases.CurrentCase is null ? "AuditLog_NoCase" : "AuditLog_None");
        bool any = count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _table.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<string> RowOf(long index)
    {
        if (_reader is null || index < 0 || index >= _reader.Count)
        {
            return [];
        }

        ForensicTimeContext time = ForensicsService.TimeContext;
        AuditEntry? e = _reader.Read(index);
        if (e is null)
        {
            // 形式が違う行 (改ざん・途中で切れた行): 生の行を示す。
            return [(index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), .. time.Columns.Select(_ => string.Empty), string.Empty, string.Empty, string.Empty, string.Empty,
                Loc.Get("AuditLog_Malformed")];
        }

        ForensicTimestamp ts = e.TimeUtc is { } t ? ForensicTimestamp.FromUtc(t, 1) : ForensicTimestamp.Invalid(e.Time);
        var cells = new List<string> { e.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (TimeColumn column in time.Columns)
        {
            cells.Add(column == TimeColumn.Utc && e.TimeUtc is not null ? e.Time : time.Cell(ts, column, Loc.Get("Time_Invalid")));
        }

        string detail = e.ParametersText.Length > 0 ? (e.Message.Length > 0 ? $"{e.Message} [{e.ParametersText}]" : e.ParametersText) : e.Message;
        cells.AddRange([e.User, e.Operation, e.EvidenceId ?? string.Empty, Loc.Get("AuditLog_Result_" + AuditExporter.ResultName(e.Result)), detail]);
        return cells;
    }

    /// <summary>検証の結果を示す。連鎖の切れがあれば赤枠と文で示す (色だけに頼らない)。</summary>
    public void ShowVerifyResult(AuditVerifyResult result)
    {
        VerifyResultIsError = !result.IsIntact;
        _verifyBox.Visibility = Visibility.Visible;
        _verifyBox.BorderBrush = (Brush)Application.Current.Resources[result.IsIntact ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        _verifyText.Text = result.IsIntact
            ? Loc.Format("AuditLog_VerifyOk", result.LinesChecked)
            : Loc.Format("AuditLog_VerifyBroken", string.Join(", ", result.Breaks.Take(20).Select(b => b.Line)) + (result.Breaks.Count > 20 || result.Truncated ? ", …" : string.Empty));
        if (!result.IsIntact && result.FirstBrokenLine is { } line && line <= _table.Count)
        {
            _table.Select(line - 1);
        }
    }

    public bool FocusContent() => (_table.Visibility == Visibility.Visible ? _table : (Control)this).Focus(FocusState.Programmatic);
}
