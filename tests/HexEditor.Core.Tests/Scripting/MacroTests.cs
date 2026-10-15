using System.Text.RegularExpressions;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Macros;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// マクロの記録 (AUTO-27) と再生 (AUTO-28) の Core の部分: 操作のまとめ方、スクリプトへの変換、繰り返し再生の条件、Undo の単位、
/// 信頼の扱い。キー入力・ダイアログ・ステータスバーの表示は UI のテストで確かめる。
/// </summary>
public sealed class MacroTests : IDisposable
{
    private readonly ScriptTestHost _host = new();
    private readonly string _previousLanguage = Msg.Default;

    public MacroTests() => Msg.Default = "en";

    public void Dispose()
    {
        Msg.Default = _previousLanguage;
        _host.Dispose();
    }

    private static byte[] Sequence(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    /// <summary>「00 を入力して → を 15 回」(カーソル位置に 00 を書いて 16 バイト進む) のマクロ。</summary>
    private static string Step16()
    {
        var recorder = new MacroRecorder();
        recorder.RecordTyping([0x00], insert: false);
        recorder.RecordMove("cursor.right", "Right", 15);
        return recorder.ToScript("step16");
    }

    private Task<ScriptRunResult> PlayAsync(string macro, MacroPlayback playback, string? path = null) =>
        _host.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Code = path is null ? macro : null,
            Path = path,
            Origin = path is null ? ScriptOrigin.UnsavedMacro : ScriptOrigin.File,
            DisplayName = "macro",
            CodeWrapper = MacroPlayer.Wrap,
            PresetArgs = MacroPlayer.Arguments(playback),
            SkipParameterDialog = true,
        }, _host.Env).WaitAsync(TimeSpan.FromSeconds(120));

    private void SetCursor(CoreDocumentHost doc, long offset) => _host.OnUi(() => doc.Editor.Select(offset, 0));

    private static byte[] Bytes(ScriptTestHost t, CoreDocumentHost doc) => t.OnUi(() => CSharpScriptTests.ReadAll(doc));

    // ---- 記録 ----

    [Fact]
    [Trait("TC", "TC-AUTO-27-02")]
    public void Typing_DEAD_is_recorded_as_one_writeBytes_with_metadata_and_comments()
    {
        var recorder = new MacroRecorder();
        foreach (byte b in new byte[] { 0xDE, 0xAD })
        {
            recorder.RecordTyping([b], insert: false);
        }

        string script = recorder.ToScript("dead");

        Assert.Equal(1, recorder.Count);
        Assert.Single(Regex.Matches(script, @"writeBytes\("));
        Assert.Contains("writeBytes(doc.cursor, [0xDE, 0xAD]);", script, StringComparison.Ordinal);
        Assert.Contains("// ==HexEditorScript==", script, StringComparison.Ordinal);
        Assert.Contains("// @name  dead", script, StringComparison.Ordinal);
        Assert.Contains("// Typed: DE AD", script, StringComparison.Ordinal);
        Assert.Equal(ScriptLanguage.JavaScript, ScriptHost.LanguageOf("dead.js"));
        Assert.Empty(ScriptMetadata.Parse(script).Warnings);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-27-01")]
    public void Consecutive_same_moves_are_merged_and_the_action_count_is_reported()
    {
        var recorder = new MacroRecorder();
        int changes = 0;
        recorder.Changed += (_, _) => changes++;

        for (int i = 0; i < 3; i++)
        {
            recorder.RecordMove("cursor.right", "Right");
        }

        Assert.Equal(1, recorder.Count);
        Assert.Equal(3, changes);
        recorder.RecordMove("cursor.down", "Down");
        recorder.RecordMove("cursor.right", "Right");
        Assert.Equal(3, recorder.Count);
        Assert.Contains("execute(\"cursor.right\", { count: 3 })", recorder.ToScript(), StringComparison.Ordinal);
    }

    [Fact]
    public void Typing_in_insert_mode_uses_insertBytes_and_is_not_merged_with_overwrite_typing()
    {
        var recorder = new MacroRecorder();
        recorder.RecordTyping([0x01], insert: false);
        recorder.RecordTyping([0x02], insert: true);
        recorder.RecordTyping([0x03], insert: true);
        string script = recorder.ToScript();
        Assert.Equal(2, recorder.Count);
        Assert.Contains("doc.writeBytes(doc.cursor, [0x01]);", script, StringComparison.Ordinal);
        Assert.Contains("doc.insertBytes(doc.cursor, [0x02, 0x03]);", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Positions_are_recorded_relative_to_the_cursor_or_absolute()
    {
        var relative = new MacroRecorder(MacroPositionMode.Relative);
        relative.RecordClick(before: 0x100, target: 0x110);
        relative.RecordClick(before: 0x110, target: 0x108);
        relative.RecordSelect(before: 0x108, start: 0x120, length: 0x20);
        string rel = relative.ToScript();
        Assert.Contains("doc.goTo(doc.cursor + 0x10);", rel, StringComparison.Ordinal);
        Assert.Contains("doc.goTo(doc.cursor - 0x8);", rel, StringComparison.Ordinal);
        Assert.Contains("doc.selection.set({ start: doc.cursor + 0x18, length: 0x20 });", rel, StringComparison.Ordinal);

        var absolute = new MacroRecorder(MacroPositionMode.Absolute);
        absolute.RecordClick(before: 0x100, target: 0x110);
        absolute.RecordSelect(before: 0x108, start: 0x120, length: 0x20);
        string abs = absolute.ToScript();
        Assert.Contains("doc.goTo(0x110);", abs, StringComparison.Ordinal);
        Assert.Contains("doc.selection.set({ start: 0x120, length: 0x20 });", abs, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-27-03")]
    public void A_dialog_command_is_recorded_with_the_confirmed_values_as_arguments()
    {
        var recorder = new MacroRecorder();
        recorder.RecordCommand("edit.selectRange", "Select range", new Dictionary<string, object?> { ["range"] = "0x0:0x100" });
        recorder.RecordCommand("edit.fill", "Fill", new Dictionary<string, object?> { ["range"] = "sel", ["pattern"] = "AB" });

        string script = recorder.ToScript();

        Assert.Contains("await app.commands.execute(\"edit.fill\", { range: \"sel\", pattern: \"AB\" });", script, StringComparison.Ordinal);
        Assert.Contains("// Fill", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-27-04")]
    public void The_recorded_code_does_not_depend_on_the_display_language()
    {
        string Record(string language)
        {
            Msg.Default = language;
            var recorder = new MacroRecorder(now: new DateTime(2026, 10, 7, 14, 3, 0));
            recorder.RecordClick(0, 0x10);
            recorder.RecordTyping([0xAB], insert: false);
            recorder.RecordMove("cursor.right", CursorCommands.NameOf("cursor.right"), 2);
            recorder.RecordCommand("edit.fill", "fill", new Dictionary<string, object?> { ["pattern"] = "CD" });
            return recorder.ToScript();
        }

        static string CodeOnly(string script) => string.Join('\n', script.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        string ja = Record("ja"), en = Record("en"), ar = Record("ar");
        Assert.NotEqual(ja, en);
        Assert.Equal(CodeOnly(ja), CodeOnly(en));
        Assert.Equal(CodeOnly(en), CodeOnly(ar));
        Assert.DoesNotContain("右", CodeOnly(ja), StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_name_has_the_start_time()
    {
        var recorder = new MacroRecorder(now: new DateTime(2026, 10, 7, 14, 3, 0));
        Assert.Equal("Macro 2026-10-07 14:03", recorder.DefaultName());
        Assert.Contains("// @name  Macro 2026-10-07 14:03", recorder.ToScript(), StringComparison.Ordinal);
    }

    // ---- 再生 ----

    [Fact]
    [Trait("TC", "TC-AUTO-28-01")]
    public async Task The_last_macro_replays_without_prompts()
    {
        var recorder = new MacroRecorder();
        recorder.RecordTyping([0xFF], insert: false);
        recorder.RecordMove("cursor.right", "Right");
        string macro = recorder.ToScript();
        CoreDocumentHost doc = _host.Open(Sequence(16));
        SetCursor(doc, 0);

        // 記録時の 1 回 (手で行った操作の代わりにスクリプトとして) と、再生 2 回。
        ScriptTestHost.AssertSucceeded(await PlayAsync(macro, MacroPlayback.Once));
        ScriptTestHost.AssertSucceeded(await PlayAsync(macro, MacroPlayback.Once));
        ScriptTestHost.AssertSucceeded(await PlayAsync(macro, MacroPlayback.Once));

        Assert.Equal([0xFF, 0x01, 0xFF, 0x03, 0xFF, 0x05], Bytes(_host, doc)[..6]);
        Assert.Empty(_host.Ui.TrustPrompts);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-28-02")]
    public async Task Repeating_until_the_end_stops_at_the_last_byte_without_growing_the_file()
    {
        CoreDocumentHost doc = _host.Open(Sequence(100));
        SetCursor(doc, 0);

        ScriptTestHost.AssertSucceeded(await PlayAsync(Step16(), new MacroPlayback(MacroRepeatMode.UntilEnd)));

        byte[] bytes = Bytes(_host, doc);
        Assert.Equal(100, bytes.Length);
        Assert.All(new[] { 0, 16, 32, 48, 64, 80, 96 }, i => Assert.Equal(0, bytes[i]));
        Assert.Equal(99, bytes[99]);
        Assert.Equal(1, bytes[1]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-28-03")]
    public async Task Ten_thousand_repetitions_are_undone_with_one_undo()
    {
        byte[] original = Sequence(10_000 * 16).Select(b => (byte)(b ^ 0x55)).ToArray();
        CoreDocumentHost doc = _host.Open(original);
        SetCursor(doc, 0);

        ScriptTestHost.AssertSucceeded(await PlayAsync(Step16(), new MacroPlayback(MacroRepeatMode.Count, 10_000)));

        byte[] after = Bytes(_host, doc);
        Assert.Equal(0, after[0]);
        Assert.Equal(0, after[0x270F0]);
        Assert.Equal(original[1], after[1]);

        _host.OnUi(() => doc.Document.Undo());
        Assert.Equal(original, Bytes(_host, doc));
        Assert.False(_host.OnUi(() => doc.Document.History.CanUndo));
    }

    [Fact]
    public async Task Repeating_for_each_result_places_the_cursor_at_each_position()
    {
        var recorder = new MacroRecorder();
        recorder.RecordTyping([0xEE], insert: false);
        CoreDocumentHost doc = _host.Open(Sequence(64));

        ScriptTestHost.AssertSucceeded(await PlayAsync(recorder.ToScript(), new MacroPlayback(MacroRepeatMode.PerResult, ResultStarts: [3, 10, 40])));

        byte[] bytes = Bytes(_host, doc);
        Assert.Equal([3, 10, 40], Enumerable.Range(0, 64).Where(i => bytes[i] == 0xEE));
    }

    [Fact]
    public async Task Repeating_in_the_selection_stops_when_the_cursor_leaves_it()
    {
        var recorder = new MacroRecorder();
        recorder.RecordTyping([0x00], insert: false);
        recorder.RecordMove("cursor.right", "Right");
        CoreDocumentHost doc = _host.Open([.. Enumerable.Repeat((byte)0xAA, 64)]);
        _host.OnUi(() => doc.Editor.Select(8, 16));

        ScriptTestHost.AssertSucceeded(await PlayAsync(recorder.ToScript(), new MacroPlayback(MacroRepeatMode.InSelection)));

        byte[] bytes = Bytes(_host, doc);
        // 8 から 1 回ごとに 2 バイト進み (書いて 1、右へ 1)、選択の終わり (24) の手前で止まる。
        Assert.Equal([8, 10, 12, 14, 16, 18, 20, 22], Enumerable.Range(0, 64).Where(i => bytes[i] == 0));
        Assert.Equal(0xAA, bytes[0]);
    }

    [Fact]
    public async Task A_failure_names_the_iteration_and_the_operation()
    {
        var recorder = new MacroRecorder();
        recorder.RecordMove("cursor.right", "Right");
        recorder.RecordCommand("no.such", "Find the marker");
        string macro = recorder.ToScript();
        _host.Open(Sequence(16));

        ScriptRunResult result = await PlayAsync(macro, new MacroPlayback(MacroRepeatMode.Count, 5));

        Assert.Equal(ScriptRunStatus.Failed, result.Status);
        (int iteration, int operation) = MacroPlayer.LocationOf(result.Message);
        // 失敗した回 (1 回目) の 2 番目の操作で止まる。2 回目以降は実行しない。
        Assert.Equal((1, 2), (iteration, operation));
        Assert.Equal("Find the marker", MacroPlayer.DescribeOperation(macro, operation));
        Assert.Equal("Move cursor: Right", MacroPlayer.DescribeOperation(macro, 1));
    }

    [Fact]
    public async Task Stopping_ends_the_repetition()
    {
        var recorder = new MacroRecorder();
        recorder.RecordMove("cursor.right", "Right");
        _host.Open(Sequence(16));
        ScriptRun run = _host.Start(new ScriptRunRequest
        {
            Code = recorder.ToScript(),
            Origin = ScriptOrigin.UnsavedMacro,
            CodeWrapper = MacroPlayer.Wrap,
            PresetArgs = MacroPlayer.Arguments(new MacroPlayback(MacroRepeatMode.Count, 1_000_000)),
        });

        // 実行を始めてから止める。
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Changed += (_, _) =>
        {
            if (run.Status == ScriptRunStatus.Running)
            {
                running.TrySetResult();
            }
        };
        if (run.Status != ScriptRunStatus.Running)
        {
            await running.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        run.Stop();
        ScriptRunResult result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ScriptRunStatus.Stopped, result.Status);
    }

    // ---- 保存と信頼 ----

    [Fact]
    [Trait("TC", "TC-AUTO-28-04")]
    public async Task A_saved_macro_runs_without_a_trust_prompt_until_it_is_changed_outside_the_app()
    {
        var recorder = new MacroRecorder();
        recorder.RecordTyping([0x7F], insert: false);
        string path = MacroStore.Save(_host.Folders, _host.Trust, "step", recorder.ToScript());
        Assert.Equal(Path.Combine(_host.Folders.Settings, "macros", "step.js"), path);
        Assert.Equal(TrustSource.RecordedMacro,
            _host.Trust.Find(TrustStore.ScriptKind, TrustStore.ScriptKey(path), TrustStore.HashOf(File.ReadAllText(path)))!.Source);

        CoreDocumentHost doc = _host.Open(Sequence(8));
        SetCursor(doc, 4);
        ScriptTestHost.AssertSucceeded(await PlayAsync("", MacroPlayback.Once, path));
        Assert.Equal(0x7F, Bytes(_host, doc)[4]);
        Assert.Empty(_host.Ui.TrustPrompts);

        // アプリの外で内容が変わったら、通常のスクリプトと同じく確認する。
        File.AppendAllText(path, "// changed outside\n");
        await PlayAsync("", MacroPlayback.Once, path);
        Assert.Single(_host.Ui.TrustPrompts);
    }

    [Fact]
    public void The_macro_name_in_the_metadata_follows_the_saved_name()
    {
        var recorder = new MacroRecorder();
        recorder.RecordTyping([1], insert: false);
        string path = MacroStore.Save(_host.Folders, _host.Trust, "renamed: macro", recorder.ToScript("Macro 1"));
        Assert.EndsWith("renamed_ macro.js", path, StringComparison.Ordinal);
        Assert.Equal("renamed: macro", ScriptMetadata.Parse(File.ReadAllText(path)).Name);
        Assert.Throws<IOException>(() => MacroStore.Save(_host.Folders, _host.Trust, "renamed: macro", recorder.ToScript(), overwrite: false));
    }
}
