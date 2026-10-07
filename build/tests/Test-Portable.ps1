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

  With -TestZip (a test build of the portable zip: build/publish.ps1 -TestHooks) and -TestDataDir, the cases that
  edit and save are driven through the test channel (AppDriver.ps1):
    TC-PKG-06-01  start, edit, save and exit add no HexEditor values under HKCU\Software (Process Monitor on CI;
                  the before/after export of HKCU\Software everywhere)
    TC-PKG-06-02  start from a read-only volume (a VHDX attached read-only), InfoBar, edit, save elsewhere (CI only:
                  attaching a VHDX needs administrator rights)
    TC-PKG-13-03  after a forced termination, "Discard" removes the recovery data in Data\recovery

  This starts windows, so do not run it on a desktop that someone is using.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Zip,
    [string]$WorkDir = (Join-Path $env:RUNNER_TEMP 'portable-tests'),
    [string]$TestZip,
    [string]$TestDataDir
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
. "$PSScriptRoot/AppDriver.ps1"
. "$PSScriptRoot/ProcessMonitor.ps1"
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

# ---- Cases that need a test build (-TestZip) ----

function Expand-TestBuild([string]$Target) {
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
    New-Item -ItemType Directory -Force $Target | Out-Null
    Expand-Archive -Path $TestZip -DestinationPath $Target
    Join-Path $Target 'HexEditor'
}

# Opens a file in a new instance of the test build and waits for its hex view.
function Start-WithFile([string]$Exe, [string]$File, [hashtable]$Hooks = @{}) {
    $app = Start-TestApp -Exe $Exe -Arguments @($File) -Hooks $Hooks
    try {
        Wait-Until { $s = Get-TestState $app; $null -ne $s.document -and $s.hexViews -gt 0 } 30 "the tab of $File"
    } catch { Stop-TestAppForcibly $app; throw }
    $app
}

# Registry keys that Windows itself writes for any app (PKG-06 spec 3): Explorer's history of dialogs, recent
# documents and app usage, the shell's caches, compatibility data, input and graphics settings.
$script:WindowsRecordedKeys = @(
    '\Software\Microsoft\Windows\CurrentVersion\Explorer\',
    '\Software\Microsoft\Windows\CurrentVersion\Search\',
    '\Software\Microsoft\Windows\CurrentVersion\UFH\',
    '\Software\Microsoft\Windows\Shell\',
    '\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\',
    '\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\',
    '\Software\Microsoft\Direct3D\',
    '\Software\Microsoft\DirectX\',
    '\Software\Microsoft\CTF\',
    '\Software\Microsoft\Input\',
    '\Software\Microsoft\InputPersonalization\'
)

function Test-WindowsRecorded([string]$Key) {
    foreach ($k in $script:WindowsRecordedKeys) { if ($Key.IndexOf($k, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } }
    $false
}

# HKCU\Software exported with reg.exe (read only) as a set of "<key>|<line>".
function Export-UserSoftware([string]$File) {
    & reg.exe export 'HKCU\Software' $File /y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'reg export failed' }
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    $key = ''
    foreach ($line in [System.IO.File]::ReadLines($File)) {
        if ($line.StartsWith('[')) { $key = $line.Trim('[', ']') } elseif ($line) { [void]$set.Add("$key|$line") }
    }
    , $set
}

