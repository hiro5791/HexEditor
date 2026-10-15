using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HexEditor.Core.Scripting.Macros;

/// <summary>位置の記録方式 (AUTO-27 の仕様 3)。</summary>
public enum MacroPositionMode
{
    /// <summary>相対 (既定): マウスでの位置指定を、その操作の直前のカーソルからの相対オフセットで記録する。</summary>
    Relative,

    /// <summary>絶対: オフセットそのものを記録する。</summary>
    Absolute,
}

/// <summary>記録した操作 1 つ。<see cref="Comment"/> は生成するスクリプトのコメント (操作の表示名。AUTO-27 の仕様 7)。</summary>
public abstract record MacroAction(string Comment)
{
    /// <summary>JavaScript の文 (改行区切り。<c>doc</c> は操作の対象のドキュメント)。</summary>
    public abstract string ToCode();
}

/// <summary>文字・Hex の入力。連続した入力は 1 つにまとめる (仕様 2)。</summary>
public sealed record TypeAction(byte[] Bytes, bool Insert) : MacroAction(Describe(Bytes, Insert))
{
    public static string Describe(byte[] bytes, bool insert) =>
        Msg.T("入力: ", "Typed: ") + string.Join(' ', bytes.Take(16).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) + (bytes.Length > 16 ? " …" : "")
        + (insert ? Msg.T(" (挿入)", " (insert)") : "");

    public override string ToCode()
    {
        string array = "[" + string.Join(", ", Bytes.Select(b => "0x" + b.ToString("X2", CultureInfo.InvariantCulture))) + "]";
        return $"doc.{(Insert ? "insertBytes" : "writeBytes")}(doc.cursor, {array});\ndoc.cursor += {Bytes.Length.ToString(CultureInfo.InvariantCulture)};";
    }
}

/// <summary>カーソル移動のコマンドと回数 (連続した同じ移動はまとめる)。</summary>
public sealed record MoveAction(string Command, int Count, string Name) : MacroAction(Msg.T("カーソル移動: ", "Move cursor: ") + Name + (Count > 1 ? " ×" + Count.ToString(CultureInfo.InvariantCulture) : ""))
{
    public override string ToCode() => $"await app.commands.execute({MacroJs.Quote(Command)}, {{ count: {Count.ToString(CultureInfo.InvariantCulture)} }});";
}

/// <summary>マウスでのクリック: カーソルをその位置に置く。</summary>
public sealed record GoToAction(long Value, bool Relative) : MacroAction(Msg.T("オフセットへ移動: ", "Go to offset: ") + MacroJs.OffsetText(Value, Relative))
{
    public override string ToCode() => $"doc.goTo({MacroJs.OffsetCode(Value, Relative)});";
}

/// <summary>マウスでの範囲選択。</summary>
public sealed record SelectAction(long Start, long Length, bool Relative) : MacroAction(Msg.T("範囲を選択: ", "Select range: ") + MacroJs.OffsetText(Start, Relative) + ":0x" + Length.ToString("X", CultureInfo.InvariantCulture))
{
    public override string ToCode() => $"doc.selection.set({{ start: {MacroJs.OffsetCode(Start, Relative)}, length: 0x{Length.ToString("X", CultureInfo.InvariantCulture)} }});";
}

/// <summary>コマンドの実行。ダイアログを使うコマンドは、ダイアログで確定した値を引数として持つ (再生時はダイアログを出さない。仕様 2)。</summary>
public sealed record CommandAction(string Command, IReadOnlyDictionary<string, object?> Arguments, string Name) : MacroAction(Name)
{
    public override string ToCode() =>
        Arguments.Count == 0
            ? $"await app.commands.execute({MacroJs.Quote(Command)});"
            : $"await app.commands.execute({MacroJs.Quote(Command)}, {MacroJs.Object(Arguments)});";
}

