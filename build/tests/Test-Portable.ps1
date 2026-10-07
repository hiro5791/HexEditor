<#
.SYNOPSIS
  Distribution tests of the portable version that start the app (CI runners only).

.DESCRIPTION
  Extracts the portable zip into fresh folders and starts HexEditor.exe (without --test-profile).
    TC-PKG-05-02  runs self-contained: the runtime and Windows App SDK load from the extracted folder
    TC-PKG-05-03  the data folder is <exe>\Data and nothing is created in %LocalAppData% / %AppData%
    TC-PKG-05-04  DataDirectory=..\HexData in portable.marker moves the data folder
    TC-PKG-05-05  two copies in two folders run as two independent processes
    TC-PKG-06-04  %TEMP%\HexEditor-<hash>\ is gone after a normal exit
  The parts that need the settings screen (changing the theme and reading settings.json) are checked
  when the settings UI exists (UI-23); until then the data folder and the absence of other folders are checked.

  This starts windows, so do not run it on a desktop that someone is using.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Zip,
    [string]$WorkDir = (Join-Path $env:RUNNER_TEMP 'portable-tests')
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
if (-not $env:RUNNER_TEMP -and -not $PSBoundParameters.ContainsKey('WorkDir')) { throw 'Set -WorkDir (this script is meant for CI runners).' }

function Expand-Portable([string]$Target) {
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
    New-Item -ItemType Directory -Force $Target | Out-Null
    Expand-Archive -Path $Zip -DestinationPath $Target
    Join-Path $Target 'HexEditor'
}

function Get-HexEditorFolders {
    @((Get-ChildItem $env:LOCALAPPDATA -Directory -Filter '*HexEditor*' -ErrorAction SilentlyContinue).FullName) +
    @((Get-ChildItem $env:APPDATA -Directory -Filter '*HexEditor*' -ErrorAction SilentlyContinue).FullName) | Where-Object { $_ }
}

function Stop-Gracefully([System.Diagnostics.Process]$Process) {
    if ($Process.HasExited) { return }
    [void]$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(15000)) {
        Stop-Process -Id $Process.Id -Force
        throw "HexEditor did not exit after closing its window (pid $($Process.Id))."
    }
}

Invoke-TestCase 'TC-PKG-05-02' 'self-contained start' {
    $app = Expand-Portable (Join-Path $WorkDir 'a')
    $env:DOTNET_ROOT = $null
    $p = Start-Process (Join-Path $app 'HexEditor.exe') -PassThru
    try {
        Assert-True (Wait-MainWindow $p) 'no main window'
        $modules = $p.Modules | ForEach-Object { $_.FileName }
        foreach ($name in 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll') {
            $m = $modules | Where-Object { (Split-Path $_ -Leaf) -ieq $name } | Select-Object -First 1
            Assert-True ($null -ne $m) "$name is not loaded"
            Assert-True ($m.StartsWith($app, [System.StringComparison]::OrdinalIgnoreCase)) "$name was loaded from $m"
        }
    } finally { Stop-Gracefully $p }
}

Invoke-TestCase 'TC-PKG-05-03' 'data folder next to the exe' {
    $before = Get-HexEditorFolders
    $app = Expand-Portable (Join-Path $WorkDir 'b')
    $p = Start-Process (Join-Path $app 'HexEditor.exe') -PassThru
    try { Assert-True (Wait-MainWindow $p) 'no main window' } finally { Stop-Gracefully $p }
    Assert-True (Test-Path (Join-Path $app 'Data')) 'Data folder was not created'
    $new = Get-HexEditorFolders | Where-Object { $before -notcontains $_ }
    Assert-True (-not $new) "new folders: $($new -join ', ')"
}

Invoke-TestCase 'TC-PKG-05-04' 'DataDirectory in portable.marker' {
    $app = Expand-Portable (Join-Path $WorkDir 'c')
    Set-Content -Path (Join-Path $app 'portable.marker') -Value 'DataDirectory=..\HexData' -Encoding ascii
    $p = Start-Process (Join-Path $app 'HexEditor.exe') -PassThru
    try { Assert-True (Wait-MainWindow $p) 'no main window' } finally { Stop-Gracefully $p }
    Assert-True (Test-Path (Join-Path $WorkDir 'c/HexData')) 'HexData was not created'
    Assert-True (-not (Test-Path (Join-Path $app 'Data'))) 'Data was created next to the exe'
}

