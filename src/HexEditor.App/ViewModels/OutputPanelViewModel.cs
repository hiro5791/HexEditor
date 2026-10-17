using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.ViewModels;

/// <summary>出力パネルの 1 行 (AUTO-24 の仕様 2・3): 時刻、出力元、種類 (色とアイコン)、本文、エラーの位置のリンク。</summary>
public sealed class OutputRow
{
    public OutputRow(ScriptOutputLine line)
    {
        Line = line;
    }

    public ScriptOutputLine Line { get; }

    public string TimeText => Line.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>出力元の表示名 (「スクリプト: 名前」)。</summary>
    public string ChannelText => OutputPanelViewModel.ChannelDisplay(Line.Channel);

    public string Text => Line.Text;

    /// <summary>種類のアイコン (Segoe Fluent Icons)。色に加えてアイコンで区別する (仕様 2)。</summary>
    public string Glyph => Line.Kind switch
    {
        ScriptOutputKind.Error => "",
        ScriptOutputKind.Warning => "",
        _ => "",
    };

    /// <summary>種類の名前 (アイコンの UI オートメーションの名前)。</summary>
    public string KindName => Line.Kind switch
    {
        ScriptOutputKind.Error => Loc.Get("Output_KindError"),
        ScriptOutputKind.Warning => Loc.Get("Output_KindWarning"),
        _ => Loc.Get("Output_KindInfo"),
    };

    /// <summary>ファイル名と行番号のリンク (エラーの行だけ。クリックでスクリプトエディタの該当行を開く。仕様 3)。</summary>
    public string LinkText => HasLink ? $"{Path.GetFileName(Line.SourcePath)}:{Line.Line.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

    public bool HasLink => Line.SourcePath is not null && Line.Line > 0 && Line.Kind != ScriptOutputKind.Info;

    public Microsoft.UI.Xaml.Visibility LinkVisibility => HasLink ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>行の折り返し (出力パネルの設定。アプリ全体で 1 つ)。</summary>
    public static bool WrapEnabled { get; set; }

    public Microsoft.UI.Xaml.TextWrapping Wrapping => WrapEnabled ? Microsoft.UI.Xaml.TextWrapping.Wrap : Microsoft.UI.Xaml.TextWrapping.NoWrap;

    public Microsoft.UI.Xaml.TextTrimming Trimming => WrapEnabled ? Microsoft.UI.Xaml.TextTrimming.None : Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis;

    public string AccessibleName => $"{TimeText} {KindName} {ChannelText} {Text}";

    /// <summary>種類の色 (テーマのブラシのキー)。</summary>
    public string BrushKey => Line.Kind switch
    {
        ScriptOutputKind.Error => "SystemFillColorCriticalBrush",
        ScriptOutputKind.Warning => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    };