/// <summary>ファイルを開く (パスを引数として記録する)。</summary>
public sealed record OpenAction(string Path) : MacroAction(Msg.T("ファイルを開く: ", "Open file: ") + System.IO.Path.GetFileName(Path))
{
    public override string ToCode() => $"doc = await app.open({MacroJs.Quote(Path)});";
}

/// <summary>保存 (パスを引数として記録する。上書き保存はパスなし)。</summary>
public sealed record SaveAction(string? Path) : MacroAction(Path is null ? Msg.T("保存", "Save") : Msg.T("名前を付けて保存: ", "Save as: ") + System.IO.Path.GetFileName(Path))
{
    public override string ToCode() => Path is null ? "await doc.save();" : $"await doc.saveAs({MacroJs.Quote(Path)});";
}

/// <summary>タブの切り替え: ドキュメントの相対的な指定 (次・前のタブ)。</summary>
public sealed record SwitchTabAction(int Delta) : MacroAction(Delta > 0 ? Msg.T("次のタブ", "Next tab") : Msg.T("前のタブ", "Previous tab"))
{
    public override string ToCode() =>
        $"app.activeDocument = app.documents[(app.documents.indexOf(app.activeDocument) + {(Delta > 0 ? 1 : -1)} + app.documents.length) % app.documents.length];\ndoc = app.activeDocument;";
}

/// <summary>JavaScript の文字列・数値の書き出し。</summary>
internal static class MacroJs
{
    public static string Quote(string s) => JsonSerializer.Serialize(s);

    public static string OffsetText(long value, bool relative) =>
        relative ? (value >= 0 ? "cur+0x" : "cur-0x") + Math.Abs(value).ToString("X", CultureInfo.InvariantCulture) : "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    public static string OffsetCode(long value, bool relative) =>
        relative ? (value >= 0 ? "doc.cursor + 0x" : "doc.cursor - 0x") + Math.Abs(value).ToString("X", CultureInfo.InvariantCulture) : "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>オブジェクトのリテラル (<c>{ range: "sel", pattern: "00" }</c>)。値は文字列・数値・真偽値。</summary>
    public static string Object(IReadOnlyDictionary<string, object?> values) =>
        "{ " + string.Join(", ", values.Select(kv => $"{kv.Key}: {Value(kv.Value)}")) + " }";

    private static string Value(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => Quote(s),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Quote(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""),
    };
}

/// <summary>記録できない操作を行った (ステータスバーに「記録できない操作: 名前」と一時表示する。AUTO-27 の仕様 5)。</summary>
public sealed class UnrecordableEventArgs(string name) : EventArgs
{
    public string Name { get; } = name;
}

/// <summary>
/// マクロの記録 (AUTO-27)。キー入力ではなくコマンド単位で操作を記録し、JavaScript のスクリプトに変換する。連続した入力と
/// 同じ移動はまとめる。App は操作のたびに <c>Record...</c> を呼ぶ。
/// </summary>
public sealed class MacroRecorder
{
    private readonly List<MacroAction> _actions = [];
    private readonly Lock _lock = new();

    public MacroRecorder(MacroPositionMode mode = MacroPositionMode.Relative, DateTime? now = null)
    {
        Mode = mode;
        StartedAt = now ?? DateTime.Now;
    }

    public MacroPositionMode Mode { get; }

    public DateTime StartedAt { get; }

