using HexEditor.App.Commands;
using HexEditor.Core.Scripting.Macros;
using Windows.System;

namespace HexEditor.App.Services.Scripting;

/// <summary>
/// マクロの記録 (08 の AUTO-27) への操作の通知口。アプリの入力・コマンドの処理がここへ知らせると、記録中なら <see cref="MacroRecorder"/> に
/// 記録する。記録中でなければ何もしない。スクリプト・マクロの実行中の操作 (<see cref="CommandArgs.IsScripted"/>) は記録しない。
/// </summary>
internal static class MacroHooks
{
    private static MacroService Service => ScriptWorkspace.Macros;

    private static readonly AsyncLocal<CommandRecord?> CurrentCommand = new();

    public static bool IsRecording => Service.IsRecording && !CommandArgs.IsScripted;

    // ---- 入力とカーソル移動 ----

    /// <summary>Hex 列・テキスト列で確定したバイト列 (連続した入力は 1 回の <c>writeBytes</c> にまとめる。AUTO-27 の仕様 2)。</summary>
    public static void Typed(byte[] bytes, bool insert)
    {
        if (IsRecording)
        {
            Service.Recorder!.RecordTyping(bytes, insert);
        }
    }

    /// <summary>マウスでのクリック・範囲選択の終わり。<paramref name="before"/> は押した時点のカーソル。</summary>
    public static void Pointer(long before, long cursor, long selectionStart, long selectionLength)
    {
        if (!IsRecording)
        {
            return;
        }

        if (selectionLength > 0)
        {
            Service.Recorder!.RecordSelect(before, selectionStart, selectionLength);
        }
        else
        {
            Service.Recorder!.RecordClick(before, cursor);
        }
    }

    /// <summary>移動のキー。Shift 付き (選択の拡張) は記録できない操作にする。</summary>
    public static void KeyMove(VirtualKey key, bool ctrl, bool shift)
    {
        if (!IsRecording)
        {
            return;
        }

        (string Command, string Name)? move = (key, ctrl) switch
        {
            (VirtualKey.Left, false) => ("cursor.left", ""),
            (VirtualKey.Right, false) => ("cursor.right", ""),
            (VirtualKey.Up, false) => ("cursor.up", ""),
            (VirtualKey.Down, false) => ("cursor.down", ""),
            (VirtualKey.PageUp, false) => ("cursor.pageUp", ""),
            (VirtualKey.PageDown, false) => ("cursor.pageDown", ""),
            (VirtualKey.Home, false) => ("cursor.lineStart", ""),
            (VirtualKey.End, false) => ("cursor.lineEnd", ""),
            (VirtualKey.Home, true) => ("cursor.fileStart", ""),
            (VirtualKey.End, true) => ("cursor.fileEnd", ""),
            _ => null,
        };
        if (shift && (move is not null || key is VirtualKey.Left or VirtualKey.Right && ctrl))
        {
            Service.RaiseUnrecordable(Loc.Get("Macro_Unrecordable_Selection"));
            return;
        }

        if (move is { } m)
        {
            Service.Recorder!.RecordMove(m.Command, CursorCommands.NameOf(m.Command));
        }
        else if (ctrl && key is VirtualKey.Left or VirtualKey.Right)
        {
            Command(key == VirtualKey.Left ? "go.previousGroup" : "go.nextGroup");
        }
        else if (key == VirtualKey.Tab && !ctrl)
        {
            Command("go.toggleColumn");
        }
        else if (key == VirtualKey.Insert && !ctrl && !shift)
        {
            Command("edit.toggleInsert");
        }
        else if (key is VirtualKey.Delete or VirtualKey.Back)
        {
            Service.RaiseUnrecordable(Loc.Get("Macro_Unrecordable_Delete"));
        }
    }

    // ---- コマンド ----

    /// <summary>引数のないコマンドの実行を記録する。</summary>
    public static void Command(string id) => Report(id, null);

