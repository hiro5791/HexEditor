# Explorer integration helpers for the distribution tests (UI-54, UI-55, UI-56, PKG-03; CI runners only).
# The new Windows 11 context menu is driven with the keyboard and UI Automation, which needs the foreground:
# never run these functions on a desktop that someone is using.
# Dot-source after TestCase.ps1:  . "$PSScriptRoot/Explorer.ps1"

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# The COM class of the new context menu (UI-55): the same value as ExplorerCommand.ClsidText and Package.appxmanifest.
$script:ExplorerCommandClsid = [guid]'2437333e-df0b-4c55-bdc1-be7265500b4d'

if (-not ('HexTest.ExplorerNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HexTest
{
    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IntPtr items, out IntPtr name);
        [PreserveSig] int GetIcon(IntPtr items, out IntPtr icon);
        [PreserveSig] int GetToolTip(IntPtr items, out IntPtr toolTip);
        [PreserveSig] int GetCanonicalName(out Guid name);
        [PreserveSig] int GetState(IntPtr items, int okToBeSlow, out uint state);
        [PreserveSig] int Invoke(IntPtr items, IntPtr bindContext);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IntPtr enumerator);
    }

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IAssocHandler handler, out uint fetched);
    }

    [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    }

    public static class ExplorerNative
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHAssocEnumHandlers(string extension, int filter, out IEnumAssocHandlers handlers);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int AssocQueryStringW(int flags, int str, string assoc, string extra, StringBuilder output, ref int length);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        // A process that is not in the foreground may not move the foreground (the foreground lock), and the keys of
        // PressApplicationKey then go to another window. A key press of Alt before SetForegroundWindow lifts the lock.
        public static bool BringToForeground(IntPtr window)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(window);
                if (GetForegroundWindow() == window) { return true; }
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        // "Open with" handlers of an extension (ASSOC_FILTER_NONE): "<name>|<UI name>".
        public static string[] OpenWithHandlers(string extension)
        {
            var result = new System.Collections.Generic.List<string>();
            IEnumAssocHandlers handlers;
            if (SHAssocEnumHandlers(extension, 0, out handlers) != 0 || handlers == null) { return result.ToArray(); }
            IAssocHandler handler;
            uint fetched;
            while (handlers.Next(1, out handler, out fetched) == 0 && fetched == 1)
            {
                string name, uiName;
                if (handler.GetName(out name) != 0) { name = ""; }
                if (handler.GetUIName(out uiName) != 0) { uiName = ""; }
                result.Add(name + "|" + uiName);
                Marshal.ReleaseComObject(handler);
            }
            Marshal.ReleaseComObject(handlers);
            return result.ToArray();
        }

        // The default app of a file type (ASSOCSTR_EXECUTABLE), or "" if there is none.
        public static string DefaultApp(string extension)
        {
            int length = 1024;
            var output = new StringBuilder(length);
            return AssocQueryStringW(0, 2, extension, "open", output, ref length) == 0 ? output.ToString() : "";
        }

        // The Application key (VK_APPS) opens the new context menu; Shift+F10 opens the classic one (TC-UI-55-01).
        public static void PressApplicationKey()
        {
            keybd_event(0x5D, 0, 0, UIntPtr.Zero);
            keybd_event(0x5D, 0, 2, UIntPtr.Zero);
        }

        // Creates the packaged COM class like Explorer does and calls it with a null item array:
        // title, GetState HRESULT, state, Invoke HRESULT, flags.
        public static string[] ProbeExplorerCommand(Guid clsid)
        {
            object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true));
            try
            {
                var command = (IExplorerCommand)instance;
                IntPtr name;
                int hr = command.GetTitle(IntPtr.Zero, out name);
                string title = hr >= 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name) : "";
                if (name != IntPtr.Zero) { Marshal.FreeCoTaskMem(name); }
                uint state;
                int stateResult = command.GetState(IntPtr.Zero, 0, out state);
                int invokeResult = command.Invoke(IntPtr.Zero, IntPtr.Zero);
                uint flags;
                command.GetFlags(out flags);
                return new[] { title, stateResult.ToString(), state.ToString(), invokeResult.ToString(), flags.ToString() };
            }
            finally
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }
}
'@
}

