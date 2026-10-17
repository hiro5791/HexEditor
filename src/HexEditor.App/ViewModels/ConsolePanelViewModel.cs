using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using HexEditor.Core.Scripting.Repl;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.ViewModels;

public enum ConsoleEntryKind
{
    Input,
    Result,
    Error,
    Output,
    Info,
}

/// <summary>展開できるツリーの節 (オブジェクトのプロパティ・配列の要素)。</summary>
public sealed class ConsoleNode
{
    public ConsoleNode(string name, ReplValue value)
    {
        Name = name;
        Text = value.Text.Contains('\n', StringComparison.Ordinal) ? value.Type : value.Display;
        foreach (ReplProperty child in value.Children)
        {
            Children.Add(new ConsoleNode(child.Name, child.Value));
        }
    }

    public string Name { get; }

    public string Text { get; }

    public string Title => $"{Name}: {Text}";

    public ObservableCollection<ConsoleNode> Children { get; } = [];
}

/// <summary>コンソールの結果の履歴の 1 行 (入力の写し、結果、エラー、出力)。</summary>
public sealed class ConsoleEntry
{
    public ConsoleEntry(ConsoleEntryKind kind, string text, ReplValue? value = null)
    {
        Kind = kind;
        Value = value;
        Text = text;
        if (value is { Expandable: true })
        {
            foreach (ReplProperty child in value.Children)
            {
                Nodes.Add(new ConsoleNode(child.Name, child.Value));
            }
        }
    }

    public ConsoleEntryKind Kind { get; }

    public ReplValue? Value { get; }

    public string Text { get; }

    public ObservableCollection<ConsoleNode> Nodes { get; } = [];

    /// <summary>展開できる結果 (オブジェクト・配列)。</summary>
    public bool HasTree => Nodes.Count > 0;

    public Visibility TreeVisibility => HasTree ? Visibility.Visible : Visibility.Collapsed;

    public string Prefix => Kind switch
    {
        ConsoleEntryKind.Input => "›",
        ConsoleEntryKind.Error => "✖",
        ConsoleEntryKind.Result => "←",
        _ => " ",
    };

    public Brush Brush => (Brush)Application.Current.Resources[Kind switch
    {
        ConsoleEntryKind.Error => "SystemFillColorCriticalBrush",
        ConsoleEntryKind.Input => "TextFillColorSecondaryBrush",
        ConsoleEntryKind.Info => "TextFillColorSecondaryBrush",
        _ => "TextFillColorPrimaryBrush",
    }];

    public string AccessibleName => $"{Kind} {Text}";
}

/// <summary>
/// スクリプトコンソール (AUTO-23)。1 回ごとの入力を実行し、結果を整形して見せる。JavaScript は入力をまたいで変数を保つ (セッション)。
/// 実行は他のスクリプトと同じ仕組み (<see cref="ScriptingService"/>) で、入力 1 回が 1 つの実行 (Undo の単位・実行時間の上限・許可の
/// 対象)。デバッグで止まっている間の入力は、止まっている位置のスコープで評価する (AUTO-21 の仕様 5)。
/// </summary>
public sealed partial class ConsolePanelViewModel : ObservableObject, IDisposable
{
    /// <summary>コンソールの実行の名前 (出力元は「スクリプト: console」)。</summary>
    public const string RunName = "console";

    private readonly DispatcherQueue _queue;
    private JsReplSession _session = new();
    private ScriptRun? _current;
    private bool _disposed;

    public ConsolePanelViewModel(DispatcherQueue queue)
    {
        _queue = queue;
        ScriptingService.Output.LinesAdded += OnOutput;
        Navigator = ScriptWorkspace.History.Navigate();
    }

    public ObservableCollection<ConsoleEntry> Entries { get; } = [];

    public ReplHistory.Navigator Navigator { get; private set; }

    /// <summary>言語 (JavaScript が既定。C# はコンポーネントがあるとき)。</summary>
    [ObservableProperty]
    public partial ScriptLanguage Language { get; set; } = ScriptLanguage.JavaScript;

    /// <summary>入力を実行中か。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>デバッグで止まっているときの評価の宛先。</summary>
    public JsDebugSession? DebugSession { get; set; }

    /// <summary>追加した (末尾へスクロールするため)。</summary>
    public event EventHandler? EntryAdded;

    public bool IsDebugPaused => DebugSession is { State: DebugState.Paused };

    private void OnOutput(object? sender, ScriptOutputLine line)
    {
        if (line.Channel != ScriptRun.ChannelPrefix + RunName)
        {
            return;
        }

        _queue.TryEnqueue(() => Add(new ConsoleEntry(line.Kind == ScriptOutputKind.Error ? ConsoleEntryKind.Error : ConsoleEntryKind.Output, line.Text)));
    }

    private void Add(ConsoleEntry entry)
    {
        if (_disposed)
        {
            return;
        }

        Entries.Add(entry);
        while (Entries.Count > 2000)
        {
            Entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>入力を実行する。結果とエラーは履歴に足す。</summary>
    public async Task SubmitAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || IsBusy)
        {
            return;
        }

        ScriptWorkspace.History.Add(code);
        Navigator.Reset();
        Add(new ConsoleEntry(ConsoleEntryKind.Input, code));
        if (DebugSession is { State: DebugState.Paused } debug && Language == ScriptLanguage.JavaScript)
        {
            try
            {
                ReplValue value = await debug.EvaluateAsync(code);
                Add(new ConsoleEntry(ConsoleEntryKind.Result, value.Display, value));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Add(new ConsoleEntry(ConsoleEntryKind.Error, ex.Message));
            }

            return;
        }

        IsBusy = true;
        try
        {
            ReplValue? value = null;
            ScriptRun run = ScriptingService.Start(new ScriptRunRequest
            {
                Code = code,
                Origin = ScriptOrigin.Console,
                DisplayName = RunName,
                Language = Language,
                Repl = Language == ScriptLanguage.JavaScript ? _session : null,
                OnReplResult = v => value = v,
                Quiet = true,
            });
            _current = run;
            ScriptRunResult result = await run.Completion;
            switch (result.Status)
            {
                case ScriptRunStatus.Succeeded when value is not null:
                    Add(new ConsoleEntry(ConsoleEntryKind.Result, value.Display, value));
                    break;
                case ScriptRunStatus.Failed:
                    Add(new ConsoleEntry(ConsoleEntryKind.Error, result.Message ?? Loc.Get("Console_Failed")));
                    break;
                case ScriptRunStatus.Stopped:
                    Add(new ConsoleEntry(ConsoleEntryKind.Info, Loc.Get("Console_Stopped")));
                    break;
                case ScriptRunStatus.NotRun:
                    Add(new ConsoleEntry(ConsoleEntryKind.Error, result.Message ?? Loc.Get("Console_Failed")));
                    break;
            }
        }
        finally
        {
            _current = null;
            IsBusy = false;
        }
    }

    /// <summary>実行中の入力を止める (Esc。コンソールにフォーカスがあるとき)。</summary>
    public void StopRunning() => _current?.Stop();

    /// <summary>新しいセッションにする (変数を忘れる。AUTO-23 の仕様 3)。</summary>
    public void Reset()
    {
        StopRunning();
        _session.Dispose();
        _session = new JsReplSession();
        Add(new ConsoleEntry(ConsoleEntryKind.Info, Loc.Get("Console_ResetDone")));
    }

    public void Clear() => Entries.Clear();

    public void Dispose()
    {
        _disposed = true;
        ScriptingService.Output.LinesAdded -= OnOutput;
        _session.Dispose();
    }
}