    public Microsoft.UI.Xaml.Media.Brush Brush => (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[BrushKey];

    /// <summary>1 行のテキスト (コピー・保存用)。</summary>
    public string ToLineText() => $"{TimeText}\t{ChannelText}\t{KindName}\t{Text}";
}

/// <summary>1 回の更新で行をまとめて入れ替えられる一覧 (大量の出力のときは 1 回の通知にする)。</summary>
public sealed class OutputRowCollection : ObservableCollection<OutputRow>
{
    public void ReplaceAll(IEnumerable<OutputRow> rows)
    {
        Items.Clear();
        foreach (OutputRow row in rows)
        {
            Items.Add(row);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// 出力パネル (AUTO-24)。スクリプト・マクロ・フックの出力とエラーを出力元ごとに分けて見せる。書く側の速さに関わらず、表示は
/// まとめて更新する (最大 10 回 / 秒。仕様の「巨大ファイル・長時間処理」)。保持する行数は設定 <c>scripts.output.maxLines</c>。
/// </summary>
public sealed partial class OutputPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>表示の更新の間隔 (最大 10 回 / 秒)。</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>この行数までは 1 行ずつ追加の通知を出し、超えたら 1 回の通知で入れ替える。</summary>
    private const int IncrementalLimit = 200;

    private readonly ScriptOutputLog _log;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private readonly Lock _lock = new();
    private readonly List<ScriptOutputLine> _pending = [];
    private readonly List<OutputRow> _all = [];
    private bool _clearPending;
    private bool _disposed;

    /// <summary>「すべて」の出力元。</summary>
    public static string AllChannels { get; } = string.Empty;

    public OutputPanelViewModel(ScriptOutputLog log, DispatcherQueue queue)
    {
        _log = log;
        _queue = queue;
        foreach (ScriptOutputLine line in log.Lines)
        {
            _all.Add(new OutputRow(line));
        }

        Channels.Add(new ChannelItem(AllChannels, Loc.Get("Output_AllChannels")));
        RebuildChannels();
        Rows.ReplaceAll(Visible());
        _timer = queue.CreateTimer();
        _timer.Interval = UpdateInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Flush();
        _log.LinesAdded += OnLineAdded;
        _log.Cleared += OnCleared;
        _timer.Start();
    }

    /// <summary>出力元の選択肢。</summary>
    public sealed record ChannelItem(string Channel, string Display)
    {
        public override string ToString() => Display;
    }

    public ObservableCollection<ChannelItem> Channels { get; } = [];

    public OutputRowCollection Rows { get; } = [];

    /// <summary>選んでいる出力元 (空は「すべて」)。</summary>
    [ObservableProperty]
    public partial string SelectedChannel { get; set; } = string.Empty;

    /// <summary>絞り込みの文字列 (本文・出力元に含まれる行だけを出す)。</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>行の折り返し (仕様 5)。</summary>
    [ObservableProperty]
    public partial bool Wrap { get; set; }

    /// <summary>保持する行数 (AUTO-24 の仕様 4)。</summary>
    public int Capacity
    {
        get => _log.Capacity;
        set
        {
            _log.Capacity = value;
            lock (_lock)
            {
                TrimAll();
            }

            Refresh();
        }
    }

    /// <summary>表示している行数 (絞り込みの後)。</summary>
    public int VisibleCount => Rows.Count;

    /// <summary>行の数 (絞り込みの前)。</summary>
    public int TotalCount => _all.Count;

    /// <summary>一覧が更新された (末尾へのスクロールのため)。引数は追加した行の数。</summary>
    public event EventHandler<int>? Updated;

    /// <summary>出力元の内部名 (<c>script:名前</c>) を表示名 (「スクリプト: 名前」) にする。</summary>
    public static string ChannelDisplay(string channel)
    {
        if (channel.StartsWith(ScriptRun.ChannelPrefix, StringComparison.Ordinal))
        {
            return Loc.Format("Output_ChannelScript", channel[ScriptRun.ChannelPrefix.Length..]);
        }

        if (channel.StartsWith("plugin:", StringComparison.Ordinal))
        {
            return Loc.Format("Output_ChannelPlugin", channel["plugin:".Length..]);
        }

        return channel;
    }

    partial void OnSelectedChannelChanged(string value) => Refresh();

    partial void OnFilterChanged(string value) => Refresh();

    partial void OnWrapChanged(bool value)
    {
        OutputRow.WrapEnabled = value;
        Refresh();
    }

    private void OnLineAdded(object? sender, ScriptOutputLine line)
    {
        lock (_lock)
        {
            _pending.Add(line);
        }
    }

    private void OnCleared(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _pending.Clear();
            _clearPending = true;
        }
    }

    /// <summary>出力元の一覧を作り直す (新しい出力元が現れたとき)。</summary>
    private void RebuildChannels()
    {
        string selected = SelectedChannel;
        var channels = _all.Select(r => r.Line.Channel).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        for (int i = Channels.Count - 1; i >= 1; i--)
        {
            if (!channels.Contains(Channels[i].Channel))
            {
                Channels.RemoveAt(i);
            }
        }

        foreach (string channel in channels.Where(c => Channels.All(i => i.Channel != c)))
        {
            Channels.Add(new ChannelItem(channel, ChannelDisplay(channel)));
        }

        if (selected != SelectedChannel)
        {
            SelectedChannel = selected;
        }
    }

    private bool Matches(OutputRow row)
    {
        if (SelectedChannel.Length > 0 && row.Line.Channel != SelectedChannel)
        {
            return false;
        }

        return Filter.Length == 0 || row.Text.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)
            || row.ChannelText.Contains(Filter, StringComparison.CurrentCultureIgnoreCase);
    }

    private IEnumerable<OutputRow> Visible() => SelectedChannel.Length == 0 && Filter.Length == 0 ? _all : _all.Where(Matches);

    private void TrimAll()
    {
        int excess = _all.Count - Capacity;
        if (excess > 0)
        {
            _all.RemoveRange(0, excess);
        }
    }

    /// <summary>絞り込みや出力元の変更で、表示する行を作り直す。</summary>
    public void Refresh()
    {
        Rows.ReplaceAll(Visible());
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(TotalCount));
        Updated?.Invoke(this, 0);
    }

    /// <summary>書かれた行を表示に反映する (10 回 / 秒の更新で呼ぶ。テストでは直接呼ぶ)。</summary>
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        List<ScriptOutputLine> batch;
        bool clear;
        lock (_lock)
        {
            if (_pending.Count == 0 && !_clearPending)
            {
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
            clear = _clearPending;
            _clearPending = false;
        }

        if (clear)
        {
            _all.Clear();
        }

        var added = new List<OutputRow>(batch.Count);
        foreach (ScriptOutputLine line in batch)
        {
            added.Add(new OutputRow(line));
        }

        _all.AddRange(added);
        int before = _all.Count;
        TrimAll();
        bool trimmed = before != _all.Count;
        bool newChannel = added.Any(r => Channels.All(c => c.Channel != r.Line.Channel));
        if (newChannel || trimmed || clear)
        {
            RebuildChannels();
        }

        if (clear || trimmed || added.Count > IncrementalLimit)
        {
            Rows.ReplaceAll(Visible());
        }
        else
        {
            foreach (OutputRow row in added.Where(Matches))
            {
                Rows.Add(row);
            }
        }

        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(TotalCount));
        Updated?.Invoke(this, added.Count);
    }

    /// <summary>表示している行のテキスト (「すべてコピー」・「ファイルに保存」)。</summary>
    public string AllText()
    {
        var sb = new StringBuilder();
        foreach (OutputRow row in Rows)
        {
            sb.Append(row.ToLineText()).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>クリア (出力の記録ごと消す)。</summary>
    public void Clear()
    {
        _log.Clear();
        Flush();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _log.LinesAdded -= OnLineAdded;
        _log.Cleared -= OnCleared;
    }
}
