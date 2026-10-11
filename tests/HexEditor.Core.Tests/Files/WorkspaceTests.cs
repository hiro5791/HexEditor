using System.Text.Json;
using HexEditor.Core.Files;

namespace HexEditor.Core.Tests.Files;

/// <summary>UI-33 ワークスペースのファイル。</summary>
public sealed class WorkspaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-workspace").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Paths_are_relative_and_the_folder_can_be_moved()
    {
        string a = Path.Combine(_dir, "proj", "files", "a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.WriteAllBytes(a, [1]);
        string workspace = Path.Combine(_dir, "proj", "w.hexworkspace");
        var window = new SessionWindow
        {
            Tabs =
            [
                new SessionTab { Path = a, DisplayName = "a.bin", Cursor = 5 },
                new SessionTab { Kind = SessionTabKind.File, Path = null, DisplayName = "Untitled 1" },
            ],
            ActiveTab = 0,
            Panels = JsonDocument.Parse("{\"right\":[\"inspector\"]}").RootElement,
        };
        WorkspaceFile.FromWindow(window, workspace).Save(workspace);
        Assert.DoesNotContain(_dir.Replace("\\", "\\\\"), File.ReadAllText(workspace));

        string moved = Path.Combine(_dir, "moved");
        Directory.Move(Path.Combine(_dir, "proj"), moved);
        SessionWindow restored = WorkspaceFile.Load(Path.Combine(moved, "w.hexworkspace")).ToWindow(Path.Combine(moved, "w.hexworkspace"));
        SessionTab tab = Assert.Single(restored.Tabs);
        Assert.Equal(Path.Combine(moved, "files", "a.bin"), tab.Path);
        Assert.True(File.Exists(tab.Path));
        Assert.Equal(5, tab.Cursor);
        Assert.Equal(0, restored.ActiveTab);
        Assert.Equal("inspector", restored.Panels!.Value.GetProperty("right")[0].GetString());
    }
}
