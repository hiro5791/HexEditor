using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using HexEditor.ShellExtension;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// Windows 11 の新しい右クリックメニューの COM サーバー (09 の UI-55)。Explorer の代わりにテストが IExplorerCommand を呼ぶ。
/// アプリの起動は偽物に差し替える (実際には起動しない)。Explorer での確認は build/tests/Test-Msix.ps1 (CI だけ)。
/// </summary>
public sealed unsafe class ShellExtensionTests
{
    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    private static readonly Guid IidClassFactory = new("00000001-0000-0000-c000-000000000046");
    private static readonly Guid IidExplorerCommand = new("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");

    private readonly FakeHost _host = new();

    private ExplorerCommand Command() => new(_host);

    private static string? TakeString(nint value)
    {
        try
        {
            return Marshal.PtrToStringUni(value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    [Fact]
    [Trait(TC, "TC-UI-55-04")]
    public void NullItemArrayReturnsHResultsWithoutThrowing()
    {
        ExplorerCommand command = Command();
        Assert.Equal(S_OK, command.GetTitle(null, out nint title));
        Assert.Equal("Open with HexEditor", TakeString(title));
        Assert.Equal(S_OK, command.GetState(null, 0, out uint state));
        Assert.Equal(0u, state); // ECS_ENABLED
        Assert.Equal(E_INVALIDARG, command.Invoke(null, 0));
        Assert.Empty(_host.Launches);
    }

    [Fact]
    [Trait(TC, "TC-UI-55-04")]
    public void DeletedFileIsPassedToTheAppWithoutReadingIt()
    {
        // Invoke はファイルを読まない (UI-55 の「巨大ファイル・長時間処理」)。削除済みのファイルもパスのまま渡し、アプリがエラーを出す。
        string deleted = Path.Combine(Path.GetTempPath(), "HexEditorPlatformTests", Guid.NewGuid().ToString("N"), "deleted.bin");
        ExplorerCommand command = Command();
        var items = new FakeItems(new FakeItem(deleted));
        Assert.Equal(S_OK, command.GetTitle(items, out nint title));
        Marshal.FreeCoTaskMem(title);
        Assert.Equal(S_OK, command.GetState(items, 1, out _));
        Assert.Equal(S_OK, command.Invoke(items, 0));
        Assert.Equal([deleted], Assert.Single(_host.Launches));
    }

    [Fact]
    [Trait(TC, "TC-UI-55-04")]
    public void VirtualFolderItemsAreSkipped()
    {
        // 仮想フォルダ (コントロールパネル) の項目はファイルシステムのパスを返さない。
        ExplorerCommand command = Command();
        var virtualOnly = new FakeItems(new FakeItem(null));
        Assert.Equal(S_OK, command.GetState(virtualOnly, 0, out _));
        Assert.Equal(E_FAIL, command.Invoke(virtualOnly, 0));
        Assert.Empty(_host.Launches);

        // ファイルと一緒なら、ファイルだけを 1 回の起動で渡す (複数のファイルは 1 つのウィンドウ。UI-54 の仕様 3)。
        var mixed = new FakeItems(new FakeItem(@"C:\data\a.bin"), new FakeItem(null), null, new FakeItem(@"C:\data\b.bin"));
        Assert.Equal(S_OK, command.Invoke(mixed, 0));
        Assert.Equal([@"C:\data\a.bin", @"C:\data\b.bin"], Assert.Single(_host.Launches));
    }

    [Fact]
    [Trait(TC, "TC-UI-55-04")]
    public void ExceptionsBecomeEFail()
    {
        // 項目の取得・アプリの起動・リソースの読み込みで例外が起きても、呼び出し元には HRESULT だけを返す。
        Assert.Equal(E_FAIL, Command().Invoke(new FakeItems(new ThrowingItem()), 0));
        Assert.Equal(E_FAIL, Command().Invoke(new ThrowingItems(), 0));

        _host.Fail = true;
        ExplorerCommand failing = Command();
        Assert.Equal(E_FAIL, failing.Invoke(new FakeItems(new FakeItem(@"C:\data\a.bin")), 0));
        Assert.Equal(E_FAIL, failing.GetTitle(null, out nint title));
        Assert.Equal(0, title);
        Assert.Equal(E_FAIL, failing.GetIcon(null, out nint icon));
        Assert.Equal(0, icon);
    }

    [Fact]
    [Trait(TC, "TC-UI-55-04")]
    [Trait(TC, "TC-UI-55-01")]
    public void ComServerCreatesTheCommandThroughTheClassFactory()
    {
        // Explorer (dllhost.exe) と同じく、DLL の DllGetClassObject → IClassFactory.CreateInstance → IExplorerCommand の
        // 関数表を通して呼ぶ。null の項目の配列ではアプリを起動しない。
        delegate* unmanaged<Guid*, Guid*, nint*, int> getClassObject = &Exports.DllGetClassObject;
        Guid clsid = ExplorerCommand.Clsid;
        Guid iidFactory = IidClassFactory;
        nint factoryPointer;
        Assert.Equal(S_OK, getClassObject(&clsid, &iidFactory, &factoryPointer));
        Assert.NotEqual(0, factoryPointer);

        Guid other = Guid.NewGuid();
        nint none;
        Assert.Equal(CLASS_E_CLASSNOTAVAILABLE, getClassObject(&other, &iidFactory, &none));
        Assert.Equal(0, none);

        nint commandPointer;
        try
        {
            var factory = (IClassFactory)Exports.Wrappers.GetOrCreateObjectForComInstance(factoryPointer, CreateObjectFlags.UniqueInstance);
            Assert.Equal(S_OK, factory.CreateInstance(0, IidExplorerCommand, out commandPointer));
        }
        finally
        {
            Marshal.Release(factoryPointer);
        }

        try
        {
            var command = (IExplorerCommand)Exports.Wrappers.GetOrCreateObjectForComInstance(commandPointer, CreateObjectFlags.UniqueInstance);
            Assert.Equal(S_OK, command.GetTitle(null, out nint title));
            Assert.False(string.IsNullOrWhiteSpace(TakeString(title)));
            Assert.Equal(S_OK, command.GetIcon(null, out nint icon));
            Assert.EndsWith("HexEditor.exe,0", TakeString(icon));
            Assert.Equal(S_OK, command.GetState(null, 0, out uint state));
            Assert.Equal(0u, state);
            Assert.Equal(E_INVALIDARG, command.Invoke(null, 0));
            Assert.Equal(S_OK, command.GetFlags(out uint flags));
            Assert.Equal(0u, flags); // ECF_DEFAULT: サブメニューなし (UI-55 の仕様 5)

            Assert.Equal(E_NOTIMPL, command.EnumSubCommands(out nint subCommands));
            Assert.Equal(0, subCommands);
        }
        finally
        {
            Marshal.Release(commandPointer);
        }
    }

    private sealed class FakeHost : ICommandHost
    {
        public List<IReadOnlyList<string>> Launches { get; } = [];

        public bool Fail { get; set; }

        public string ExePath => Fail ? throw new InvalidOperationException("exe") : @"C:\Program Files\WindowsApps\HexEditor\HexEditor.exe";

        public string Title() => Fail ? throw new InvalidOperationException("resources") : "Open with HexEditor";

        public void Launch(IReadOnlyList<string> files)
        {
            if (Fail)
            {
                throw new System.ComponentModel.Win32Exception(2);
            }

            Launches.Add([.. files]);
        }
    }
}

/// <summary>Explorer が渡す項目の配列の代わり。</summary>
[GeneratedComClass]
internal sealed partial class FakeItems(params IShellItem?[] items) : IShellItemArray
{
    public int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result) => NotImplemented(out result);

    public int GetPropertyStore(int flags, in Guid riid, out nint result) => NotImplemented(out result);

    public int GetPropertyDescriptionList(nint keyType, in Guid riid, out nint result) => NotImplemented(out result);

    public int GetAttributes(int attributeFlags, uint mask, out uint attributes)
    {
        attributes = 0;
        return unchecked((int)0x80004001);
    }

    public int GetCount(out uint count)
    {
        count = (uint)items.Length;
        return 0;
    }

    public int GetItemAt(uint index, out IShellItem? item)
    {
        item = items[index];
        return item is null ? unchecked((int)0x80004005) : 0;
    }

    public int EnumItems(out nint enumerator) => NotImplemented(out enumerator);

    internal static int NotImplemented(out nint result)
    {
        result = 0;
        return unchecked((int)0x80004001);
    }
}

/// <summary>項目 1 つ。<paramref name="path"/> が null なら仮想フォルダの項目 (ファイルシステムのパスがない)。</summary>
[GeneratedComClass]
internal sealed partial class FakeItem(string? path) : IShellItem
{
    public int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result) => FakeItems.NotImplemented(out result);

    public int GetParent(out nint parent) => FakeItems.NotImplemented(out parent);

    public int GetDisplayName(uint form, out nint name)
    {
        name = path is null ? 0 : Marshal.StringToCoTaskMemUni(path);
        return path is null ? unchecked((int)0x80070057) : 0;
    }

    public int GetAttributes(uint mask, out uint attributes)
    {
        attributes = 0;
        return 0;
    }

    public int Compare(nint other, uint hint, out int order)
    {
        order = 0;
        return 0;
    }
}

/// <summary>名前の取得で例外を投げる項目。</summary>
[GeneratedComClass]
internal sealed partial class ThrowingItem : IShellItem
{
    public int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result) => FakeItems.NotImplemented(out result);

    public int GetParent(out nint parent) => FakeItems.NotImplemented(out parent);

    public int GetDisplayName(uint form, out nint name) => throw new UnauthorizedAccessException();

    public int GetAttributes(uint mask, out uint attributes) => throw new InvalidOperationException();

    public int Compare(nint other, uint hint, out int order) => throw new InvalidOperationException();
}

/// <summary>数の取得で例外を投げる項目の配列。</summary>
[GeneratedComClass]
internal sealed partial class ThrowingItems : IShellItemArray
{
    public int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result) => FakeItems.NotImplemented(out result);

    public int GetPropertyStore(int flags, in Guid riid, out nint result) => FakeItems.NotImplemented(out result);

    public int GetPropertyDescriptionList(nint keyType, in Guid riid, out nint result) => FakeItems.NotImplemented(out result);

    public int GetAttributes(int attributeFlags, uint mask, out uint attributes) => throw new InvalidOperationException();

    public int GetCount(out uint count) => throw new InvalidOperationException("items");

    public int GetItemAt(uint index, out IShellItem? item) => throw new InvalidOperationException();

    public int EnumItems(out nint enumerator) => FakeItems.NotImplemented(out enumerator);
}