Invoke-TestCase 'TC-PKG-05-05' 'two portable copies run independently' {
    $a = Expand-Portable (Join-Path $WorkDir 'pa')
    $b = Expand-Portable (Join-Path $WorkDir 'pb')
    $pa = Start-Process (Join-Path $a 'HexEditor.exe') -PassThru
    try {
        Assert-True (Wait-MainWindow $pa) 'first copy: no main window'
        $pb = Start-Process (Join-Path $b 'HexEditor.exe') -PassThru
        try {
            Assert-True (Wait-MainWindow $pb) 'second copy did not start its own window (redirected?)'
            Assert-True (-not $pa.HasExited -and -not $pb.HasExited) 'one of the processes exited'
        } finally { Stop-Gracefully $pb }
    } finally { Stop-Gracefully $pa }
}

Invoke-TestCase 'TC-PKG-06-04' 'temp folder is removed after a normal exit' {
    $app = Expand-Portable (Join-Path $WorkDir 'd')
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("HexEditor-" + (Get-FolderHash $app))
    $p = Start-Process (Join-Path $app 'HexEditor.exe') -PassThru
    try {
        Assert-True (Wait-MainWindow $p) 'no main window'
        # Simulate a spilled add buffer in the temp folder that the app must clean up.
        New-Item -ItemType Directory -Force $temp | Out-Null
        Set-Content -Path (Join-Path $temp 'spill.tmp') -Value 'x'
    } finally { Stop-Gracefully $p }
    Assert-True (-not (Test-Path $temp)) "$temp remains"
}

Invoke-TestCase 'TC-UI-15-01' 'a second start is redirected to the existing instance' {
    $app = Expand-Portable (Join-Path $WorkDir 'e')
    $file = Join-Path $WorkDir 'e/bytes.bin'
    [System.IO.File]::WriteAllBytes($file, [byte[]](0..255))
    $first = Start-Process (Join-Path $app 'HexEditor.exe') -PassThru
    try {
        Assert-True (Wait-MainWindow $first) 'no main window'
        $second = Start-Process (Join-Path $app 'HexEditor.exe') -ArgumentList "`"$file`"" -PassThru
        Assert-True ($second.WaitForExit(10000)) 'the second process did not exit'
        Assert-True ($second.ExitCode -eq 0) "the second process exited with $($second.ExitCode)"
        $count = @(Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$app*" }).Count
        Assert-True ($count -eq 1) "$count HexEditor processes"
        # The tab check (TD-BYTES-256 is active) needs UI automation (UI tests).
    } finally { Stop-Gracefully $first }
}

Invoke-TestCase 'TC-UI-15-05' 'an unresponsive instance: the second start runs on its own after 5 s' {
    Add-Type -Namespace HexTests -Name Native -MemberDefinition @'
[DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
[DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
'@
    $app = Expand-Portable (Join-Path $WorkDir 'f')
    $first = Start-Process (Join-Path $app 'HexEditor.exe') -PassThru
    try {
        Assert-True (Wait-MainWindow $first) 'no main window'
        [void][HexTests.Native]::NtSuspendProcess($first.Handle)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $second = Start-Process (Join-Path $app 'HexEditor.exe') -PassThru
        try {
            Assert-True (Wait-MainWindow $second 15) 'the second process did not show a window'
            $seconds = $sw.Elapsed.TotalSeconds
            Assert-True ($seconds -ge 4.5 -and $seconds -le 8) "the window appeared after $seconds s (expected 5-7 s)"
            # The InfoBar text (Startup_RedirectTimedOut) needs UI automation (UI tests).
        } finally {
            [void][HexTests.Native]::NtResumeProcess($first.Handle)
            Stop-Gracefully $second
        }
    } finally {
        [void][HexTests.Native]::NtResumeProcess($first.Handle)
        Stop-Gracefully $first
    }
}

Complete-TestRun 'Portable tests'
