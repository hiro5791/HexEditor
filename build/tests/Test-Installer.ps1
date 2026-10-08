<#
.SYNOPSIS
  Distribution tests of the installer version (CI runners only: this installs and uninstalls HexEditor).

.DESCRIPTION
    TC-PKG-07-01  installs without UAC: Setup.exe exits 0 and current\HexEditor.exe exists and starts
    TC-PKG-07-02  Start menu shortcut, no desktop shortcut
    TC-PKG-07-03  "Apps" entry (Uninstall key) and uninstall from it
    TC-PKG-07-04  App Paths: ShellExecute "HexEditor" starts current\HexEditor.exe
    TC-PKG-11-02  the Velopack hook arguments exit within 5 s without a window
    TC-PKG-09-01  data is kept by a default uninstall
    TC-PKG-09-02  uninstall.removeUserData = true removes the data folder
    TC-PKG-08-04  no writes to HKLM by Setup.exe, Update.exe and HexEditor.exe during all of the above
                  (Process Monitor records from the first install to the last uninstall)
    TC-UI-54-01   the classic context menu "Open with HexEditor" is registered in HKCU with the app icon
    TC-UI-54-04   three files opened with the menu end up in one process (the tabs are checked with a test build)
    TC-UI-55-03   unsigned: the classic menu "Open with HexEditor" (Shell API) opens the file; no sparse package
    TC-UI-56-01   .hexproj is associated with HexEditor and opens it
    TC-UI-56-02   the default app of .iso does not change; HexEditor is added to "Open with"
  The UAC part of TC-PKG-07-01 (consent.exe) needs a dedicated runner account and is not checked here.
  The update step of TC-PKG-08-04 waits for the updater (PKG-18, phase 1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
. "$PSScriptRoot/ProcessMonitor.ps1"
. "$PSScriptRoot/Explorer.ps1"
if (-not $env:GITHUB_ACTIONS) { throw 'This script installs HexEditor; run it only on CI runners.' }

# TC-PKG-08-04: record every registry access from the first installation to the last uninstallation.
$procmon = $null
$procmonError = $null
$backing = Join-Path $env:RUNNER_TEMP 'installer-events.pml'
try {
    $procmon = Get-ProcessMonitor (Join-Path $env:RUNNER_TEMP 'procmon')
    Start-ProcessMonitor $procmon $backing
} catch {
    $procmon = $null
    $procmonError = "$_"
}

$installRoot = Join-Path $env:LOCALAPPDATA 'HexEditor'
$exe = Join-Path $installRoot 'current/HexEditor.exe'
$dataRoot = Join-Path $env:LOCALAPPDATA 'HexEditorData'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HexEditor'
$appPathsKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\HexEditor.exe'

function Stop-HexEditor { Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500 }

function Install-HexEditor {
    $p = Start-Process $Setup -ArgumentList '--silent' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup.exe exit code $($p.ExitCode)" }
    Start-Sleep -Seconds 3
}

function Uninstall-HexEditor {
    Stop-HexEditor
    $command = (Get-ItemProperty $uninstallKey).UninstallString
    $exePart = if ($command -match '^"([^"]+)"\s*(.*)$') { $Matches[1] } else { ($command -split ' ', 2)[0] }
    $argPart = if ($command -match '^"([^"]+)"\s*(.*)$') { $Matches[2] } else { ($command -split ' ', 2)[1] }
    $p = Start-Process $exePart -ArgumentList "$argPart --silent" -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "uninstall exit code $($p.ExitCode)" }
    Start-Sleep -Seconds 3
}

if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }

# The default app of a file type (AssocQueryString ASSOCSTR_EXECUTABLE) for TC-UI-56-02.
Add-Type -Namespace HexTest -Name Assoc -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int AssocQueryStringW(int flags, int str, string assoc, string extra, System.Text.StringBuilder output, ref int length);
'@
function Get-DefaultApp([string]$Extension) {
    $length = 1024
    $sb = New-Object System.Text.StringBuilder $length
    $hr = [HexTest.Assoc]::AssocQueryStringW(0, 2, $Extension, 'open', $sb, [ref]$length)
    if ($hr -ne 0) { return '' }
    $sb.ToString()
}
$isoDefaultBefore = Get-DefaultApp '.iso'
$contextMenuKey = 'HKCU:\Software\Classes\*\shell\HexEditor'