# The classic context menu verbs of a file, as Explorer shows them (Shell.Application, the same IContextMenu verbs).
function Get-ContextMenuVerbs([string]$Path) {
    $shell = New-Object -ComObject Shell.Application
    $item = $shell.NameSpace((Split-Path -Parent $Path)).ParseName((Split-Path -Leaf $Path))
    @($item.Verbs() | ForEach-Object { $_ })
}

function Get-HexEditorVerb([string]$Path) {
    Get-ContextMenuVerbs $Path | Where-Object { ($_.Name -replace '&', '') -like '*HexEditor*' } | Select-Object -First 1
}

# Packages with an identity for HexEditor (the MSIX version, or a sparse package of UI-55 spec 2).
function Get-HexEditorPackages { @(Get-AppxPackage | Where-Object { $_.Name -like '*HexEditor*' }) }

# HexEditor processes whose command line contains the file name (the app was started for that file).
function Get-HexEditorFor([string]$FileName) {
    @(Get-CimInstance Win32_Process -Filter "Name = 'HexEditor.exe'" | Where-Object { $_.CommandLine -like "*$FileName*" })
}

function Get-ExplorerIds { @(Get-Process explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id } | Sort-Object) }

# Opens a File Explorer window with the file selected and returns its UI Automation element.
function Open-ExplorerSelection([string]$Path) {
    $folder = Split-Path -Leaf (Split-Path -Parent $Path)
    Start-Process explorer.exe -ArgumentList "/select,`"$Path`""
    $e = [System.Windows.Automation.AutomationElement]
    $condition = New-Object System.Windows.Automation.PropertyCondition($e::ClassNameProperty, 'CabinetWClass')
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $window = @($e::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition) | Where-Object { $_.Current.Name -like "*$folder*" }) | Select-Object -First 1
        if ($window) {
            if (-not [HexTest.ExplorerNative]::BringToForeground([IntPtr]$window.Current.NativeWindowHandle)) {
                Write-Host "The File Explorer window of $folder is not in the foreground."
            }
            Start-Sleep -Seconds 2
            return $window
        }
        Start-Sleep -Milliseconds 250
    }
    throw "No File Explorer window for $folder."
}

function Close-ExplorerWindow($Window) {
    try { $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
}

# Opens the new context menu of the selected file with the Application key and returns the menu items whose
# name is $Name, each with its parent menu and whether it opens a submenu.
function Get-NewContextMenuItems([string]$Name, [int]$Seconds = 15) {
    [HexTest.ExplorerNative]::PressApplicationKey()
    $e = [System.Windows.Automation.AutomationElement]
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($e::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)),
        (New-Object System.Windows.Automation.PropertyCondition($e::NameProperty, $Name)))
    $explorer = Get-ExplorerIds
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $found = @()
        foreach ($id in $explorer) {
            $windows = $e::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition($e::ProcessIdProperty, $id)))
            foreach ($w in $windows) {
                foreach ($item in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
                    $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
                    $patterns = @($item.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName })
                    $found += [pscustomobject]@{
                        Element    = $item
                        ParentType = if ($parent) { $parent.Current.ControlType.ProgrammaticName } else { '' }
                        Submenu    = ($patterns -contains 'ExpandCollapsePatternIdentifiers.Pattern')
                    }
                }
            }
        }
        if ($found.Count -gt 0) { return $found }
        Start-Sleep -Milliseconds 250
    }
    # Not found: show what the menu has (the item may be named differently or the menu did not open).
    $anyItem = New-Object System.Windows.Automation.PropertyCondition($e::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $names = foreach ($id in (Get-ExplorerIds)) {
        foreach ($w in $e::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition($e::ProcessIdProperty, $id)))) {
            foreach ($item in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $anyItem)) { "$($w.Current.ClassName): $($item.Current.Name)" }
        }
    }
    Write-Host "Menu items of explorer.exe: $(@($names) -join '; ')"
    @()
}
