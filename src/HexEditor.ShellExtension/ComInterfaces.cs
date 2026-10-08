using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace HexEditor.ShellExtension;

// Explorer との間の COM のインターフェイス (Windows SDK の shobjidl_core.h、unknwn.h と同じ並び)。
// NativeAOT で使えるよう、ソース生成の COM (GeneratedComInterface) で宣言する。すべて [PreserveSig] で HRESULT を返し、
// 例外を呼び出し元 (Explorer) に伝えない (UI-55 の「エラー」)。使わない引数は nint のまま受け取る。

/// <summary>IExplorerCommand: 右クリックメニューの項目 1 つ。</summary>
[GeneratedComInterface]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
internal partial interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(IShellItemArray? items, out nint name);

    [PreserveSig]
    int GetIcon(IShellItemArray? items, out nint icon);

    [PreserveSig]
    int GetToolTip(IShellItemArray? items, out nint toolTip);

    [PreserveSig]
    int GetCanonicalName(out Guid name);

    [PreserveSig]
    int GetState(IShellItemArray? items, int okToBeSlow, out uint state);

    [PreserveSig]
    int Invoke(IShellItemArray? items, nint bindContext);

    [PreserveSig]
    int GetFlags(out uint flags);

    [PreserveSig]
    int EnumSubCommands(out nint enumerator);
}

/// <summary>IShellItemArray: 選んだ項目の一覧。</summary>
[GeneratedComInterface]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal partial interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);

    [PreserveSig]
    int GetPropertyStore(int flags, in Guid riid, out nint result);

    [PreserveSig]
    int GetPropertyDescriptionList(nint keyType, in Guid riid, out nint result);

    [PreserveSig]
    int GetAttributes(int attributeFlags, uint mask, out uint attributes);

    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetItemAt(uint index, out IShellItem? item);

    [PreserveSig]
    int EnumItems(out nint enumerator);
}

/// <summary>IShellItem: 項目 1 つ (ファイル、または仮想フォルダの項目)。</summary>
[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint bindContext, in Guid handler, in Guid riid, out nint result);

    [PreserveSig]
    int GetParent(out nint parent);

    [PreserveSig]
    int GetDisplayName(uint form, out nint name);

    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    [PreserveSig]
    int Compare(nint other, uint hint, out int order);
}

/// <summary>IClassFactory: DllGetClassObject が返すクラスの生成元。</summary>
[GeneratedComInterface]
[Guid("00000001-0000-0000-c000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, in Guid riid, out nint result);

    [PreserveSig]
    int LockServer(int lockServer);
}

/// <summary>HRESULT と定数。</summary>
internal static class HResults
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    /// <summary>SIGDN_FILESYSPATH: ファイルシステムのパス (仮想フォルダの項目では失敗する)。</summary>
    public const uint SigdnFileSysPath = 0x80058000;

    /// <summary>ECS_ENABLED。</summary>
    public const uint EcsEnabled = 0;

    /// <summary>ECF_DEFAULT (サブメニューなし)。</summary>
    public const uint EcfDefault = 0;
}
