using System.Text.Json.Nodes;
using HexEditor.Core.Settings;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Settings;

/// <summary>UI-23 設定の保存形式。外部の編集を 1 秒以内に読み直すこと (仕様 7) を時間で確かめるので、他のテストと並列に動かさない。</summary>
[Collection(SerialCollection.Name)]
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-settings").FullName;

    [Fact]
    public void WritesOnlyNonDefaultValuesWithSchemaHeader()
    {
        using (var store = new SettingsStore(_dir, "file:///schema.json"))
        {
            store.Load();
            store.SetString("ui.theme", "dark", "system");
            store.SetInt("view.zoom.hex", 100, 100);
            store.Flush();
        }

        JsonObject json = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, SettingsStore.FileName)))!.AsObject();
        Assert.Equal(["$schema", "$schemaVersion", "ui.theme"], json.Select(p => p.Key));
        Assert.Equal(1, json["$schemaVersion"]!.GetValue<int>());

        // 既定に戻すとキーが消える。
        using (var store = new SettingsStore(_dir))
        {
            store.Load();
            Assert.Equal("dark", store.GetString("ui.theme", "system"));
            store.SetString("ui.theme", "system", "system");
            store.Flush();
        }

        json = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, SettingsStore.FileName)))!.AsObject();
        Assert.Equal(["$schemaVersion"], json.Select(p => p.Key));
    }

    [Fact]
    [Trait(TC, "TC-UI-23-04")]
    public void KeepsUnknownKeys()
    {
        File.WriteAllText(Path.Combine(_dir, SettingsStore.FileName), """{ "$schemaVersion": 1, "future.key": [1, 2], "ui.theme": "light" }""");
        using var store = new SettingsStore(_dir);
        Assert.Equal(SettingsLoadStatus.Ok, store.Load());
        store.SetString("ui.theme", "dark", "system");
        store.Flush();

        JsonObject json = JsonNode.Parse(File.ReadAllText(store.PathName))!.AsObject();
        Assert.Equal("[1,2]", json["future.key"]!.ToJsonString());
        Assert.Equal("dark", json["ui.theme"]!.GetValue<string>());
    }

    [Fact]
    public void BrokenFileIsKeptAndDefaultsAreUsed()
    {
        File.WriteAllText(Path.Combine(_dir, SettingsStore.FileName), "{ not json");
        using var store = new SettingsStore(_dir);
        Assert.Equal(SettingsLoadStatus.Broken, store.Load());
        Assert.Equal("system", store.GetString("ui.theme", "system"));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.broken-*"));
    }

    [Fact]
    public void TooNewFileIsReadButNotWritten()
    {
        string original = """{ "$schemaVersion": 99, "ui.theme": "dark" }""";
        File.WriteAllText(Path.Combine(_dir, SettingsStore.FileName), original);
        using var store = new SettingsStore(_dir);
        Assert.Equal(SettingsLoadStatus.TooNew, store.Load());
        Assert.Equal("dark", store.GetString("ui.theme", "system"));
        store.SetString("ui.theme", "light", "system");
        store.Flush();
        Assert.Equal(original, File.ReadAllText(store.PathName));
    }

    [Fact]
    public async Task WritesAfterDelayAndReloadsExternalEdits()
    {
        using var store = new SettingsStore(_dir);
        store.Load();
        store.StartWatching();
        store.SetBool("log.debug", true, false);
        await Task.Delay(SettingsStore.WriteDelay + TimeSpan.FromMilliseconds(500));
        Assert.True(File.Exists(store.PathName));

        var changed = new TaskCompletionSource<IReadOnlyCollection<string>>();
        store.Changed += keys => changed.TrySetResult(keys);
        File.WriteAllText(store.PathName, """{ "$schemaVersion": 1, "ui.theme": "light" }""");
        Task done = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.True(done == changed.Task, "外部の変更を 1 秒以内に読み直す");
        Assert.Equal("light", store.GetString("ui.theme", "system"));
    }

    [Fact]
    public void Broken_file_path_is_reported_for_the_open_file_button()
    {
        File.WriteAllText(Path.Combine(_dir, SettingsStore.FileName), "[1, 2]");
        using var store = new SettingsStore(_dir);
        Assert.Equal(SettingsLoadStatus.Broken, store.Load());
        Assert.NotNull(store.BrokenFilePath);
        Assert.Equal("[1, 2]", File.ReadAllText(store.BrokenFilePath!));
    }

    [Fact]
    public void Write_failure_is_reported_once_and_does_not_throw()
    {
        // 設定フォルダの場所にファイルがある (フォルダを作れない) = 書き込めない。
        string blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, string.Empty);
        using var store = new SettingsStore(blocked);
        store.Load();
        var failures = new List<string>();
        store.WriteFailed += failures.Add;

        store.SetString("ui.theme", "dark", "system");
        store.Flush();
        store.SetString("ui.theme", "light", "system");
        Assert.False(store.SaveNow());

        Assert.Single(failures);
        Assert.True(store.HasPendingChanges);
        Assert.Equal("light", store.GetString("ui.theme", "system"));
    }

    [Fact]
    public async Task Write_failure_on_the_timer_thread_does_not_crash()
    {
        string blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, string.Empty);
        using var store = new SettingsStore(blocked);
        var failed = new TaskCompletionSource<string>();
        store.WriteFailed += m => failed.TrySetResult(m);
        store.SetBool("log.debug", true, false);
        Task done = await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(done == failed.Task, "書き込みの失敗を知らせる");
    }

    [Fact]
    public void Unreadable_file_is_not_overwritten()
    {
        string path = Path.Combine(_dir, SettingsStore.FileName);
        File.WriteAllText(path, """{ "$schemaVersion": 1, "ui.theme": "dark" }""");
        using var store = new SettingsStore(_dir);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SettingsLoadStatus.Unreadable, store.Load());
        }

        Assert.NotNull(store.LoadError);
        Assert.True(store.ReadOnly);
        store.SetString("ui.theme", "light", "system");
        store.Flush();
        Assert.Contains("dark", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("\"1\"", SettingsLoadStatus.Ok)]
    [InlineData("1.0", SettingsLoadStatus.Ok)]
    [InlineData("1.5", SettingsLoadStatus.TooNew)]
    [InlineData("\"abc\"", SettingsLoadStatus.Ok)]
    [InlineData("null", SettingsLoadStatus.Ok)]
    [InlineData("{}", SettingsLoadStatus.Ok)]
    public void Non_integer_schema_versions_are_read_without_crashing(string version, SettingsLoadStatus expected)
    {
        File.WriteAllText(Path.Combine(_dir, SettingsStore.FileName), $$"""{ "$schemaVersion": {{version}}, "ui.theme": "dark" }""");
        using var store = new SettingsStore(_dir);
        Assert.Equal(expected, store.Load());
        Assert.Equal("dark", store.GetString("ui.theme", "system"));
    }

    [Fact]
    public async Task Reloading_external_edits_keeps_unsaved_changes()
    {
        using var store = new SettingsStore(_dir);
        store.Load();
        store.SaveNow();
        store.StartWatching();
        var changed = new TaskCompletionSource<IReadOnlyCollection<string>>();
        store.Changed += keys =>
        {
            if (keys.Contains("ui.theme"))
            {
                changed.TrySetResult(keys);
            }
        };

        // まだ書いていない変更 (書き込みは 500 ms 後) があるうちに、外部で別のキーを書き換える。
        store.SetBool("log.debug", true, false);
        File.WriteAllText(store.PathName, """{ "$schemaVersion": 1, "ui.theme": "light" }""");
        Task done = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(done == changed.Task, "外部の変更を読み直す");
        Assert.Equal("light", store.GetString("ui.theme", "system"));
        Assert.True(store.GetBool("log.debug", false));

        store.Flush();
        JsonObject json = JsonNode.Parse(File.ReadAllText(store.PathName))!.AsObject();
        Assert.Equal("light", json["ui.theme"]!.GetValue<string>());
        Assert.True(json["log.debug"]!.GetValue<bool>());
    }

    [Fact]
    public void Writing_before_the_change_notice_keeps_the_external_edit()
    {
        // 変更の通知の処理が遅れ (混んだ PC)、通知を読み直す前にアプリ内の変更を書いても、外部の編集を上書きしない。
        using var store = new SettingsStore(_dir);
        store.Load();
        store.SaveNow();
        store.StartWatching();
        store.SetBool("log.debug", true, false);
        File.WriteAllText(store.PathName, """{ "$schemaVersion": 1, "ui.theme": "light" }""");
        store.Flush();

        Assert.Equal("light", store.GetString("ui.theme", "system"));
        JsonObject json = JsonNode.Parse(File.ReadAllText(store.PathName))!.AsObject();
        Assert.Equal("light", json["ui.theme"]!.GetValue<string>());
        Assert.True(json["log.debug"]!.GetValue<bool>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
