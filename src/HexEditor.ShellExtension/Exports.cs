using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static HexEditor.ShellExtension.HResults;

namespace HexEditor.ShellExtension;

/// <summary>
/// COM サーバーの DLL の入口 (DllGetClassObject、DllCanUnloadNow)。NativeAOT で発行すると DLL の関数として公開される。
/// 例外は外に出さない (UI-55 の「エラー」)。
/// </summary>
public static unsafe class Exports
{
    internal static readonly StrategyBasedComWrappers Wrappers = new();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, nint* result)
    {
        try
        {
            if (result == null)
            {
                return E_POINTER;
            }

            *result = 0;
            if (clsid == null || iid == null)
            {
                return E_INVALIDARG;
            }

            return *clsid == ExplorerCommand.Clsid ? QueryInterface(new ClassFactory(), *iid, result) : CLASS_E_CLASSNOTAVAILABLE;
        }
        catch (Exception)
        {
            return E_FAIL;
        }
    }

    /// <summary>NativeAOT のランタイムは取り外せないため、読み込んだままにする。</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => S_FALSE;

    internal static int QueryInterface(object instance, Guid iid, nint* result)
    {
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            int hr = Marshal.QueryInterface(unknown, in iid, out nint pointer);
            *result = pointer;
            return hr;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}

/// <summary><see cref="ExplorerCommand"/> を作る IClassFactory。</summary>
[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    public unsafe int CreateInstance(nint outer, in Guid riid, out nint result)
    {
        result = 0;
        try
        {
            if (outer != 0)
            {
                return CLASS_E_NOAGGREGATION;
            }

            nint pointer;
            int hr = Exports.QueryInterface(new ExplorerCommand(), riid, &pointer);
            result = pointer;
            return hr;
        }
        catch (Exception)
        {
            result = 0;
            return E_FAIL;
        }
    }

    public int LockServer(int lockServer) => S_OK;
}