if ($TestZip) {
    if (-not $TestDataDir) { throw '-TestZip needs -TestDataDir (tools/TestDataGen output with TD-SEQ-1M).' }

    Invoke-TestCase 'TC-PKG-06-01' 'no HexEditor values in HKCU\Software' {
        $app = Expand-TestBuild (Join-Path $WorkDir 'reg')
        $file = Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $WorkDir 'reg\seq.bin')
        $before = Export-UserSoftware (Join-Path $WorkDir 'reg\before.reg')
        $procmon = $null
        $backing = Join-Path $WorkDir 'reg\events.pml'
        if ($env:GITHUB_ACTIONS) {
            $procmon = Get-ProcessMonitor (Join-Path $WorkDir 'procmon')
            Start-ProcessMonitor $procmon $backing
        } else {
            Add-TestNote 'TC-PKG-06-01: Process Monitor runs only on CI runners; here only the export of HKCU\Software before and after was compared.'
        }
        $recorded = $null
        try {
            $a = Start-WithFile (Join-Path $app 'HexEditor.exe') $file
            try {
                Edit-Bytes $a 0 'FF'
                $r = Send-TestCommand $a 'key' @{ key = 'S'; ctrl = $true }
                Assert-True ($r.handledBy -eq 'menu:Command_Save') "Ctrl+S was handled by $($r.handledBy)"
                [void](Send-TestCommand $a 'idle')
                Wait-Until { -not (Get-TestState $a).document.modified } 30 'the save'
            } finally { Stop-TestApp $a }
        } finally {
            if ($procmon) { $recorded = Stop-ProcessMonitor $procmon $backing '(?i)^HexEditor\.exe$' }
        }
        Assert-True ([System.IO.File]::ReadAllBytes($file)[0] -eq 0xFF) 'the file was not saved'

        if ($procmon) {
            Assert-True ($recorded.Processes.Count -gt 0) 'Process Monitor recorded no registry events of HexEditor.exe'
            $writes = @($recorded.Writes |
                    Where-Object { $_.Path -like 'HKCU\Software\*' -and -not (Test-WindowsRecorded ('\' + $_.Path.Substring(5) + '\')) })
            Write-Host "Registry writes of HexEditor.exe under HKCU\Software (not by Windows): $($writes.Count)"
            Assert-True ($writes.Count -eq 0) ("HexEditor wrote to HKCU\Software: " + (($writes | Select-Object -First 20 | ForEach-Object { "$($_.Operation) $($_.Path)" }) -join '; '))
        }
        $after = Export-UserSoftware (Join-Path $WorkDir 'reg\after.reg')
        $added = @($after | Where-Object { -not $before.Contains($_) -and $_ -match 'hexeditor' } |
                Where-Object { -not (Test-WindowsRecorded ('\' + ($_ -split '\|', 2)[0].Substring('HKEY_CURRENT_USER\'.Length) + '\')) })
        Assert-True ($added.Count -eq 0) ("new values with HexEditor in HKCU\Software: " + (($added | Select-Object -First 20) -join '; '))
    }

    Invoke-TestCase 'TC-PKG-06-02' 'start from read-only media' {
        if (-not $env:GITHUB_ACTIONS) { Skip-TestCase 'attaching a VHDX needs administrator rights; CI runners only.' }
        $vhd = Join-Path $WorkDir 'readonly.vhdx'
        if (Test-Path $vhd) { Remove-Item $vhd -Force }
        $diskpartScript = Join-Path $WorkDir 'diskpart.txt'
        Set-Content -Path $diskpartScript -Value "create vdisk file=`"$vhd`" maximum=64 type=expandable" -Encoding ascii
        & diskpart.exe /s $diskpartScript | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'diskpart could not create the VHDX' }
        # Write the portable version and TD-SEQ-1M (E:\HexEditor, E:\data\seq.bin of the test case) on it.
        $disk = Mount-DiskImage -ImagePath $vhd -PassThru | Get-Disk
        $volume = $disk | Initialize-Disk -PartitionStyle MBR -PassThru | New-Partition -AssignDriveLetter -UseMaximumSize |
            Format-Volume -FileSystem NTFS -NewFileSystemLabel 'HEXRO' -Confirm:$false
        $root = "$($volume.DriveLetter):\"
        $app = Expand-TestBuild (Join-Path $WorkDir 'ro-source')
        Copy-Item $app (Join-Path $root 'HexEditor') -Recurse
        [void](Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $root 'data\seq.bin'))
        Dismount-DiskImage -ImagePath $vhd | Out-Null
        $letter = (Mount-DiskImage -ImagePath $vhd -Access ReadOnly -PassThru | Get-Disk | Get-Partition | Where-Object DriveLetter | Select-Object -First 1).DriveLetter
        try {
            $root = "${letter}:\"
            $out = Join-Path $WorkDir 'out\seq-edited.bin'
            New-Item -ItemType Directory -Force (Split-Path -Parent $out) | Out-Null
            if (Test-Path $out) { Remove-Item $out -Force }
            # 1. Start from the read-only volume.
            $a = Start-WithFile (Join-Path $root 'HexEditor\HexEditor.exe') (Join-Path $root 'data\seq.bin')
            try {
                # 2. The app-wide InfoBar: settings are not saved.
                Wait-Until { @((Get-TestState $a).notifications | Where-Object { $_.message -match "Settings won't be saved" }).Count -gt 0 } 15 'the InfoBar about the data folder'
                Add-TestNote 'TC-PKG-06-02: the "Export settings" button is not checked (settings export, UI-25, is phase 1).'
                # 3. Overwrite offset 0 with FF. 4. Save As C:\out\seq-edited.bin (the system Save dialog).
                Edit-Bytes $a 0 'FF'
                [void](Send-TestCommand $a 'invoke' @{ id = 'Command_SaveAs' })
                Complete-SaveDialog $a.Id $out
                Wait-Until { (Test-Path $out) -and -not (Get-TestState $a).document.modified } 30 'the save as'
            } finally { Stop-TestApp $a }
            # 5. The saved file has FF at offset 0.
            Assert-True ([System.IO.File]::ReadAllBytes($out)[0] -eq 0xFF) 'offset 0 of the saved file is not FF'
        } finally {
            Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "${letter}:\*" } | Stop-Process -Force
            Dismount-DiskImage -ImagePath $vhd | Out-Null
            Remove-Item $vhd -Force -ErrorAction SilentlyContinue
        }
    }

    Invoke-TestCase 'TC-PKG-13-03' '"Discard" after a forced termination removes the recovery data' {
        $app = Expand-TestBuild (Join-Path $WorkDir 'recovery')
        $exe = Join-Path $app 'HexEditor.exe'
        $recovery = Join-Path $app 'Data\recovery'
        $file = Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $WorkDir 'recovery\seq.bin')
        # The recovery interval is 2 s instead of 1 minute (the same timer; only the wait is shorter).
        $hooks = @{ recoveryIntervalSeconds = 2 }
        $a = Start-WithFile $exe $file $hooks
        try {
            Edit-Bytes $a 0 'FF'
            Wait-Until { @(Get-ChildItem $recovery -Recurse -Filter 'state.json' -ErrorAction SilentlyContinue).Count -gt 0 } 30 'the recovery data'
        } finally {
            # 1. Forced termination.
            Stop-TestAppForcibly $a
        }
        # 2. The recovery data is there.
        $states = @(Get-ChildItem $recovery -Recurse -Filter 'state.json')
        Assert-True ($states.Count -eq 1) "$($states.Count) recovery entries after the forced termination"
        $entry = $states[0].Directory.FullName
        # 3. Start again and choose "Discard".
        $b = Start-TestApp -Exe $exe -Hooks $hooks
        try {
            Wait-Until { (Send-TestCommand $b 'element' @{ id = 'Recovery_Discard' }).found } 20 'the recovery dialog'
            [void](Send-TestCommand $b 'invoke' @{ id = 'Recovery_Discard' })
            # 4. The recovery data is gone.
            Wait-Until { -not (Test-Path (Join-Path $entry 'state.json')) } 15 'the recovery data to be removed'
        } finally { Stop-TestApp $b }
        Assert-True (@(Get-ChildItem $recovery -Recurse -Filter 'state.json' -ErrorAction SilentlyContinue).Count -eq 0) 'recovery data remains'
    }
}

Complete-TestRun 'Portable tests'
