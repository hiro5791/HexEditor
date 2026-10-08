using System.Xml.Linq;
using HexEditor.Platform.Shell;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// MSIX のマニフェスト (Package.appxmanifest) の宣言の検査 (10 の PKG-03、09 の UI-55)。インストールした後の動作は
/// build/tests/Test-Msix.ps1 (CI だけ) で確かめる。ここではインストールせずに宣言の内容だけを見る。
/// </summary>
public sealed class ManifestTests
{
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";

    private static XDocument Manifest() => XDocument.Load(RepoFile("src/HexEditor.App/Package.appxmanifest"));

    private static IReadOnlyList<string> FileTypes(XDocument manifest, string name) =>
    [
        .. manifest.Descendants(Uap + "FileTypeAssociation").Where(e => (string?)e.Attribute("Name") == name)
            .SelectMany(e => e.Descendants(Uap + "FileType")).Select(e => e.Value.Trim()),
    ];

    [Fact]
    [Trait(TC, "TC-PKG-03-01")]
    public void ManifestDeclaresTheFileTypeAssociations()
    {
        XDocument manifest = Manifest();

        // 独自の形式 (.hexproj、.hexworkspace) は既定のアプリの候補 (PKG-03 の仕様 1、UI-56 の仕様 1)。
        Assert.Equal([".hexproj", ".hexworkspace"], FileTypes(manifest, "hexeditor.project"));

        // バイナリ形式は「プログラムから開く」の候補 (UI-56 の仕様 2 の 7 つ。インストーラ版・ポータブル版の既定の一覧と同じ)。
        Assert.Equal(ShellRegistration.DefaultOpenWithExtensions, FileTypes(manifest, "hexeditor.binary"));

        // 関連付けは 2 つだけ。表示名は言語ごとのリソース (英語と日本語にある)。
        XElement[] associations = [.. manifest.Descendants(Uap + "FileTypeAssociation")];
        Assert.Equal(2, associations.Length);
        foreach (XElement association in associations)
        {
            string displayName = association.Element(Uap + "DisplayName")!.Value;
            Assert.StartsWith("ms-resource:", displayName);
            foreach (string lang in new[] { "en", "ja" })
            {
                string resw = File.ReadAllText(RepoFile($"src/HexEditor.App/Strings/{lang}/Resources.resw"));
                Assert.Contains($"name=\"{displayName["ms-resource:".Length..]}\"", resw);
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-UI-55-01")]
    public void ManifestRegistersOneExplorerCommandForEveryFile()
    {
        XDocument manifest = Manifest();

        // desktop4:FileExplorerContextMenus + desktop5:ItemType Type="*" に動詞が 1 つだけ (UI-55 の仕様 5: サブメニューを作らない)。
        XElement menus = Assert.Single(manifest.Descendants(Desktop4 + "FileExplorerContextMenus"));
        XElement itemType = Assert.Single(menus.Elements(Desktop5 + "ItemType"));
        Assert.Equal("*", (string?)itemType.Attribute("Type"));
        XElement verb = Assert.Single(itemType.Elements(Desktop5 + "Verb"));
        Guid clsid = Guid.Parse((string)verb.Attribute("Clsid")!);
        Assert.Equal(ShellExtension.ExplorerCommand.Clsid, clsid);

        // 動詞の CLSID は com:SurrogateServer の COM クラス (PKG-03 の仕様 1) で、DLL はパッケージの中のもの。
        XElement surrogate = Assert.Single(manifest.Descendants(Com + "SurrogateServer"));
        XElement comClass = Assert.Single(surrogate.Elements(Com + "Class"));
        Assert.Equal(clsid, Guid.Parse((string)comClass.Attribute("Id")!));
        Assert.Equal(ShellExtension.ExplorerCommand.DllName, (string?)comClass.Attribute("Path"));
        Assert.Equal("STA", (string?)comClass.Attribute("ThreadingModel"));

        // 宣言した名前空間は無視できる名前空間に入れる (古い Windows でもインストールできるように)。
        string ignorable = (string)manifest.Root!.Attribute("IgnorableNamespaces")!;
        Assert.All(new[] { "desktop4", "desktop5", "com" }, prefix => Assert.Contains(prefix, ignorable.Split(' ')));

        // DLL は MSIX 版のビルドでだけ作って入れる (build/publish.ps1 が NativeAOT で発行し、HexEditor.App.csproj が取り込む)。
        string publish = File.ReadAllText(RepoFile("build/publish.ps1"));
        Assert.Contains("HexEditor.ShellExtension", publish);
        Assert.Contains("HexShellExtensionDir", publish);
        string project = File.ReadAllText(RepoFile("src/HexEditor.App/HexEditor.App.csproj"));
        Assert.Contains(ShellExtension.ExplorerCommand.DllName, project);
    }
}