# Waits until HexEditor runs for the file (its command line has the file name).
function Wait-HexEditorFor([string]$FileName, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $found = @(Get-HexEditorFor $FileName)
        if ($found.Count -gt 0) { return $found[0] }
        Start-Sleep -Milliseconds 250
    }
    throw "HexEditor did not start for $FileName within $Seconds s."
}

Invoke-TestCase 'TC-PKG-07-01' 'install as a normal user' {
    Install-HexEditor
    Assert-True (Test-Path $exe) "$exe missing"
    $running = Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $exe }
    Assert-True ($null -ne $running) 'the app did not start after installation'
    Stop-HexEditor
}

Invoke-TestCase 'TC-PKG-07-02' 'Start menu shortcut only' {
    $start = Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter 'HexEditor*.lnk'
    Assert-True ($start.Count -ge 1) 'no Start menu shortcut'
    $shell = New-Object -ComObject WScript.Shell
    Assert-True ($shell.CreateShortcut($start[0].FullName).TargetPath -ieq $exe) 'the shortcut does not point to current\HexEditor.exe'
    foreach ($desktop in [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('CommonDesktopDirectory')) {
        Assert-True (-not (Get-ChildItem $desktop -Filter 'HexEditor*.lnk' -ErrorAction SilentlyContinue)) "desktop shortcut in $desktop"
    }
}

Invoke-TestCase 'TC-PKG-07-04' 'App Paths' {
    Assert-True ((Get-ItemProperty $appPathsKey).'(default)' -ieq $exe) 'App Paths does not point to current\HexEditor.exe'
    $p = Start-Process 'HexEditor' -PassThru
    try {
        Assert-True (Wait-MainWindow $p) 'no window'
        Assert-True ($p.Path -ieq $exe) "started $($p.Path)"
    } finally { Stop-HexEditor }
}

Invoke-TestCase 'TC-PKG-11-02' 'Velopack hook arguments' {
    foreach ($hook in '--veloapp-install', '--veloapp-updated', '--veloapp-obsolete') {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process $exe -ArgumentList $hook, '0.0.1' -PassThru
        $windowed = $false
        while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt 10) { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $windowed = $true }; Start-Sleep -Milliseconds 100 }
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; throw "$hook did not exit" }
        Assert-True (-not $windowed) "$hook created a window"
        Assert-True ($sw.Elapsed.TotalSeconds -lt 5) "$hook took $($sw.Elapsed.TotalSeconds) s"
    }
    Assert-True (Test-Path (Join-Path $dataRoot 'logs/install.log')) 'install.log was not written'
}

Invoke-TestCase 'TC-UI-54-01' 'context menu "Open with HexEditor" without UAC' {
    Assert-True (Test-Path -LiteralPath $contextMenuKey) "$contextMenuKey does not exist"
    $menu = Get-ItemProperty -LiteralPath $contextMenuKey
    Assert-True ($menu.'(default)' -like '*HexEditor*') "menu name '$($menu.'(default)')'"
    Assert-True ($menu.Icon -like "*$exe*") "icon '$($menu.Icon)'"
    $command = (Get-ItemProperty -LiteralPath "$contextMenuKey\command").'(default)'
    Assert-True ($command -eq ('"' + $exe + '" "%1"')) "command '$command'"
    $file = Join-Path $env:RUNNER_TEMP 'menu-test.bin'
    [System.IO.File]::WriteAllBytes($file, [byte[]](0..255))
    $verbs = Get-ContextMenuVerbs $file
    Assert-True (@($verbs | Where-Object { ($_.Name -replace '&', '') -like '*HexEditor*' }).Count -ge 1) ("verbs: " + (($verbs | ForEach-Object { $_.Name }) -join ', '))
    Add-TestNote 'TC-UI-54-01: the UAC part (no elevated process during the installation) needs a standard user account on the runner.'
}

