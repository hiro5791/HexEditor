<#
.SYNOPSIS
  Distribution tests that start the three distributions of one architecture (CI runners: installs HexEditor).

.DESCRIPTION
  Uses test builds (build/publish.ps1 -TestHooks) started with --test-hooks and without --test-profile, so each
  distribution keeps its data in its real place. The app is driven through the test channel (AppDriver.ps1).
    TC-PKG-12-01  About shows the distribution of each build (and Development for a dotnet build output, -DevExe)
    TC-UI-40-01   About shows the distribution and the architecture; "Copy info" copies the same values
    TC-PKG-13-01  settings, recovery data, crash info and logs of each distribution are in the place of PKG-13
    TC-PKG-13-02  storage.tempDirectory moves the document folders
    TC-PKG-30-02  crash info and recovery data of each distribution are in the place of PKG-13
    TC-PKG-12-03  installer: the AppUserModelID of the process and window equals the Start menu shortcut's
    TC-PKG-15-01  -Arch arm64 on an ARM64 runner: every distribution runs as native ARM64, PE Machine of the exe
    TC-PKG-31-02  the installer and the MSIX version run at the same time, are not redirected to each other and keep
                  separate settings (dark / light)
    TC-UI-43-05   installer: switching the display language and restarting does not crash (the portable version is
                  checked in Test-Portable.ps1)
  -Setup and -Msix install HexEditor and are allowed only on CI runners (GITHUB_ACTIONS). Without them only the
  portable version (and -DevExe) is tested, nothing is installed and the clipboard is not used, so the script can
  run against a local test build: the windows open behind other windows and never take the focus.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [Parameter(Mandatory)][string]$PortableZip,
    [string]$Setup,
    [string]$Msix,
    [string]$DevExe,
    [Parameter(Mandatory)][string]$TestDataDir,
    [string]$WorkDir
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
. "$PSScriptRoot/AppDriver.ps1"
. "$PSScriptRoot/Msix.ps1"

$ci = [bool]$env:GITHUB_ACTIONS
if (-not $WorkDir) {
    if (-not $env:RUNNER_TEMP) { throw 'Set -WorkDir.' }
    $WorkDir = Join-Path $env:RUNNER_TEMP 'distribution-tests'
}
if (($Setup -or $Msix) -and -not $ci) { throw '-Setup and -Msix install HexEditor; use them only on CI runners.' }
$expectedArch = if ($Arch -eq 'x64') { 'X64' } else { 'Arm64' }
if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

# ---- The distributions under test ----

$targets = New-Object System.Collections.Generic.List[object]

Expand-Archive -Path $PortableZip -DestinationPath (Join-Path $WorkDir 'portable')
$portableExe = Join-Path $WorkDir 'portable\HexEditor\HexEditor.exe'
$targets.Add([pscustomobject]@{
        Name = 'Portable'; Expected = 'Portable'; Exe = $portableExe
        Data = Join-Path (Split-Path -Parent $portableExe) 'Data'
        Temp = Join-Path ([System.IO.Path]::GetTempPath()) ('HexEditor-' + (Get-FolderHash (Split-Path -Parent $portableExe)))
        InstallRoot = $null
    })

if ($Setup) {
    $p = Start-Process $Setup -ArgumentList '--silent' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup.exe exit code $($p.ExitCode)" }
    Start-Sleep -Seconds 3
    # Velopack starts the app after the installation (TC-PKG-07-01); the tests start their own.
    Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
    $installRoot = Join-Path $env:LOCALAPPDATA 'HexEditor'
    $targets.Add([pscustomobject]@{
            Name = 'Installer'; Expected = 'Installer'; Exe = Join-Path $installRoot 'current\HexEditor.exe'
            Data = Join-Path $env:LOCALAPPDATA 'HexEditorData'
            Temp = Join-Path $env:LOCALAPPDATA 'HexEditorData\temp'
            InstallRoot = $installRoot
        })
}

if ($Msix) {
    $package = Install-TestMsix $Msix
    $packageData = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)"
    $targets.Add([pscustomobject]@{
            Name = 'Msix'; Expected = 'Msix'; Exe = Get-MsixAlias
            Data = Join-Path $packageData 'LocalState'
            Temp = Join-Path $packageData 'LocalCache\temp'
            InstallRoot = $package.InstallLocation
        })
}

