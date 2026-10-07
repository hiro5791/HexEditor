using System.Text.Json.Nodes;
using HexEditor.Core.Settings;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Settings;

/// <summary>UI-23 設定の保存形式。</summary>
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