    /// <summary>
    /// コマンドを記録する。ダイアログを使うコマンドは、ダイアログで確定した値を <paramref name="arguments"/> に入れる (再生時はダイアログを出さない。
    /// AUTO-27 の仕様 2)。コマンドの処理の中から呼ぶと、そのコマンドの実行として 1 回だけ記録する。
    /// </summary>
    public static void Report(string id, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (!IsRecording)
        {
            return;
        }

        if (CurrentCommand.Value is { } current && current.Id == id)
        {
            current.Reported = true;
        }

        Service.Recorder!.RecordCommand(id, CommandService.DisplayName(id), arguments);
    }

    /// <summary>API の呼び出しとして記録する (検索・置換・オフセットへの移動など、入力した値を引数に持つもの)。</summary>
    public static void Api(string name, string code)
    {
        if (IsRecording)
        {
            Service.Recorder!.RecordApi(name, code);
        }
    }

    /// <summary>オフセットへ移動 (Ctrl+G で確定した値。AUTO-27 の仕様 2: ダイアログの値を引数として記録する)。</summary>
    public static void GoTo(long offset, bool select)
    {
        if (IsRecording)
        {
            Service.Recorder!.RecordApi(Loc.Get("Macro_Op_GoTo"), $"doc.goTo(0x{offset:X});");
        }
    }

    /// <summary>範囲を選択 (Ctrl+E で確定した範囲)。</summary>
    public static void SelectRange(long start, long length)
    {
        if (IsRecording)
        {
            Service.Recorder!.RecordApi(Loc.Get("Macro_Op_SelectRange"), $"doc.selection.set({{ start: 0x{start:X}, length: 0x{length:X} }});");
        }
    }

    /// <summary>塗りつぶし (ダイアログで確定した範囲とパターン。AUTO-27 の仕様 2 と受け入れ基準 3)。</summary>
    public static void Fill(bool useSelection, long start, long length, byte[] pattern)
    {
        if (!IsRecording)
        {
            return;
        }

        string range = useSelection ? "sel" : $"0x{start:X}:0x{length:X}";
        Report("edit.fill", new Dictionary<string, object?> { ["range"] = range, ["pattern"] = Convert.ToHexString(pattern) });
    }

    /// <summary>検索の次 / 前 (検索バーで見つかったとき)。条件を引数に持つ API の呼び出しとして記録する。見つからなければ止まる。</summary>
    public static void Find(HexEditor.Core.Search.SearchQuery query, bool forward)
    {
        if (!IsRecording)
        {
            return;
        }

        string name = Loc.Get("Macro_Op_Find");
        if (QueryOptions(query) is not { } options)
        {
            Service.RaiseUnrecordable(name);
            return;
        }

        string text = MacroJs.Quote(query.Text);
        string notFound = MacroJs.Quote(Loc.Format("Macro_NotFound", query.Text));
        string backward = forward ? "false" : "true";
        Service.Recorder!.RecordApi(name,
            $"{{ const found = await doc.find({text}, {{ {options}, from: doc.cursor, backward: {backward} }}); if (!found) throw new Error({notFound}); doc.selection.set(found); }}");
    }