    /// <summary>記録した操作の数 (ステータスバーに表示する。まとめた後の数)。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _actions.Count;
            }
        }
    }

    public IReadOnlyList<MacroAction> Actions
    {
        get
        {
            lock (_lock)
            {
                return [.. _actions];
            }
        }
    }

    /// <summary>操作が増えた・まとまった。</summary>
    public event EventHandler? Changed;

    /// <summary>記録できない操作があった。</summary>
    public event EventHandler<UnrecordableEventArgs>? Unrecordable;

    /// <summary>文字・Hex の入力 (確定した 1 バイト以上)。直前も入力ならまとめる。</summary>
    public void RecordTyping(byte[] bytes, bool insert)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_actions.Count > 0 && _actions[^1] is TypeAction last && last.Insert == insert)
            {
                _actions[^1] = new TypeAction([.. last.Bytes, .. bytes], insert);
            }
            else
            {
                _actions.Add(new TypeAction(bytes, insert));
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>入力した 1 バイトを取り消して置き換える (Backspace で直前の入力を消す)。まとめた入力の末尾を削る。</summary>
    public void RecordUndoTypedByte()
    {
        lock (_lock)
        {
            if (_actions.Count > 0 && _actions[^1] is TypeAction { Bytes.Length: > 0 } last)
            {
                byte[] rest = last.Bytes[..^1];
                if (rest.Length == 0)
                {
                    _actions.RemoveAt(_actions.Count - 1);
                }
                else
                {
                    _actions[^1] = new TypeAction(rest, last.Insert);
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>カーソル移動のコマンド (<c>cursor.right</c> など)。連続した同じ移動はまとめる。</summary>
    public void RecordMove(string command, string name, int count = 1)
    {
        lock (_lock)
        {
            if (_actions.Count > 0 && _actions[^1] is MoveAction last && last.Command == command)
            {
                _actions[^1] = last with { Count = last.Count + count };
            }
            else
            {
                _actions.Add(new MoveAction(command, count, name));
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>マウスでのクリック。<paramref name="before"/> は操作の直前のカーソル (相対の記録のため)。</summary>
    public void RecordClick(long before, long target) =>
        Add(new GoToAction(Mode == MacroPositionMode.Relative ? target - before : target, Mode == MacroPositionMode.Relative));

    /// <summary>マウスでの範囲選択。</summary>
    public void RecordSelect(long before, long start, long length) =>
        Add(new SelectAction(Mode == MacroPositionMode.Relative ? start - before : start, length, Mode == MacroPositionMode.Relative));

    /// <summary>
    /// コマンドの実行。ダイアログを使うコマンドは、ダイアログで確定した値を <paramref name="arguments"/> に入れる。
    /// <paramref name="name"/> は操作の表示名 (スクリプトのコメント)。
    /// </summary>
    public void RecordCommand(string command, string name, IReadOnlyDictionary<string, object?>? arguments = null) =>
        Add(new CommandAction(command, arguments ?? new Dictionary<string, object?>(), name));

    public void RecordOpen(string path) => Add(new OpenAction(path));

    public void RecordSave(string? path) => Add(new SaveAction(path));

    public void RecordSwitchTab(int delta) => Add(new SwitchTabAction(delta));

    /// <summary>記録できない操作を行った (記録しない)。</summary>
    public void RaiseUnrecordable(string name) => Unrecordable?.Invoke(this, new UnrecordableEventArgs(name));

    private void Add(MacroAction action)
    {
        lock (_lock)
        {
            _actions.Add(action);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>既定の名前 (<c>マクロ 2026-10-07 14:03</c>)。</summary>
    public string DefaultName() => Msg.T("マクロ ", "Macro ") + StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 記録を JavaScript のスクリプトにする (AUTO-27 の仕様 7: メタデータと各操作のコメントを含む)。
    /// </summary>
    public string ToScript(string? name = null)
    {
        var sb = new StringBuilder();
        sb.Append("// ==HexEditorScript==\n");
        sb.Append("// @name  ").Append(name ?? DefaultName()).Append('\n');
        sb.Append("// @api   1.0\n");
        sb.Append("// ==/HexEditorScript==\n");
        sb.Append("let doc = app.activeDocument;\n");
        foreach (MacroAction action in Actions)
        {
            sb.Append("// ").Append(action.Comment.Replace('\n', ' ')).Append('\n');
            sb.Append(action.ToCode()).Append('\n');
        }

        return sb.ToString();
    }
}
