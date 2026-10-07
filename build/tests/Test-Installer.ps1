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
  The UAC part of TC-PKG-07-01 (consent.exe) and TC-PKG-08-04 (Process Monitor) need a dedicated
  runner account and are not checked here.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
if (-not $env:GITHUB_ACTIONS) { throw 'This script installs HexEditor; run it only on CI runners.' }

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

Complete-TestRun 'Installer tests'
