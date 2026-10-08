using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static HexEditor.ShellExtension.HResults;

namespace HexEditor.ShellExtension;

/// <summary>メニューの表示とアプリの起動 (テストでは差し替える)。</summary>
internal interface ICommandHost
{
    /// <summary>メニューの名前「HexEditor で開く」(表示言語のリソース)。</summary>
    string Title();

    /// <summary>起動するアプリ (パッケージの中の HexEditor.exe)。アイコンもここから取る。</summary>
    string ExePath { get; }

    /// <summary>選んだファイルを引数にアプリを起動する (ファイルは読まない)。</summary>
    void Launch(IReadOnlyList<string> files);
}

/// <summary>
/// Windows 11 の新しい右クリックメニューの「HexEditor で開く」(09 の UI-55)。項目は 1 つだけで、サブメニューを作らない (仕様 5)。
/// <list type="bullet">
/// <item><c>GetTitle</c>: 表示言語のリソース (仕様 1)。</item>
/// <item><c>GetIcon</c>: アプリのアイコン (HexEditor.exe の最初のアイコン)。</item>
/// <item><c>GetState</c>: 常に ECS_ENABLED (対象はマニフェストの ItemType="*" ですべてのファイル)。</item>
/// <item><c>Invoke</c>: 選んだファイルを引数にアプリを起動する。複数のファイルは 1 回の起動にまとめる (UI-54 の仕様 3)。
/// ファイルシステムのパスがない項目 (仮想フォルダの項目) は除く。</item>
/// </list>
/// どのメソッドも例外を外に出さず、失敗は E_FAIL などの HRESULT で返す (UI-55 の「エラー」: Explorer に影響させない)。
/// </summary>
[GeneratedComClass]
[Guid(ClsidText)]
internal sealed partial class ExplorerCommand : IExplorerCommand
{
    /// <summary>COM クラスの CLSID (Package.appxmanifest の desktop5:Verb と com:Class に同じ値を書く)。</summary>
    public const string ClsidText = "2437333e-df0b-4c55-bdc1-be7265500b4d";

    /// <summary>パッケージの中の DLL の名前 (Package.appxmanifest の com:Class の Path)。</summary>
    public const string DllName = "HexEditor.ShellExtension.dll";

    public static readonly Guid Clsid = new(ClsidText);

    private readonly ICommandHost _host;

    public ExplorerCommand()
        : this(PackageCommandHost.Instance)
    {
    }

    internal ExplorerCommand(ICommandHost host) => _host = host;

    public int GetTitle(IShellItemArray? items, out nint name) => ReturnString(_host.Title, out name);

    public int GetIcon(IShellItemArray? items, out nint icon) => ReturnString(() => _host.ExePath + ",0", out icon);

    public int GetToolTip(IShellItemArray? items, out nint toolTip)
    {
        toolTip = 0;
        return E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid name)
    {
        name = Guid.Empty;
        return E_NOTIMPL;
    }

    public int GetState(IShellItemArray? items, int okToBeSlow, out uint state)
    {
        state = EcsEnabled;
        return S_OK;
    }

    public int Invoke(IShellItemArray? items, nint bindContext)
    {
        try
        {
            if (items is null)
            {
                return E_INVALIDARG;
            }

            IReadOnlyList<string> files = FilePaths(items);
            if (files.Count == 0)
            {
                // 仮想フォルダの項目だけ (開けるファイルがない)。
                return E_FAIL;
            }

            _host.Launch(files);
            return S_OK;
        }
        catch (Exception)
        {
            // Explorer に例外を伝えない (UI-55 の「エラー」)。
            return E_FAIL;
        }
    }

    public int GetFlags(out uint flags)
    {
        flags = EcfDefault;
        return S_OK;
    }

    public int EnumSubCommands(out nint enumerator)
    {
        // サブメニューを作らない (UI-55 の仕様 5)。
        enumerator = 0;
        return E_NOTIMPL;
    }

    /// <summary>選んだ項目のファイルシステムのパス。取れない項目は除く (削除済みのファイルはパスのまま渡し、アプリがエラーを出す)。</summary>
    internal static IReadOnlyList<string> FilePaths(IShellItemArray items)
    {
        var files = new List<string>();
        if (items.GetCount(out uint count) < 0)
        {
            return files;
        }

        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out IShellItem? item) < 0 || item is null)
            {
                continue;
            }

            try
            {
                if (item.GetDisplayName(SigdnFileSysPath, out nint name) >= 0 && name != 0)
                {
                    try
                    {
                        string? path = Marshal.PtrToStringUni(name);
                        if (!string.IsNullOrEmpty(path))
                        {
                            files.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(name);
                    }
                }
            }
            finally
            {
                ((object)item as ComObject)?.FinalRelease();
            }
        }

        return files;
    }

    /// <summary>文字列を CoTaskMemAlloc の領域で返す (呼び出し元が CoTaskMemFree で解放する)。</summary>
    private static int ReturnString(Func<string> value, out nint result)
    {
        result = 0;
        try
        {
            result = Marshal.StringToCoTaskMemUni(value());
            return S_OK;
        }
        catch (Exception)
        {
            result = 0;
            return E_FAIL;
        }
    }
}
