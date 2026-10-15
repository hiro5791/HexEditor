namespace HexEditor.Core.Scripting;

/// <summary>出力の行の種類 (AUTO-24 の仕様 2)。</summary>
public enum ScriptOutputKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 出力の 1 行 (AUTO-24 の仕様 2・3)。<see cref="Channel"/> は出力元 (「スクリプト: 名前」など)。エラーの行のファイル名と行番号
/// (<see cref="SourcePath"/>、<see cref="Line"/>、<see cref="Column"/>) は、出力パネルでスクリプトエディタの該当行へのリンクにする。
/// </summary>
public sealed record ScriptOutputLine(DateTimeOffset Time, string Channel, ScriptOutputKind Kind, string Text, string? SourcePath = null,
    int Line = 0, int Column = 0);

/// <summary>
/// 出力の記録 (アプリ全体で 1 つ。AUTO-24 の出力パネルの元)。保持する行数の上限 (既定 10,000 行) を超えたら古いものから消す (仕様 4)。
/// <see cref="LinesAdded"/> は書いたスレッドで発生する。パネルは受け取った行をまとめて表示する (最大 10 回 / 秒)。
/// </summary>
public sealed class ScriptOutputLog(int capacity = ScriptOutputLog.DefaultCapacity, TimeProvider? time = null)
{
    public const int DefaultCapacity = 10_000;

    private readonly Lock _lock = new();
    private readonly LinkedList<ScriptOutputLine> _lines = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _capacity = Math.Max(1, capacity);

    /// <summary>行が加わった (書いたスレッドで発生する)。</summary>
    public event EventHandler<ScriptOutputLine>? LinesAdded;

    /// <summary>消された (クリア)。</summary>
    public event EventHandler? Cleared;

    public int Capacity
    {
        get => _capacity;
        set
        {
            lock (_lock)
            {
                _capacity = Math.Max(1, value);
                Trim();
            }
        }
    }

    public IReadOnlyList<ScriptOutputLine> Lines
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>出力元の一覧 (出力パネルのドロップダウン。AUTO-24 の仕様 1)。</summary>
    public IReadOnlyList<string> Channels
    {
        get
        {
            lock (_lock)
            {
                return [.. _lines.Select(l => l.Channel).Distinct(StringComparer.Ordinal)];
            }
        }
    }

    public ScriptOutputLine Write(string channel, ScriptOutputKind kind, string text, string? sourcePath = null, int line = 0, int column = 0)
    {
        var entry = new ScriptOutputLine(_time.GetUtcNow(), channel, kind, text, sourcePath, line, column);
        lock (_lock)
        {
            _lines.AddLast(entry);
            Trim();
        }

        LinesAdded?.Invoke(this, entry);
        return entry;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>出力元の行 (テストと「すべてコピー」用)。</summary>
    public IReadOnlyList<ScriptOutputLine> LinesOf(string channel)
    {
        lock (_lock)
        {
            return [.. _lines.Where(l => l.Channel == channel)];
        }
    }

    private void Trim()
    {
        while (_lines.Count > _capacity)
        {
            _lines.RemoveFirst();
        }
    }
}
