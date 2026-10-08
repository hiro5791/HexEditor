# Helpers to start and drive a test build of HexEditor from the distribution tests (test strategy 6.2, 7.2).
# A test build (build/publish.ps1 -TestHooks) started with --test-hooks <file> opens the test channel: the named
# pipe HexEditor.Test.<pid>, one JSON command per line (the list is in src/HexEditor.App/MainWindow.TestMenu.cs).
# The window is shown without being activated, and --test-profile is not used, so the app keeps its data in the
# real place of its distribution (PKG-13).
# Dot-source after TestCase.ps1:  . "$PSScriptRoot/AppDriver.ps1"

Add-Type -AssemblyName System.Core

$script:HooksFolder = Join-Path ([System.IO.Path]::GetTempPath()) 'hexeditor-dist-tests'

# Never take the foreground: a test build started with --test-hooks shows its window without activating it, and
# this marker (DevOptions.NoActivate) does the same for a start without --test-hooks.
function Enable-NoActivate {
    $marker = Join-Path ([System.IO.Path]::GetTempPath()) 'HexEditor\dev-no-activate'
    if (-not (Test-Path $marker)) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $marker) | Out-Null
        Set-Content -Path $marker -Value '' -Encoding ascii -NoNewline
    }
}

function ConvertTo-ArgumentString([string[]]$Arguments) {
    ($Arguments | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}

function Connect-TestChannel([int]$ProcessId, [int]$TimeoutMilliseconds = 200) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "HexEditor.Test.$ProcessId", [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try { $pipe.Connect($TimeoutMilliseconds) } catch { $pipe.Dispose(); return $null }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = New-Object System.IO.StreamWriter($pipe, $encoding)
    $writer.AutoFlush = $true
    $writer.NewLine = "`n"
    [pscustomobject]@{ Pipe = $pipe; Reader = (New-Object System.IO.StreamReader($pipe, $encoding)); Writer = $writer }
}

<#
  Starts the app and waits until its test channel answers. Returns an object with Process, Id and the channel.
  -Exe can be HexEditor.exe or the MSIX execution alias (%LocalAppData%\Microsoft\WindowsApps\hexeditor.exe);
  for the alias the packaged process is found among the HexEditor processes started after this call.
#>
function Start-TestApp {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [string[]]$Arguments = @(),
        # Put before the test arguments: -Exe cmd.exe -Prefix '/c', 'hexeditor.exe' starts the app from a command prompt.
        [string[]]$Prefix = @(),
        [hashtable]$Hooks = @{},
        [string]$WorkingDirectory,
        # Only processes whose image is in this folder are taken (other HexEditor processes may be running on the
        # same machine). Default: the folder of -Exe. For the MSIX alias: the InstallLocation of the package.
        [string]$ImageFolder,
        [int]$TimeoutSeconds = 90,
        [switch]$Shell
    )
    if (-not $ImageFolder) { $ImageFolder = Split-Path -Parent $Exe }
    Enable-NoActivate
    New-Item -ItemType Directory -Force $script:HooksFolder | Out-Null
    $hooksPath = Join-Path $script:HooksFolder ("hooks-" + [guid]::NewGuid().ToString('N') + '.json')
    [System.IO.File]::WriteAllText($hooksPath, ($Hooks | ConvertTo-Json -Depth 10 -Compress), (New-Object System.Text.UTF8Encoding($false)))
    if (-not $WorkingDirectory) { $WorkingDirectory = $script:HooksFolder }

    $all = $Prefix + @('--test-hooks', $hooksPath, '--ui-lang', 'en') + $Arguments
    $since = (Get-Date).AddSeconds(-1)
    $started = $null
    if ($Shell) {
        $started = Start-Process -FilePath $Exe -ArgumentList (ConvertTo-ArgumentString $all) -WorkingDirectory $WorkingDirectory -PassThru
    } else {
        $info = New-Object System.Diagnostics.ProcessStartInfo($Exe, (ConvertTo-ArgumentString $all))
        $info.UseShellExecute = $false
        $info.WorkingDirectory = $WorkingDirectory
        $started = [System.Diagnostics.Process]::Start($info)
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $candidates = @(Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object {
                try { $_.StartTime -ge $since -and $_.Path -and $_.Path.StartsWith($ImageFolder, [System.StringComparison]::OrdinalIgnoreCase) } catch { $false } })
        foreach ($p in $candidates) {
            $channel = Connect-TestChannel $p.Id
            if ($channel) {
                $app = [pscustomobject]@{ Process = $p; Id = $p.Id; Pipe = $channel.Pipe; Reader = $channel.Reader; Writer = $channel.Writer; Hooks = $hooksPath }
                [void](Send-TestCommand $app 'ping')
                return $app
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if ($started -and -not $started.HasExited) { Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue }
    throw "HexEditor ($Exe) did not open the test channel within $TimeoutSeconds s. Is it a test build (publish.ps1 -TestHooks)?"
}

# Sends one command and returns the answer. Throws if the answer is not ok.
function Send-TestCommand {
    param(
        [Parameter(Mandatory)]$App,
        [Parameter(Mandatory)][string]$Command,
        [hashtable]$Arguments = @{},
        [int]$TimeoutSeconds = 30
    )
    $request = @{ cmd = $Command }
    foreach ($k in $Arguments.Keys) { $request[$k] = $Arguments[$k] }
    $App.Writer.WriteLine(($request | ConvertTo-Json -Depth 10 -Compress))
    $task = $App.Reader.ReadLineAsync()
    if (-not $task.Wait($TimeoutSeconds * 1000)) { throw "Test command '$Command' timed out." }
    if ($null -eq $task.Result) {
        $detail = ''
        try {
            if ($App.Process -and $App.Process.WaitForExit(5000)) { $detail = ' The process exited with code 0x{0:X8}.' -f $App.Process.ExitCode }
        } catch { }
        throw "The app closed the test channel (command '$Command').$detail"
    }
    $response = $task.Result | ConvertFrom-Json
    if (-not $response.ok) { throw "Test command '$Command' failed: $($response.error)" }
    $response
}

function Get-TestState($App) { Send-TestCommand $App 'state' }

function Get-AppLog($App) { @((Send-TestCommand $App 'log').lines) }

# Waits until the script block returns true. Throws with -What after the timeout.
function Wait-Until([scriptblock]$Condition, [int]$Seconds = 30, [string]$What = 'the condition') {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $What ($Seconds s)."
}

function Close-TestChannel($App) {
    foreach ($d in $App.Writer, $App.Reader, $App.Pipe) { try { $d.Dispose() } catch { } }
}

# Closes the app normally (the test command 'exit' skips the unsaved-changes question). Kills it after the timeout.
function Stop-TestApp($App, [int]$Seconds = 30) {
    if ($App.Process.HasExited) { Close-TestChannel $App; return }
    try { [void](Send-TestCommand $App 'exit') } catch { }
    Close-TestChannel $App
    if (-not $App.Process.WaitForExit($Seconds * 1000)) {
        Stop-Process -Id $App.Id -Force -ErrorAction SilentlyContinue
        throw "HexEditor (pid $($App.Id)) did not exit."
    }
}

function Stop-TestAppForcibly($App) {
    Close-TestChannel $App
    Stop-Process -Id $App.Id -Force -ErrorAction SilentlyContinue
    [void]$App.Process.WaitForExit(15000)
}

# Opens a file in a new tab, puts the cursor on the offset in the Hex column and types the hex digits (overwrite mode).
function Edit-Bytes($App, [long]$Offset, [string]$Hex) {
    [void](Send-TestCommand $App 'click' @{ offset = $Offset; column = 'Hex' })
    [void](Send-TestCommand $App 'text' @{ text = $Hex })
    [void](Send-TestCommand $App 'idle')
}

# TC-UI-43-05: the language to start the app in before switching to -Language. The restart notice (UI-43 spec 5)
# appears only when the display language changes, so the app must not already run in the target language.
function Get-OtherUiLanguage([string]$Language) {
    # "system" follows the Windows language list (not always the UI culture of this PowerShell): start in the pseudo
    # language, which "system" never resolves to.
    if ($Language -eq 'system') { return 'qps-ploc' }
    if ($Language -eq 'en') { 'ja' } else { 'en' }
}

# Sets the display language (the test command does what the settings page does) and presses "Restart now" of the
# notice that appears (it is shown asynchronously after the setting changed).
function Invoke-LanguageRestart($App, [string]$Language) {
    [void](Send-TestCommand $App 'setDisplayLanguage' @{ language = $Language })
    $script:RestartLabel = $null
    Wait-Until {
        $notice = @((Get-TestState $App).notifications | Where-Object { @($_.actions).Count -gt 0 }) | Select-Object -First 1
        if ($notice) { $script:RestartLabel = @($notice.actions)[0] }
        $null -ne $script:RestartLabel
    } 15 "the restart notice ($Language)"
    [void](Send-TestCommand $App 'noticeAction' @{ label = $script:RestartLabel })
}

# Shows Help > About and returns the values of the About dialog (UI-40 spec 2) by their AutomationId (About_<label>).
function Get-AboutValues($App) {
    [void](Send-TestCommand $App 'invoke' @{ id = 'Command_About' })
    $values = @{}
    Wait-Until { (Send-TestCommand $App 'element' @{ id = 'About_Distribution' }).found } 15 'the About dialog'
    foreach ($label in 'Version', 'Channel', 'Distribution', 'Architecture', 'NET', 'WindowsAppSDK', 'OS', 'Administrator') {
        $e = Send-TestCommand $App 'element' @{ id = "About_$label" }
        if ($e.found) { $values[$label] = $e.text }
    }
    $values
}

# ---- UI Automation (the parts that the test channel does not reach: system dialogs, dialog buttons) ----

function Initialize-Uia {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
}

function Get-UiaWindows([int]$ProcessId) {
    Initialize-Uia
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    @([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition))
}

function Find-UiaElement {
    param([Parameter(Mandatory)][int]$ProcessId, [string]$AutomationId, [string]$ClassName, [string]$Name, [int]$Seconds = 15)
    Initialize-Uia
    $conditions = @()
    $e = [System.Windows.Automation.AutomationElement]
    if ($AutomationId) { $conditions += New-Object System.Windows.Automation.PropertyCondition($e::AutomationIdProperty, $AutomationId) }
    if ($ClassName) { $conditions += New-Object System.Windows.Automation.PropertyCondition($e::ClassNameProperty, $ClassName) }
    if ($Name) { $conditions += New-Object System.Windows.Automation.PropertyCondition($e::NameProperty, $Name) }
    if ($conditions.Count -eq 0) { throw 'Find-UiaElement needs -AutomationId, -ClassName or -Name.' }
    $condition = if ($conditions.Count -eq 1) { $conditions[0] } else { New-Object System.Windows.Automation.AndCondition(, [System.Windows.Automation.Condition[]]$conditions) }
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in Get-UiaWindows $ProcessId) {
            $found = $w.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $condition)
            if ($found) { return $found }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "UI Automation element not found (process $ProcessId, AutomationId '$AutomationId', class '$ClassName', name '$Name')."
}

function Invoke-UiaElement($Element) {
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

# The names of an element and all its descendants (the text of an InfoBar or a dialog).
function Get-UiaText($Element) {
    $all = $Element.FindAll([System.Windows.Automation.TreeScope]::Subtree, [System.Windows.Automation.Condition]::TrueCondition)
    (@($all | ForEach-Object { $_.Current.Name } | Where-Object { $_ })) -join "`n"
}

# Fills the file name of the system Save dialog (FileSavePicker) and presses Save, without focus or keyboard.
function Complete-SaveDialog([int]$ProcessId, [string]$Path) {
    $dialog = Find-UiaElement -ProcessId $ProcessId -ClassName '#32770' -Seconds 20
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1001')
    $name = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $name) { throw 'The file name box of the Save dialog was not found.' }
    $name.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Path)
    $save = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1')))
    if (-not $save) { throw 'The Save button of the Save dialog was not found.' }
    Invoke-UiaElement $save
}

# ---- Native information about processes, files and shortcuts ----

if (-not ('HexTests.Dist' -as [type])) {
    Add-Type -Namespace HexTests -Name Dist -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

[StructLayout(LayoutKind.Sequential)]
public struct ProcessMachineInformation { public ushort ProcessMachine; public ushort Res0; public uint MachineAttributes; }

[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool GetProcessInformation(IntPtr process, int infoClass, out ProcessMachineInformation info, int size);

// ProcessMachineTypeInfo (Windows 11): the architecture the process runs as. Unlike IsWow64Process2 it also
// tells an x64 process under emulation on ARM64 (IMAGE_FILE_MACHINE_AMD64) from a native ARM64 one.
public static ushort ProcessMachine(IntPtr process)
{
    ProcessMachineInformation info;
    if (!GetProcessInformation(process, 9, out info, Marshal.SizeOf(typeof(ProcessMachineInformation))))
    {
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    return info.ProcessMachine;
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
private struct PropertyKey { public Guid FormatId; public uint PropertyId; }

[StructLayout(LayoutKind.Sequential)]
private struct PropVariant { public ushort Vt; public ushort R1; public ushort R2; public ushort R3; public IntPtr P; public IntPtr P2; }

[DllImport("shell32.dll")]
private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore store);

[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid riid, out IPropertyStore store);

[DllImport("ole32.dll")]
private static extern int PropVariantClear(ref PropVariant value);

private static string AppUserModelId(IPropertyStore store)
{
    // PKEY_AppUserModel_ID
    PropertyKey key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
    PropVariant value;
    store.GetValue(ref key, out value);
    try { return value.Vt == 31 ? Marshal.PtrToStringUni(value.P) : null; }
    finally { PropVariantClear(ref value); }
}

// System.AppUserModel.ID of a window (null if the window has none; the taskbar then uses the process value).
public static string WindowAppUserModelId(IntPtr hwnd)
{
    Guid iid = typeof(IPropertyStore).GUID;
    IPropertyStore store;
    int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out store);
    if (hr < 0) { Marshal.ThrowExceptionForHR(hr); }
    try { return AppUserModelId(store); } finally { Marshal.ReleaseComObject(store); }
}

// System.AppUserModel.ID of a file such as a shortcut (.lnk).
public static string FileAppUserModelId(string path)
{
    Guid iid = typeof(IPropertyStore).GUID;
    IPropertyStore store;
    int hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
    if (hr < 0) { Marshal.ThrowExceptionForHR(hr); }
    try { return AppUserModelId(store); } finally { Marshal.ReleaseComObject(store); }
}
'@
}

# IMAGE_FILE_MACHINE_* values.
$script:MachineX64 = 0x8664
$script:MachineArm64 = 0xAA64

# The Machine field of the PE header of an exe or dll.
function Get-PeMachine([string]$Path) {
    $bytes = New-Object byte[] 4096
    $stream = [System.IO.File]::OpenRead($Path)
    try { [void]$stream.Read($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    $offset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    if ([System.Text.Encoding]::ASCII.GetString($bytes, $offset, 4) -ne "PE`0`0") { throw "$Path is not a PE file." }
    [int][System.BitConverter]::ToUInt16($bytes, $offset + 4)
}

# The data that the distribution tests use (TD-*, test strategy 7.1) from tools/TestDataGen: -TestDataDir is a folder
# where `dotnet run --project tools/TestDataGen -- <ID> <folder>` wrote <ID>.bin. Copies it so that the original stays.
function Copy-TestData([string]$TestDataDir, [string]$Id, [string]$Destination) {
    $source = Join-Path $TestDataDir "$Id.bin"
    if (-not (Test-Path $source)) { throw "Test data $Id is missing in $TestDataDir (run tools/TestDataGen)." }
    New-Item -ItemType Directory -Force (Split-Path -Parent $Destination) | Out-Null
    Copy-Item $source $Destination -Force
    $Destination
}