Invoke-TestCase 'TC-UI-54-04' 'three files from the context menu: one process' {
    Stop-HexEditor
    $folder = Join-Path $env:RUNNER_TEMP 'files50'
    New-Item -ItemType Directory -Force $folder | Out-Null
    $files = foreach ($n in 1..3) { $f = Join-Path $folder ('file{0:D2}.bin' -f $n); [System.IO.File]::WriteAllBytes($f, [byte[]](@($n) * 4096)); $f }
    foreach ($f in $files) {
        $verb = Get-ContextMenuVerbs $f | Where-Object { ($_.Name -replace '&', '') -like '*HexEditor*' } | Select-Object -First 1
        Assert-True ($null -ne $verb) "no HexEditor verb for $f"
        $verb.DoIt()
    }
    Start-Sleep -Seconds 10
    $running = @(Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $exe })
    try {
        Assert-True ($running.Count -eq 1) "$($running.Count) HexEditor processes"
        Assert-True ($running[0].MainWindowHandle -ne 0) 'no window'
    } finally { Stop-HexEditor }
    Add-TestNote 'TC-UI-54-04: the 3 tabs are counted with a test build (test channel); this release build only shows one process and one window.'
}

Invoke-TestCase 'TC-UI-55-03' 'unsigned installer: classic menu, no sparse package' {
    # TD-SEQ-1M (1 MiB of 00 01 .. FF repeated).
    $folder = Join-Path $env:RUNNER_TEMP 'menu55'
    New-Item -ItemType Directory -Force $folder | Out-Null
    $file = Join-Path $folder 'seq.bin'
    [System.IO.File]::WriteAllBytes($file, [byte[]]((0..255) * 4096))
    Stop-HexEditor
    # 1. The classic menu item through the Shell API, and run it.
    $verb = Get-HexEditorVerb $file
    Assert-True ($null -ne $verb) 'no "Open with HexEditor" in the classic context menu'
    $verb.DoIt()
    try {
        # 2. HexEditor starts with the file.
        $started = Wait-HexEditorFor 'seq.bin'
        Assert-True ($started.ExecutablePath -ieq $exe) "the menu started $($started.ExecutablePath)"
    } finally { Stop-HexEditor }
    # 3. No sparse package (UI-55 spec 2 and 3: not provided while code signing is off).
    $packages = Get-HexEditorPackages
    Assert-True ($packages.Count -eq 0) "packages: $(($packages | ForEach-Object { $_.PackageFullName }) -join ', ')"
    Add-TestNote 'TC-UI-55-03: the tab is checked through the command line of the started process (a release build has no test channel).'
}

Invoke-TestCase 'TC-UI-56-01' '.hexproj opens HexEditor' {
    $progId = (Get-ItemProperty 'HKCU:\Software\Classes\.hexproj').'(default)'
    Assert-True ($progId -eq 'HexEditor.Project') ".hexproj is associated with '$progId'"
    Assert-True ((Get-DefaultApp '.hexproj') -ieq $exe) "the default app of .hexproj is '$(Get-DefaultApp '.hexproj')'"
    $folder = Join-Path $env:RUNNER_TEMP 'hexproj'
    New-Item -ItemType Directory -Force $folder | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $folder 'seq.bin'), [byte[]](0..255))
    $project = Join-Path $folder 'proj.hexproj'
    Set-Content -Path $project -Value '{ "file": "seq.bin", "bookmarks": [ { "name": "proj-mark", "offset": 64, "length": 1 } ] }' -Encoding utf8
    Stop-HexEditor
    Start-Process $project
    Start-Sleep -Seconds 10
    try {
        $p = Get-CimInstance Win32_Process -Filter "Name = 'HexEditor.exe'" | Where-Object { $_.CommandLine -like '*proj.hexproj*' }
        Assert-True ($null -ne $p) 'HexEditor was not started with proj.hexproj'
    } finally { Stop-HexEditor }
    Add-TestNote 'TC-UI-56-01: opening the project (the seq.bin tab and the bookmark proj-mark) is the project file feature (UI-33); only the association is checked.'
}

