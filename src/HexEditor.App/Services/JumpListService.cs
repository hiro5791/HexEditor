using System.Runtime.InteropServices;
using HexEditor.Platform.Shell;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.Services;

/// <summary>
/// タスクバーのジャンプリスト (09 の UI-35)。内容は <see cref="JumpListPlan"/> で決め、Win32 の <c>ICustomDestinationList</c> で
/// アプリの AppUserModelID (PKG-12) に関連付ける。最近使ったファイルの一覧が変わるたびに 500 ms まとめて作り直す。
/// 設定 <c>shell.jumpList.enabled</c> が false なら一覧を消す。失敗は利用者に知らせずログに書く。
/// テスト用のビルドを --test-hooks で動かしているときは、Windows のジャンプリストを変えずに、作った内容を記録するだけにする
/// (<see cref="LastPlan"/>。利用者の PC の状態を変えない)。
/// </summary>
public sealed class JumpListService
{
    private readonly IRecentFilesSource _source;
    private readonly string _appId;
    private readonly string _exe;
    private readonly DispatcherQueueTimer _timer;

    public JumpListService(IRecentFilesSource source, string appId, string exe, DispatcherQueue dispatcher)
    {
        _source = source;
        _appId = appId;
        _exe = exe;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = JumpListPlan.Debounce;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Update();
        _source.Changed += (_, _) => dispatcher.TryEnqueue(Schedule);
    }

    /// <summary>最後に作った内容 (テスト用)。</summary>
    public IReadOnlyList<JumpListItem> LastPlan { get; private set; } = [];

    public static bool Enabled => App.Settings.GetBool(JumpListPlan.EnabledKey, true);

    /// <summary>500 ms 後に作り直す (その間の変更はまとめる)。</summary>
    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Update()
    {
        try
        {
            if (!Enabled)
            {
                LastPlan = [];
                if (!TestHooks.Active)
                {
                    Native.Delete(_appId);
                }

                return;
            }

            LastPlan = JumpListPlan.Build(_source, id => Loc.Get("JumpList_Task_" + id));
            if (!TestHooks.Active)
            {
                Native.Commit(_appId, _exe, LastPlan, Loc.Get("JumpList_Pinned"), Loc.Get("JumpList_Recent"));
            }
        }
        catch (Exception ex)
        {
            // 失敗しても利用者には知らせない (UI-35 の「エラー」)。COM の失敗の種類を問わずログだけにする。
            AppLog.Warning($"Jump list update failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    /// <summary>
    /// ジャンプリストを消す (ポータブル版の「この PC から登録を解除」。10 の PKG-09 の仕様 4)。テスト用のビルドで異常を再現する仕組みが
    /// 有効なときは、ジャンプリストを作らないため何もしない。失敗は例外のまま呼び出し元に返す (失敗した項目の一覧に出す)。
    /// </summary>
    public static void DeleteList(string appId)
    {
        if (!TestHooks.Active)
        {
            Native.Delete(appId);
        }
    }

    /// <summary>ICustomDestinationList の呼び出し。</summary>
    private static class Native
    {
        private static readonly Guid ClsidDestinationList = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
        private static readonly Guid ClsidEnumerableObjectCollection = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
        private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
        private static readonly Guid IidObjectArray = new("92CA9DCD-5622-4bba-A805-5E9F541BD8C9");

        public static void Commit(string appId, string exe, IReadOnlyList<JumpListItem> items, string pinnedTitle, string recentTitle)
        {
            var list = (ICustomDestinationList)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidDestinationList)!)!;
            try
            {
                list.SetAppID(appId);
                Guid iid = IidObjectArray;
                Marshal.ThrowExceptionForHR(list.BeginList(out _, ref iid, out object removed));
                Marshal.ReleaseComObject(removed);
                foreach ((JumpListCategory category, string title) in new[] { (JumpListCategory.Pinned, pinnedTitle), (JumpListCategory.Recent, recentTitle) })
                {
                    List<JumpListItem> inCategory = [.. items.Where(i => i.Category == category)];
                    if (inCategory.Count > 0)
                    {
                        // 項目が削除済みの一覧にあると E_ACCESSDENIED になる。その分類だけ飛ばす (仕様の「エラー」: ログに書く)。
                        int hr = list.AppendCategory(title, Collection(exe, inCategory));
                        if (hr < 0)
                        {
                            AppLog.Warning($"Jump list category {category} was not added: 0x{hr:X8}");
                        }
                    }
                }

                List<JumpListItem> tasks = [.. items.Where(i => i.Category == JumpListCategory.Tasks)];
                if (tasks.Count > 0)
                {
                    Marshal.ThrowExceptionForHR(list.AddUserTasks(Collection(exe, tasks)));
                }

                list.CommitList();
            }
            catch
            {
                try
                {
                    list.AbortList();
                }
                catch (COMException)
                {
                }

                throw;
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }

        public static void Delete(string appId)
        {
            var list = (ICustomDestinationList)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidDestinationList)!)!;
            try
            {
                list.DeleteList(appId);
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }

        private static IObjectArray Collection(string exe, IEnumerable<JumpListItem> items)
        {
            var collection = (IObjectCollection)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidEnumerableObjectCollection)!)!;
            foreach (JumpListItem item in items)
            {
                var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellLink)!)!;
                link.SetPath(exe);
                link.SetArguments(item.Arguments);
                link.SetIconLocation(exe, 0);
                link.SetDescription(item.FilePath ?? item.Title);
                SetTitle((IPropertyStore)link, item.Title);
                collection.AddObject(link);
            }

            return (IObjectArray)collection;
        }

        /// <summary>System.Title (ジャンプリストに出る名前)。</summary>
        private static void SetTitle(IPropertyStore store, string title)
        {
            var key = new PropertyKey { FormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), PropertyId = 2 };
            var value = new PropVariant { VarType = 31, Pointer = Marshal.StringToCoTaskMemUni(title) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort VarType;

        [FieldOffset(8)]
        public nint Pointer;
    }

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);

        [PreserveSig]
        int BeginList(out uint pcMinSlots, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [PreserveSig]
        int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);

        void AppendKnownCategory(int category);

        [PreserveSig]
        int AddUserTasks(IObjectArray poa);

        void CommitList();

        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);

        void AbortList();
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint pcObjects);

        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport]
    [Guid("5632B1A4-E38A-400a-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        void GetCount(out uint pcObjects);

        void GetAt(uint uiIndex, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void AddObject([MarshalAs(UnmanagedType.Interface)] object pvObject);

        void AddFromArray(IObjectArray poaSource);

        void RemoveObjectAt(uint uiIndex);

        void Clear();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, nint pfd, uint fFlags);

        void GetIDList(out nint ppidl);

        void SetIDList(nint pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(nint hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);

        void GetAt(uint iProp, out PropertyKey pkey);

        void GetValue(ref PropertyKey key, out PropVariant pv);

        void SetValue(ref PropertyKey key, ref PropVariant pv);

        void Commit();
    }
}