# Removes the data of a distribution (only while it is not running). Never the Development data folder.
function Reset-Data($Target) {
    $folder = if ($Target.Name -eq 'Msix') { $Target.InstallRoot } else { Split-Path -Parent $Target.Exe }
    Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($folder, [System.StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    if ($Target.Name -eq 'Portable' -or $ci) {
        foreach ($sub in 'settings.json', 'recovery', 'crash', 'logs', 'documents', 'temp') {
            $path = Join-Path $Target.Data $sub
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
    }
}

function Get-CrashReports([string]$Folder) {
    if (-not (Test-Path $Folder)) { return @() }
    @(Get-ChildItem $Folder -Filter '*.txt' | Where-Object { $_.Name -ne 'seen.txt' })
}

# Writes the crash reports of a distribution to the log (when the app ended during a test case).
function Show-CrashReports($Target) {
    if (-not $Target.Data) { return }
    foreach ($report in Get-CrashReports (Join-Path $Target.Data 'crash')) {
        Write-Host "--- $($Target.Name): $($report.FullName)"
        Get-Content $report.FullName -TotalCount 60 | Out-Host
    }
}

function Get-RecoveryStates([string]$Folder) {
    if (-not (Test-Path $Folder)) { return @() }
    @(Get-ChildItem $Folder -Recurse -Filter 'state.json')
}

# Opens a copy of the test data, writes FF at offset 0 (unsaved), and returns the app.
function Start-WithEditedFile($Target, [string]$DataId, [string]$Case) {
    $file = Copy-TestData $TestDataDir $DataId (Join-Path $WorkDir "$Case\$($Target.Name)\$DataId.bin")
    $app = Start-App $Target -Arguments @($file)
    try {
        Wait-Until { $s = Get-TestState $app; $null -ne $s.document -and $s.hexViews -gt 0 } 30 "the tab of $DataId"
        Edit-Bytes $app 0 'FF'
        $doc = (Get-TestState $app).document
        Assert-True ($doc.modified) "$($Target.Name): the document is not modified after typing FF"
    } catch {
        Stop-TestAppForcibly $app
        throw
    }
    $app
}

function Start-App($Target, [string[]]$Arguments = @()) {
    $folder = if ($Target.Name -eq 'Msix') { $Target.InstallRoot } else { Split-Path -Parent $Target.Exe }
    Start-TestApp -Exe $Target.Exe -Arguments $Arguments -ImageFolder $folder
}

# Stops the processes of this run that are still there (so that no window is left behind after a failure).
function Stop-LeftOvers {
    foreach ($t in $targets) {
        $folder = if ($t.Name -eq 'Msix') { $t.InstallRoot } else { Split-Path -Parent $t.Exe }
        Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($folder, [System.StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

# Raises an unhandled exception on the UI thread (the development command of PKG-30) and waits for the exit.
function Invoke-Crash($App) {
    [void](Send-TestCommand $App 'throw' @{ place = 'UiThread' })
    Close-TestChannel $App
    if (-not $App.Process.WaitForExit(90000)) {
        Stop-Process -Id $App.Id -Force -ErrorAction SilentlyContinue
        throw "HexEditor (pid $($App.Id)) did not exit after the unhandled exception."
    }
}

foreach ($t in $targets) { Reset-Data $t }

# ---- Test cases ----

Invoke-TestCase 'TC-PKG-12-01' 'About shows the distribution' {
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($t in $targets) { $list.Add($t) }
    if ($DevExe) {
        $list.Add([pscustomobject]@{
                Name = 'Development'; Expected = 'Development'; Exe = (Resolve-Path $DevExe).Path
                Data = Join-Path $env:LOCALAPPDATA 'HexEditorData-dev'
            })
    } else {
        Add-TestNote 'TC-PKG-12-01: no -DevExe, so the development build (dotnet build output) was not checked.'
    }
    foreach ($t in $list) {
        $app = Start-App $t
        try { $about = Get-AboutValues $app }
        catch { Show-CrashReports $t; throw }
        finally { Stop-TestApp $app }
        Write-Host "$($t.Name): Distribution = $($about['Distribution'])"
        Assert-True ($about['Distribution'] -eq $t.Expected) "$($t.Name): About shows the distribution '$($about['Distribution'])'"
    }
    if (-not $Setup -or -not $Msix) { Add-TestNote 'TC-PKG-12-01: the installer and MSIX versions are checked only with -Setup and -Msix (CI).' }
}

Invoke-TestCase 'TC-UI-40-01' 'About shows the distribution and the architecture' {
    foreach ($t in $targets) {
        $app = Start-App $t
        try {
            try { $about = Get-AboutValues $app }
            catch { Show-CrashReports $t; throw }
            Write-Host "$($t.Name): Distribution = $($about['Distribution']), Architecture = $($about['Architecture'])"
            Assert-True ($about['Distribution'] -eq $t.Expected) "$($t.Name): About shows the distribution '$($about['Distribution'])'"
            Assert-True ($about['Architecture'] -eq $expectedArch) "$($t.Name): About shows the architecture '$($about['Architecture'])', expected $expectedArch"
            # "Copy info" is the primary button of the dialog (ContentDialog: PrimaryButton). A test build started with
            # --test-hooks copies into its in-app stand-in of the clipboard (test strategy 7.2), read with the test command.
            [void](Send-TestCommand $app 'dialogButton' @{ name = 'PrimaryButton' })
            $script:CopiedText = $null
            Wait-Until { $script:CopiedText = (Send-TestCommand $app 'clipboard').text; "$script:CopiedText" -match 'Distribution: ' } 10 'the copied information'
            $text = $script:CopiedText
            Assert-True ($text -match "(?m)^Distribution: $($t.Expected)\s*$") "$($t.Name): the copied text has no 'Distribution: $($t.Expected)'"
            Assert-True ($text -match "(?m)^Architecture: $expectedArch\s*$") "$($t.Name): the copied text has no 'Architecture: $expectedArch'"
        } finally { Stop-TestApp $app }
    }
}

Invoke-TestCase 'TC-PKG-13-01' 'the data of each distribution is in the place of PKG-13' {
    foreach ($t in $targets) {
        Reset-Data $t
        $app = Start-WithEditedFile $t 'TD-RANDOM-16M' '13-01'
        # Settings: change the theme from the View menu (settings.json is written 500 ms later. UI-23).
        [void](Send-TestCommand $app 'invoke' @{ id = 'Command_ThemeDark' })
        Wait-Until { Test-Path (Join-Path $t.Data 'settings.json') } 10 "$($t.Name): settings.json"
        # Recovery data: the same writer as the 1-minute timer (ENG-27), without waiting a minute.
        [void](Send-TestCommand $app 'writeRecovery')
        Invoke-Crash $app

        Assert-True (Test-Path (Join-Path $t.Data 'settings.json')) "$($t.Name): no settings.json in $($t.Data)"
        $states = Get-RecoveryStates (Join-Path $t.Data 'recovery')
        Assert-True ($states.Count -ge 1) "$($t.Name): no recovery data in $($t.Data)\recovery"
        # The add buffer file is kept with the recovery data of the document (ENG-27 spec 2), not in the temp folder.
        $addBuffer = @(Get-ChildItem (Join-Path $t.Data 'recovery') -Recurse -Filter 'add.bin')
        Assert-True ($addBuffer.Count -ge 1) "$($t.Name): no add buffer file in $($t.Data)\recovery"
        Assert-True ((Get-CrashReports (Join-Path $t.Data 'crash')).Count -ge 1) "$($t.Name): no crash info in $($t.Data)\crash"
        Assert-True (Test-Path (Join-Path $t.Data 'logs\hexeditor.log')) "$($t.Name): no logs\hexeditor.log in $($t.Data)"
        if ($t.InstallRoot -and $t.Name -eq 'Installer') {
            $inInstall = @(Get-ChildItem $t.InstallRoot -Recurse -Force -ErrorAction SilentlyContinue | Where-Object {
                    $_.Name -in 'settings.json', 'state.json', 'hexeditor.log', 'add.bin' -or ($_.PSIsContainer -and $_.Name -in 'recovery', 'crash', 'logs', 'documents') })
            Assert-True ($inInstall.Count -eq 0) "Installer: data files under $($t.InstallRoot): $($inInstall.FullName -join ', ')"
        }
        Reset-Data $t
    }
    Add-TestNote 'TC-PKG-13-01: documents\ is not checked (bookmarks, ENG-16, do not exist yet). The add buffer file is checked in recovery\<id>\ (ENG-27 spec 2) instead of the temp folder of the PKG-13 table, and the memory limit is not lowered (no setting yet).'
}

Invoke-TestCase 'TC-PKG-13-02' 'storage.tempDirectory on drive D' {
    $t = $targets | Where-Object Name -eq 'Installer'
    if (-not $t) { $t = $targets | Select-Object -First 1 }
    $drive = if (Test-Path 'D:\') { 'D:\' } else { $env:TEMP }
    $custom = Join-Path $drive ('HexTemp-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        Reset-Data $t
        New-Item -ItemType Directory -Force $t.Data | Out-Null
        $json = '{ "$schemaVersion": 1, "storage.tempDirectory": "' + ($custom -replace '\\', '\\\\') + '" }'
        [System.IO.File]::WriteAllText((Join-Path $t.Data 'settings.json'), $json, (New-Object System.Text.UTF8Encoding($false)))
        $app = Start-WithEditedFile $t 'TD-SEQ-1M' '13-02'
        Invoke-Crash $app
        $states = Get-RecoveryStates (Join-Path $custom 'HexEditor\recovery')
        Assert-True ($states.Count -ge 1) "$($t.Name): no recovery data (add buffer folder) under $custom"
        $default = Get-RecoveryStates (Join-Path $t.Data 'recovery')
        Assert-True ($default.Count -eq 0) "$($t.Name): recovery data was written to the default folder"
    }
    finally {
        Reset-Data $t
        Remove-Item $custom -Recurse -Force -ErrorAction SilentlyContinue
    }
    Add-TestNote 'TC-PKG-13-02: uses D:\ when it exists, otherwise %TEMP%. The add buffer lives in <dir>\HexEditor\recovery\<id>\ (PKG-13 spec 3, ENG-27 spec 2).'
}

Invoke-TestCase 'TC-PKG-30-02' 'crash info and recovery data of each distribution' {
    foreach ($t in $targets) {
        Reset-Data $t
        $app = Start-WithEditedFile $t 'TD-SEQ-1M' '30-02'
        Invoke-Crash $app
        $crash = Get-CrashReports (Join-Path $t.Data 'crash')
        Assert-True ($crash.Count -ge 1) "$($t.Name): no crash info in $($t.Data)\crash"
        Assert-True ((Get-Content $crash[0].FullName -Raw) -match "Distribution: $($t.Expected)") "$($t.Name): the crash info does not name the distribution"
        $states = Get-RecoveryStates (Join-Path $t.Data 'recovery')
        Assert-True ($states.Count -ge 1) "$($t.Name): no recovery data in $($t.Data)\recovery"
        Assert-True ((Get-Content $states[0].FullName -Raw) -match 'TD-SEQ-1M\.bin') "$($t.Name): the recovery data is not for TD-SEQ-1M.bin"
        Reset-Data $t
    }
}

if ($Setup) {
    Invoke-TestCase 'TC-PKG-12-03' 'installer: taskbar group of the Start menu shortcut' {
        $t = $targets | Where-Object Name -eq 'Installer'
        $lnk = Get-ChildItem (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') -Recurse -Filter 'HexEditor*.lnk' | Select-Object -First 1
        Assert-True ($null -ne $lnk) 'no Start menu shortcut'
        $shortcutId = [HexTests.Dist]::FileAppUserModelId($lnk.FullName)
        Write-Host "Shortcut: $shortcutId"
        Assert-True ([bool]$shortcutId) 'the shortcut has no System.AppUserModel.ID'

        Enable-NoActivate
        $since = Get-Date
        Start-Process $lnk.FullName
        $p = $null
        Wait-Until {
            $script:p = Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $t.Exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
            $null -ne $script:p
        } 60 'the window started from the shortcut'
        $p = $script:p
        try {
            $windowId = [HexTests.Dist]::WindowAppUserModelId($p.MainWindowHandle)
            $log = Join-Path $t.Data 'logs\hexeditor.log'
            $processId = $null
            Wait-Until {
                if (-not (Test-Path $log)) { return $false }
                $line = Get-Content $log | Where-Object { $_ -match '^(\S+ \S+) INF AppUserModelID: (.+)$' -and [datetime]$Matches[1] -ge $since.AddSeconds(-2) } | Select-Object -Last 1
                if ($line -match 'AppUserModelID: (.+)$') { $script:processId = $Matches[1].Trim(); return $true }
                $false
            } 30 'the AppUserModelID in the log'
            $processId = $script:processId
            Write-Host "Process: $processId, window: $windowId"
            Assert-True ($processId -eq $shortcutId) "the process AppUserModelID '$processId' differs from the shortcut's '$shortcutId'"
            Assert-True (-not $windowId -or $windowId -eq $shortcutId) "the window AppUserModelID '$windowId' differs from the shortcut's '$shortcutId'"
        } finally {
            [void]$p.CloseMainWindow()
            if (-not $p.WaitForExit(15000)) { Stop-Process -Id $p.Id -Force }
        }
    }
}

if ($Arch -eq 'arm64') {
    Invoke-TestCase 'TC-PKG-15-01' 'ARM64 builds run without emulation' {
        Assert-True ("$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" -eq 'Arm64') 'this runner is not ARM64'
        foreach ($t in $targets) {
            $app = Start-App $t
            try {
                $handle = $app.Process.Handle
                $processMachine = 0; $nativeMachine = 0
                Assert-True ([HexTests.Dist]::IsWow64Process2($handle, [ref]$processMachine, [ref]$nativeMachine)) 'IsWow64Process2 failed'
                $runsAs = [HexTests.Dist]::ProcessMachine($handle)
                Write-Host ('{0}: IsWow64Process2 process=0x{1:X4} native=0x{2:X4}, ProcessMachine=0x{3:X4}' -f $t.Name, $processMachine, $nativeMachine, $runsAs)
                Assert-True ($processMachine -eq 0) "$($t.Name): the process runs under WOW64 (0x$('{0:X4}' -f $processMachine))"
                Assert-True ($nativeMachine -eq $script:MachineArm64) "$($t.Name): the native machine is not ARM64"
                Assert-True ($runsAs -eq $script:MachineArm64) "$($t.Name): the process runs as 0x$('{0:X4}' -f $runsAs), not ARM64"
                $exe = $app.Process.Path
                Assert-True ((Get-PeMachine $exe) -eq $script:MachineArm64) "$($t.Name): $exe is not an ARM64 image"
                $elevated = Join-Path (Split-Path -Parent $exe) 'HexEditor.Elevated.exe'
                if (Test-Path $elevated) {
                    Assert-True ((Get-PeMachine $elevated) -eq $script:MachineArm64) "$($t.Name): $elevated is not an ARM64 image"
                } elseif ($t.Name -ne 'Msix') {
                    Add-TestNote "TC-PKG-15-01: $($t.Name) has no HexEditor.Elevated.exe yet (PKG-14, phase 2)."
                }
            } finally { Stop-TestApp $app }
        }
    }
}

Invoke-TestCase 'TC-PKG-31-02' 'installer and MSIX side by side' {
    $installer = $targets | Where-Object Name -eq 'Installer'
    $store = $targets | Where-Object Name -eq 'Msix'
    if (-not $installer -or -not $store) { Skip-TestCase 'needs both -Setup and -Msix.' }
    Reset-Data $installer
    Reset-Data $store
    $a = Start-App $installer
    try {
        [void](Send-TestCommand $a 'invoke' @{ id = 'Command_ThemeDark' })
        $b = Start-App $store
        try {
            Assert-True ($a.Id -ne $b.Id -and -not $a.Process.HasExited -and -not $b.Process.HasExited) 'the two versions were not separate processes'
            Assert-True (-not (Test-Path (Join-Path $store.Data 'settings.json')) -or -not ((Get-Content (Join-Path $store.Data 'settings.json') -Raw) -match '"ui.theme"')) 'the MSIX version has a theme before it was changed'
            [void](Send-TestCommand $b 'invoke' @{ id = 'Command_ThemeLight' })
        } finally { Stop-TestApp $b }
    } finally { Stop-TestApp $a }
    Assert-True ((Get-Content (Join-Path $installer.Data 'settings.json') -Raw) -match '"ui.theme":\s*"dark"') 'the installer setting is not dark'
    Assert-True ((Get-Content (Join-Path $store.Data 'settings.json') -Raw) -match '"ui.theme":\s*"light"') 'the MSIX setting is not light'
}

Invoke-TestCase 'TC-UI-43-05' 'installer: display language switch and restart' {
    $installer = $targets | Where-Object Name -eq 'Installer'
    if (-not $installer) { Skip-TestCase 'needs -Setup.' }
    Reset-Data $installer
    $crash = Join-Path $installer.Data 'crash'
    foreach ($language in @('en', 'zh-Hans', 'zh-Hant', 'ja', 'ko', 'id', 'vi', 'th', 'de', 'fr', 'es', 'pt', 'it', 'ru', 'uk', 'pl', 'cs', 'hu', 'ro', 'el', 'ar', 'tr', 'fa', 'system')) {
        $a = Start-App $installer -Arguments @('--ui-lang', (Get-OtherUiLanguage $language))
        $oldPid = $a.Id
        Invoke-LanguageRestart $a $language
        Close-TestChannel $a
        Assert-True ($a.Process.WaitForExit(30000)) "the app did not restart ($language)"
        # Wait-Until runs the block in a child scope, so the process is looked up again afterwards.
        $find = { Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $oldPid -and $_.Path -ieq $installer.Exe } | Select-Object -First 1 }
        Wait-Until { $null -ne (& $find) } 30 "the restarted app ($language)"
        $new = & $find
        Assert-True (Wait-MainWindow $new 30) "no window after restarting in $language"
        Start-Sleep -Seconds 2
        Assert-True (-not $new.HasExited) "the app exited after restarting in $language"
        Stop-Process -Id $new.Id -Force
        Assert-True (@(Get-CrashReports $crash).Count -eq 0) "crash info after $language"
    }
}

Stop-LeftOvers
if ($Msix) { Uninstall-TestMsix }
Complete-TestRun "Distribution tests ($Arch)"