    /// <summary>すべて置換 (検索バーの条件と置換語)。</summary>
    public static void ReplaceAll(HexEditor.Core.Search.SearchQuery query, string replacement)
    {
        if (!IsRecording)
        {
            return;
        }

        string name = Loc.Get("Macro_Op_ReplaceAll");
        if (QueryOptions(query) is not { } options)
        {
            Service.RaiseUnrecordable(name);
            return;
        }

        // テキストの置換語は、バイト列 (UTF-8) の Hex にして渡す (API の置換語は Hex かバイト列)。
        string replace = query.Kind == HexEditor.Core.Search.SearchKind.Text
            ? Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(replacement))
            : replacement;
        Service.Recorder!.RecordApi(name, $"await doc.replaceAll({MacroJs.Quote(query.Text)}, {MacroJs.Quote(replace)}, {{ {options} }});");
    }

    /// <summary>検索条件の API のオプション。Hex と、UTF-8 / ASCII のテキストだけ記録できる (他は null)。</summary>
    private static string? QueryOptions(HexEditor.Core.Search.SearchQuery q) => q.Kind switch
    {
        HexEditor.Core.Search.SearchKind.Hex when !q.Mask && !q.Mismatch && q.Terms is null => "type: \"hex\"",
        HexEditor.Core.Search.SearchKind.Text when !q.Mask && !q.Mismatch && q.Terms is null && q.Encodings.Count == 0
            && q.EncodingId is "utf-8" or "ascii" or "utf8" && !q.WholeWord && !q.UseEscapes =>
            $"type: \"text\", encoding: \"utf-8\", caseSensitive: {(q.CaseSensitive ? "true" : "false")}",
        _ => null,
    };

    /// <summary>ファイルを開いた。</summary>
    public static void Opened(string path)
    {
        if (IsRecording)
        {
            Service.Recorder!.RecordOpen(path);
        }
    }

    /// <summary>保存した (パスを指定したときは名前を付けて保存)。</summary>
    public static void Saved(string? saveAsPath)
    {
        if (IsRecording)
        {
            Service.Recorder!.RecordSave(saveAsPath);
        }
    }

    /// <summary>記録できない操作 (ステータスバーに「記録できない操作: 名前」と一時表示する。AUTO-27 の仕様 5)。</summary>
    public static void Unrecordable(string name)
    {
        if (IsRecording)
        {
            Service.RaiseUnrecordable(name);
        }
    }

    /// <summary>コマンドの処理の始まり (<see cref="CommandHost"/> から)。</summary>
    internal static IDisposable? BeginCommand(string id)
    {
        if (!IsRecording)
        {
            return null;
        }

        var record = new CommandRecord(id);
        CurrentCommand.Value = record;
        return record;
    }

    private sealed class CommandRecord(string id) : IDisposable
    {
        public string Id { get; } = id;

        public bool Reported { get; set; }

        public void Dispose()
        {
            if (CurrentCommand.Value == this)
            {
                CurrentCommand.Value = null;
            }

            if (!Reported)
            {
                Finish(Id);
            }
        }
    }

    /// <summary>ダイアログを使わないコマンド (そのまま記録する)。</summary>
    private static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "edit.undo", "edit.redo", "edit.cut", "edit.copy", "edit.paste", "edit.pasteOverwrite", "edit.selectAll", "edit.toggleInsert",
        "go.toggleColumn", "go.previousGroup", "go.nextGroup", "go.nextNibble", "go.previousNibble", "go.start", "go.end", "go.back", "go.forward",
        "go.bookmark.toggle", "go.bookmark.next", "go.bookmark.previous", "search.findNext", "search.findPrevious", "tab.next", "tab.previous",
    };

    /// <summary>値を報告して記録するコマンド (報告がなければ、キャンセルか、記録できない種類の入力だったということ)。</summary>
    private static readonly HashSet<string> Dialog = new(StringComparer.Ordinal)
    {
        "go.goTo", "edit.selectRange", "edit.fill", "edit.insertBytes", "edit.resize", "edit.truncate", "search.replaceAll", "search.find", "search.replace",
        "file.open", "file.save", "file.saveAs",
    };

    /// <summary>画面の表示・設定・ヘルプなど、データの操作ではないもの (記録しない。記録できない操作の表示も出さない)。</summary>
    private static readonly string[] Silent = ["macro.", "view.", "settings.", "help.", "codeEditor.", "scripting.show", "window.", "workspace."];

    private static void Finish(string id)
    {
        if (Dialog.Contains(id) || Silent.Any(p => id.StartsWith(p, StringComparison.Ordinal)))
        {
            return;
        }

        if (Plain.Contains(id))
        {
            if (id is "tab.next" or "tab.previous")
            {
                Service.Recorder?.RecordSwitchTab(id == "tab.next" ? 1 : -1);
            }
            else
            {
                Service.Recorder?.RecordCommand(id, CommandService.DisplayName(id));
            }

            return;
        }

        Service.RaiseUnrecordable(CommandService.DisplayName(id));
    }
}
