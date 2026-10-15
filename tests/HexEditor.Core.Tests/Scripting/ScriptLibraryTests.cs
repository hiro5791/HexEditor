using System.Diagnostics;
using HexEditor.Core.Commands;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Library;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>スクリプト一覧の元 (AUTO-19): 根のフォルダの走査、表示名、監視、コマンド ID、操作。</summary>
public sealed class ScriptLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HexEditor", "library-tests", Guid.NewGuid().ToString("N"));
    private readonly ScriptFolders _folders;
    private readonly List<ScriptLibrary> _libraries = [];

    public ScriptLibraryTests()
    {
        _folders = new ScriptFolders(Path.Combine(_root, "settings"), Path.Combine(_root, "data"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (ScriptLibrary l in _libraries)
        {
            l.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ScriptLibrary Create(string[]? extra = null, string? samples = null, string language = "en")
    {
        var library = ScriptLibrary.Create(_folders, () => extra ?? [], () => samples);
        library.Language = language;
        _libraries.Add(library);
        library.Start();
        return library;
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string Hello = "// ==HexEditorScript==\n// @name  hello\n// @description  says hello\n// @on  open *.bin\n// ==/HexEditorScript==\nconsole.log('hello from js');\n";

    [Fact]
    public void Scripts_macros_extra_folders_and_samples_are_listed_with_names_and_descriptions()
    {
        string extra = Path.Combine(_root, "shared");
        string samples = Path.Combine(_root, "samples");
        Write(Path.Combine(_folders.Scripts, "plain.js"), "1;");
        Write(Path.Combine(_folders.Scripts, "sub", "hello.js"), Hello);
        Write(Path.Combine(_folders.Settings, "macros", "m1.js"), "// ==HexEditorScript==\n// @name  My macro\n// ==/HexEditorScript==\n");
        Write(Path.Combine(extra, "team.csx"), "var x = 1;");
        Write(Path.Combine(samples, "xor.js"), "// ==HexEditorScript==\n// @name  XOR decode\n// ==/HexEditorScript==\n");

        ScriptLibrary library = Create([extra], samples);

        ScriptNode scripts = library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Scripts);
        Assert.Equal(["sub", "plain"], scripts.Children.Select(c => c.Name));
        ScriptNode hello = scripts.Children.Single(c => c.Name == "sub").Children.Single();
        Assert.Equal("hello", hello.Name);
        Assert.Equal("says hello", hello.Description);
        Assert.Equal(["open *.bin"], hello.HookCandidates);
        Assert.Equal(ScriptLanguage.JavaScript, hello.Language);
        Assert.Equal("sub/hello.js", hello.RelativePath);
        Assert.Equal("My macro", library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Macros).Children.Single().Name);
        ScriptNode team = library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Extra).Children.Single();
        Assert.Equal(ScriptLanguage.CSharp, team.Language);
        Assert.Equal("XOR decode", library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Samples).Children.Single().Name);

        // サンプルはコマンドにしない (仕様 4)。
        Assert.DoesNotContain(library.CommandScripts, n => n.Root.Kind == ScriptRootKind.Samples);
        Assert.Equal(4, library.CommandScripts.Count);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-26-02")]
    public void The_display_name_follows_the_display_language()
    {
        Write(Path.Combine(_folders.Scripts, "params.js"), "// ==HexEditorScript==\n// @name XOR デコード\n// @name:en XOR decode\n// ==/HexEditorScript==\n");
        ScriptLibrary library = Create(language: "en");
        Assert.Equal("XOR decode", library.Scripts.Single().Name);
        library.Language = "ja";
        Assert.Equal("XOR デコード", library.Scripts.Single().Name);
        library.Language = "ar";
        Assert.Equal("XOR デコード", library.Scripts.Single().Name);
    }

    [Fact]
    public void A_missing_folder_is_marked_and_the_other_items_are_still_listed()
    {
        Write(Path.Combine(_folders.Scripts, "a.js"), "1;");
        ScriptLibrary library = Create([Path.Combine(_root, "not-there")]);
        ScriptNode extra = library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Extra);
        Assert.True(extra.Missing);
        Assert.Empty(extra.Children);
        Assert.Single(library.Scripts);
    }

    [Fact]
    public void Storage_and_lib_folders_are_not_listed()
    {
        Write(Path.Combine(_folders.Storage, "x.js"), "1;");
        Write(Path.Combine(_folders.Library, "util.js"), "1;");
        Write(Path.Combine(_folders.Scripts, "real.js"), "1;");
        Assert.Equal(["real"], Create().Scripts.Select(s => s.Name));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-19-01")]
    public async Task A_script_dropped_into_the_folder_appears_within_one_second_and_disappears_when_deleted()
    {
        ScriptLibrary library = Create();
        string path = Path.Combine(_folders.Scripts, "sub", "hello.js");

        TimeSpan appeared = await WaitUntilAsync(library, l => l.Scripts.Any(s => s.Name == "hello"), () => Write(path, Hello));
        Assert.True(appeared < TimeSpan.FromSeconds(1), $"appeared after {appeared.TotalMilliseconds:0} ms");
        ScriptNode sub = library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Scripts).Children.Single(c => c.Name == "sub");
        Assert.Equal("hello", sub.Children.Single().Name);

        TimeSpan removed = await WaitUntilAsync(library, l => l.Scripts.All(s => s.Name != "hello"), () => File.Delete(path));
        Assert.True(removed < TimeSpan.FromSeconds(1), $"removed after {removed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task An_edited_name_and_a_folder_created_later_are_picked_up()
    {
        string extra = Path.Combine(_root, "later");
        ScriptLibrary library = Create([extra]);
        Assert.True(library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Extra).Missing);

        await WaitUntilAsync(library, l => l.Scripts.Any(s => s.Name == "late"), () => Write(Path.Combine(extra, "late.js"), "1;"));
        Assert.False(library.Tree.Single(n => n.Root.Kind == ScriptRootKind.Extra).Missing);

        string path = Path.Combine(_folders.Scripts, "x.js");
        Write(path, "1;");
        await WaitUntilAsync(library, l => l.Scripts.Any(s => s.Name == "x"), () => { });
        await WaitUntilAsync(library, l => l.Scripts.Any(s => s.Name == "Renamed"), () => File.WriteAllText(path, "// ==HexEditorScript==\n// @name Renamed\n// ==/HexEditorScript==\n"));
    }

    private static async Task<TimeSpan> WaitUntilAsync(ScriptLibrary library, Func<ScriptLibrary, bool> condition, Action change)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, EventArgs e)
        {
            if (condition(library))
            {
                tcs.TrySetResult();
            }
        }

        library.Changed += OnChanged;
        try
        {
            var watch = Stopwatch.StartNew();
            change();
            if (condition(library))
            {
                return watch.Elapsed;
            }

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return watch.Elapsed;
        }
        finally
        {
            library.Changed -= OnChanged;
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-19-02")]
    public void Command_ids_are_valid_stable_and_unique()
    {
        Write(Path.Combine(_folders.Scripts, "hello.js"), Hello);
        Write(Path.Combine(_folders.Scripts, "sub", "hello.js"), Hello);
        Write(Path.Combine(_folders.Scripts, "日本語.js"), "1;");
        Write(Path.Combine(_folders.Scripts, "1 2.js"), "1;");
        Write(Path.Combine(_folders.Settings, "macros", "hello.js"), Hello);

        var ids = Create().CommandScripts.Select(ScriptCommandIds.For).ToList();

        Assert.Equal(5, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(CommandDefinition.IsValidId(id), id));
        Assert.All(ids, id => Assert.StartsWith(ScriptCommandIds.Prefix, id, StringComparison.Ordinal));
        // 同じファイルは作り直しても同じ ID (ショートカットの割り当てが残る)。
        Assert.Equal(ids.Order(), Create().CommandScripts.Select(ScriptCommandIds.For).Order());
    }

    [Fact]
    public void Scripts_can_be_created_renamed_and_samples_are_copied_without_changing_the_original()
    {
        string path = ScriptLibrary.CreateScript(_folders.Scripts, ScriptLanguage.JavaScript, "my script");
        Assert.EndsWith("my script.js", path, StringComparison.Ordinal);
        Assert.Contains("@name  my script", File.ReadAllText(path), StringComparison.Ordinal);
        string second = ScriptLibrary.CreateScript(_folders.Scripts, ScriptLanguage.CSharp, "my script");
        Assert.EndsWith("my script.csx", second, StringComparison.Ordinal);
        Assert.Equal(ScriptLibrary.CreateScript(_folders.Scripts, ScriptLanguage.JavaScript, "my script"), Path.Combine(_folders.Scripts, "my script (2).js"));

        string renamed = ScriptLibrary.Rename(path, "better:name");
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(renamed));
        Assert.EndsWith("better_name.js", renamed, StringComparison.Ordinal);
        Assert.Throws<IOException>(() => ScriptLibrary.Rename(Path.Combine(_folders.Scripts, "my script (2).js"), "better_name"));

        string sample = Write(Path.Combine(_root, "samples", "xor.js"), Hello);
        File.SetAttributes(sample, FileAttributes.ReadOnly);
        try
        {
            string copy = ScriptLibrary.CopySample(sample, _folders.Scripts);
            File.AppendAllText(copy, "// edited\n");
            Assert.Equal(Hello, File.ReadAllText(sample));
            Assert.NotEqual(sample, copy);
            Assert.Equal(Path.Combine(_folders.Scripts, "xor (2).js"), ScriptLibrary.CopySample(sample, _folders.Scripts));
        }
        finally
        {
            File.SetAttributes(sample, FileAttributes.Normal);
        }
    }
}