Invoke-TestCase 'TC-UI-56-02' 'the default app of .iso is kept' {
    Assert-True ((Get-DefaultApp '.iso') -eq $isoDefaultBefore) "the default app of .iso changed: '$isoDefaultBefore' -> '$(Get-DefaultApp '.iso')'"
    $openWith = Get-ItemProperty 'HKCU:\Software\Classes\.iso\OpenWithProgids' -ErrorAction SilentlyContinue
    Assert-True ($null -ne $openWith -and $openWith.PSObject.Properties.Name -contains 'HexEditor.Binary') 'HexEditor.Binary is not in .iso\OpenWithProgids'
    Add-TestNote 'TC-UI-56-02: the "Open with" list is checked through OpenWithProgids (SHAssocEnumHandlers is not called from PowerShell).'
}

Invoke-TestCase 'TC-PKG-07-03' 'Apps entry and uninstall' {
    $entry = Get-ItemProperty $uninstallKey
    Assert-True ($entry.DisplayName -like 'HexEditor*') "DisplayName $($entry.DisplayName)"
    Assert-True ($entry.Publisher -eq 'Hiroyura') "Publisher $($entry.Publisher)"
    Assert-True ([bool]$entry.DisplayVersion) 'no DisplayVersion'
    Assert-True ([bool]$entry.DisplayIcon) 'no DisplayIcon'
    # A settings file for TC-PKG-09-01.
    New-Item -ItemType Directory -Force $dataRoot | Out-Null
    Set-Content -Path (Join-Path $dataRoot 'settings.json') -Value '{ "$schemaVersion": 1, "ui.theme": "dark" }' -Encoding utf8
    Uninstall-HexEditor
    Assert-True (-not (Test-Path $installRoot) -or -not (Test-Path $exe)) "$installRoot remains"
    Assert-True (-not (Test-Path $uninstallKey)) 'the Uninstall key remains'
    Assert-True (-not (Test-Path $appPathsKey)) 'App Paths remains (ShellRegistration)'
    Assert-True (-not (Test-Path -LiteralPath $contextMenuKey)) 'the context menu key remains (ShellRegistration)'
    Assert-True (-not (Test-Path 'HKCU:\Software\Classes\HexEditor.Project')) 'the ProgID HexEditor.Project remains'
}

Invoke-TestCase 'TC-PKG-09-01' 'data is kept by a default uninstall' {
    Assert-True (Test-Path (Join-Path $dataRoot 'settings.json')) 'settings.json was removed'
    Install-HexEditor
    Stop-HexEditor
    Assert-True ((Get-Content (Join-Path $dataRoot 'settings.json') -Raw) -match 'dark') 'the previous settings were not kept'
}

Invoke-TestCase 'TC-PKG-09-02' 'uninstall.removeUserData' {
    Set-Content -Path (Join-Path $dataRoot 'settings.json') -Value '{ "$schemaVersion": 1, "uninstall.removeUserData": true }' -Encoding utf8
    Uninstall-HexEditor
    Assert-True (-not (Test-Path $dataRoot)) "$dataRoot remains"
}

Invoke-TestCase 'TC-PKG-08-04' 'no writes to HKLM' {
    Assert-True ($null -ne $procmon) "Process Monitor did not start: $procmonError"
    # Setup.exe (HexEditor-<ver>-<arch>-Setup.exe), Update.exe and HexEditor.exe (child processes have these names too).
    $recorded = Stop-ProcessMonitor $procmon $backing '(?i)^(HexEditor.*Setup\.exe|Setup\.exe|Update\.exe|HexEditor\.exe)$'
    Assert-True ($recorded.Processes.Count -gt 0) 'no registry events of the installer processes were recorded'
    $writes = @($recorded.Writes | Where-Object { $_.Path -like 'HKLM\*' })
    Assert-True ($writes.Count -eq 0) ("writes to HKLM: " + (($writes | Select-Object -First 20 | ForEach-Object { "$($_.Process) $($_.Operation) $($_.Path) $($_.Result)" }) -join '; '))
    Add-TestNote 'TC-PKG-08-04: the update step is not run (the updater, PKG-18, is phase 1).'
}

Complete-TestRun 'Installer tests'
