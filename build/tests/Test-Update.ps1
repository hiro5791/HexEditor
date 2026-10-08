<#
.SYNOPSIS
  Distribution tests of the updates (PKG-17 to PKG-22) against the test feed TD-PKG-UPDATE-FEED (CI runners only).

.DESCRIPTION
  -FeedRepo is the URL of the test repository with the releases of TD-PKG-UPDATE-FEED, made by the release workflow
  from one commit as test builds (build/publish.ps1 -TestHooks): v0.9.0 and v0.9.1 (stable, published; 0.9.1 has a
  delta package), v0.9.2-preview.1 (pre-release) and v0.9.2 (stable, draft; published and drafted again by the test).
  The app reads the feed from the setting test.update.source (PKG-17 spec 8). Without -FeedRepo every case is Skipped.
    TC-PKG-17-01  update.checkAutomatically = false: no update requests at start and after 24 h
    TC-PKG-17-02  manual check on the newest stable: "up to date (0.9.1)", no buttons, closes after 8 s
    TC-PKG-17-03  nothing in the first 30 s, one check between 30 and 60 s
    TC-PKG-17-04  no connection (firewall rule / 192.0.2.1): "could not check" within 15 s
    TC-PKG-18-01  installer 0.9.0 -> 0.9.1 downloads only the delta package
    TC-PKG-18-02  "Restart to update" starts 0.9.1 with the tabs and shows "Updated to version 0.9.1"
    TC-PKG-18-03  "Later", exit, start: 0.9.1
    TC-PKG-18-04  "Restart to update" is disabled while saving
    TC-PKG-20-01  portable: "Version 0.9.1 is available" with its buttons; the download page of 0.9.1
    TC-PKG-20-02  "Skip this version" until 0.9.2 is published
    TC-PKG-21-01  preview channel finds 0.9.2-preview.1
    TC-PKG-21-02  preview channel receives 0.9.2 (stable)
    TC-PKG-21-03  back to stable: no downgrade to 0.9.1
    TC-PKG-22-01  the same message for the installer and the portable version; different first buttons
    TC-PKG-22-03  the app never restarts by itself
  Requests are read from the app's own network log (every request of the app goes through it, UI-58). The DNS and
  TCP records of ETW are not collected by this script.
  This installs HexEditor and starts windows; run it only on CI runners.
#>
[CmdletBinding()]
param(
    [string]$FeedRepo,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$TestDataDir,
    [string]$WorkDir = (Join-Path $env:RUNNER_TEMP 'update-tests')
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
. "$PSScriptRoot/AppDriver.ps1"
if ($FeedRepo -and -not $env:GITHUB_ACTIONS) { throw 'This script installs HexEditor; run it only on CI runners.' }
$feedName = if ($FeedRepo) { ($FeedRepo.TrimEnd('/') -split '/')[-2..-1] -join '/' } else { '' }
$installRoot = Join-Path $env:LOCALAPPDATA 'HexEditor'
$installedExe = Join-Path $installRoot 'current\HexEditor.exe'
$installerData = Join-Path $env:LOCALAPPDATA 'HexEditorData'

function Assert-Feed { if (-not $FeedRepo) { Skip-TestCase 'TD-PKG-UPDATE-FEED is not set up (pass -FeedRepo with the test repository).' } }

function Get-FeedAsset([string]$Version, [string]$Name) {
    $dir = Join-Path $WorkDir "feed\$Version"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir $Name
    if (-not (Test-Path $path)) {
        gh release download "v$Version" --repo $feedName --pattern $Name --dir $dir --clobber
        if ($LASTEXITCODE -ne 0) { throw "gh release download v$Version $Name failed" }
    }
    $path
}

function Write-Settings([string]$Folder, [hashtable]$Values) {
    New-Item -ItemType Directory -Force $Folder | Out-Null
    $all = @{ '$schemaVersion' = 1; 'test.update.source' = $FeedRepo }
    foreach ($k in $Values.Keys) { $all[$k] = $Values[$k] }
    [System.IO.File]::WriteAllText((Join-Path $Folder 'settings.json'), ($all | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
}

function Install-Version([string]$Version, [hashtable]$Settings = @{}) {
    Uninstall-Installed
    $setup = Get-FeedAsset $Version "HexEditor-$Version-$Arch-Setup.exe"
    $p = Start-Process $setup -ArgumentList '--silent' -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup.exe $Version exit code $($p.ExitCode)" }
    Start-Sleep -Seconds 3
    Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $installerData) { Remove-Item $installerData -Recurse -Force }
    Write-Settings $installerData $Settings
}

function Uninstall-Installed {
    Get-Process HexEditor, Update -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HexEditor'
    if (Test-Path $key) {
        $command = (Get-ItemProperty $key).UninstallString
        if ($command -match '^"([^"]+)"\s*(.*)$') { Start-Process $Matches[1] -ArgumentList "$($Matches[2]) --silent" -Wait | Out-Null }
        Start-Sleep -Seconds 3
    }
}

function Expand-PortableVersion([string]$Version, [hashtable]$Settings = @{}) {
    $zip = Get-FeedAsset $Version "HexEditor-$Version-$Arch-portable.zip"
    $target = Join-Path $WorkDir "portable-$Version-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
    Expand-Archive -Path $zip -DestinationPath $target
    $exe = Join-Path $target 'HexEditor\HexEditor.exe'
    Write-Settings (Join-Path $target 'HexEditor\Data') $Settings
    $exe
}

function Get-UpdateRequests($App) { @((Send-TestCommand $App 'networkLog').requests | Where-Object { $_.feature -eq 'Updates' }) }

function Get-Update($App) { Send-TestCommand $App 'updateState' }

function Set-Draft([string]$Version, [bool]$Draft) {
    gh release edit "v$Version" --repo $feedName "--draft=$($Draft.ToString().ToLowerInvariant())"
    if ($LASTEXITCODE -ne 0) { throw "gh release edit v$Version failed" }
}

New-Item -ItemType Directory -Force $WorkDir | Out-Null

Invoke-TestCase 'TC-PKG-17-01' 'automatic checks off: no requests' {
    Assert-Feed
    foreach ($exe in @((Expand-PortableVersion '0.9.0' @{ 'update.checkAutomatically' = $false }))) {
        $a = Start-TestApp -Exe $exe
        try {
            Start-Sleep -Seconds 60
            [void](Send-TestCommand $a 'advanceUpdateClock' @{ hours = 24.02 })
            Start-Sleep -Seconds 60
            $requests = Get-UpdateRequests $a
            Assert-True ($requests.Count -eq 0) "update requests: $(($requests | ForEach-Object { $_.host + $_.path }) -join ', ')"
        } finally { Stop-TestApp $a }
    }
    Add-TestNote 'TC-PKG-17-01: the installer version follows the same code path; only the portable version is started here.'
}

Invoke-TestCase 'TC-PKG-17-02' 'manual check on the newest version' {
    Assert-Feed
    $a = Start-TestApp -Exe (Expand-PortableVersion '0.9.1')
    try {
        $s = Send-TestCommand $a 'updateCheck' @{ manual = $true }
        Assert-True ($s.messageKey -eq 'Update_UpToDate' -and $s.message -match '0\.9\.1') "message: $($s.message)"
        Assert-True (@($s.buttons).Count -eq 0) 'the up-to-date notice has buttons'
        Start-Sleep -Seconds 9
        Assert-True (-not (Get-Update $a).barVisible) 'the notice did not close after 8 s'
    } finally { Stop-TestApp $a }
}

Invoke-TestCase 'TC-PKG-17-03' 'no check in the first 30 s' {
    Assert-Feed
    $a = Start-TestApp -Exe (Expand-PortableVersion '0.9.0')
    try {
        $started = $a.Process.StartTime
        Wait-Until { ((Get-Date) - $started).TotalSeconds -ge 28 } 40 '28 s after the start'
        Assert-True ((Get-UpdateRequests $a).Count -eq 0) 'an update request in the first 30 s'
        Wait-Until { ((Get-Date) - $started).TotalSeconds -ge 60 } 40 '60 s after the start'
        Assert-True ((Get-UpdateRequests $a).Count -eq 1) "$((Get-UpdateRequests $a).Count) update requests between 30 and 60 s"
    } finally { Stop-TestApp $a }
}

Invoke-TestCase 'TC-PKG-17-04' 'no connection: could not check within 15 s' {
    Assert-Feed
    # (b) a source that does not answer.
    $exe = Expand-PortableVersion '0.9.0'
    Write-Settings (Join-Path (Split-Path -Parent $exe) 'Data') @{ 'test.update.source' = 'https://192.0.2.1/owner/repo' }
    $a = Start-TestApp -Exe $exe
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $s = Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 30
        Assert-True ($sw.Elapsed.TotalSeconds -le 16) "took $($sw.Elapsed.TotalSeconds) s"
        Assert-True ($s.messageKey -eq 'Update_Failed' -and $s.message -match 'connect') "message: $($s.message)"
    } finally { Stop-TestApp $a }
    # (a) the firewall blocks the app.
    $exe2 = Expand-PortableVersion '0.9.0'
    $rule = 'HexEditor update test'
    New-NetFirewallRule -DisplayName $rule -Direction Outbound -Program $exe2 -Action Block | Out-Null
    try {
        $b = Start-TestApp -Exe $exe2
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $s = Send-TestCommand $b 'updateCheck' @{ manual = $true } -TimeoutSeconds 30
            Assert-True ($sw.Elapsed.TotalSeconds -le 16 -and $s.messageKey -eq 'Update_Failed') "firewall: $($s.message) after $($sw.Elapsed.TotalSeconds) s"
        } finally { Stop-TestApp $b }
    } finally { Remove-NetFirewallRule -DisplayName $rule }
}

Invoke-TestCase 'TC-PKG-18-01' 'only the delta package is downloaded' {
    Assert-Feed
    Install-Version '0.9.0'
    $a = Start-TestApp -Exe $installedExe
    try {
        [void](Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120)
        Wait-Until { (Get-Update $a).messageKey -eq 'Update_Ready' } 300 'the download'
        $paths = @(Get-UpdateRequests $a | ForEach-Object { $_.path })
        Assert-True (@($paths | Where-Object { $_ -like '*-delta.nupkg' }).Count -ge 1) "no delta package: $($paths -join ', ')"
        Assert-True (@($paths | Where-Object { $_ -like '*-full.nupkg' }).Count -eq 0) "the full package was downloaded: $($paths -join ', ')"
    } finally { Stop-TestApp $a }
}

Invoke-TestCase 'TC-PKG-18-02' 'Restart to update' {
    Assert-Feed
    if (-not $TestDataDir) { Skip-TestCase 'needs -TestDataDir (TD-SEQ-1M, TD-BYTES-256).' }
    Install-Version '0.9.0'
    $seq = Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $WorkDir 'restart\seq.bin')
    $bytes = Copy-TestData $TestDataDir 'TD-BYTES-256' (Join-Path $WorkDir 'restart\bytes.bin')
    $a = Start-TestApp -Exe $installedExe -Arguments @($seq, $bytes)
    $oldPid = $a.Id
    [void](Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120)
    Wait-Until { (Get-Update $a).messageKey -eq 'Update_Ready' } 300 'the download'
    [void](Send-TestCommand $a 'updateButton' @{ button = 'RestartToUpdate' })
    Close-TestChannel $a
    $find = { Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $oldPid -and $_.Path -ieq $installedExe } | Select-Object -First 1 }
    Wait-Until { $null -ne (& $find) } 120 'the updated app'
    $new = & $find
    Wait-Until { ($null -ne ($script:ch = Connect-TestChannel $new.Id 1000)) } 60 'the test channel of the updated app'
    $b = [pscustomobject]@{ Process = $new; Id = $new.Id; Pipe = $script:ch.Pipe; Reader = $script:ch.Reader; Writer = $script:ch.Writer }
    try {
        $s = Get-Update $b
        Assert-True ($s.current -eq '0.9.1') "version after the update: $($s.current)"
        Assert-True ($s.messageKey -eq 'Update_Updated') "notice: $($s.messageKey)"
        $names = @((Get-TestState $b).documents | ForEach-Object { $_.name })
        Assert-True ($names -contains 'seq.bin' -and $names -contains 'bytes.bin') "tabs: $($names -join ', ')"
    } finally { Stop-TestApp $b }
}

Invoke-TestCase 'TC-PKG-18-03' 'Later: the next start is the new version' {
    Assert-Feed
    Install-Version '0.9.0'
    $a = Start-TestApp -Exe $installedExe
    try {
        [void](Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120)
        Wait-Until { (Get-Update $a).messageKey -eq 'Update_Ready' } 300 'the download'
        [void](Send-TestCommand $a 'updateButton' @{ button = 'Later' })
        Assert-True ((Get-Update $a).current -eq '0.9.0') 'the app restarted after "Later"'
    } finally { Stop-TestApp $a }
    Wait-Until { -not (Get-Process Update -ErrorAction SilentlyContinue) } 120 'Update.exe to finish'
    $b = Start-TestApp -Exe $installedExe
    try { Assert-True ((Get-Update $b).current -eq '0.9.1') "version: $((Get-Update $b).current)" } finally { Stop-TestApp $b }
}

Invoke-TestCase 'TC-PKG-18-04' 'Restart to update is disabled while saving' {
    Assert-Feed
    if (-not $TestDataDir) { Skip-TestCase 'needs -TestDataDir (TD-RANDOM-16M).' }
    Install-Version '0.9.0'
    $file = Copy-TestData $TestDataDir 'TD-RANDOM-16M' (Join-Path $WorkDir 'busy\random.bin')
    $a = Start-TestApp -Exe $installedExe -Arguments @($file) -Hooks @{ fileSources = @(@{ match = 'random.bin'; delayMs = 40 }) }
    try {
        [void](Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120)
        Wait-Until { (Get-Update $a).messageKey -eq 'Update_Ready' } 300 'the download'
        [void](Send-TestCommand $a 'insertBytes' @{ offset = 0; length = 1 })
        [void](Send-TestCommand $a 'key' @{ key = 'S'; ctrl = $true })
        Wait-Until { (Get-TestState $a).activeOperations -gt 0 } 10 'the save to start'
        $restart = @((Get-Update $a).buttons | Where-Object { $_.kind -eq 'RestartToUpdate' })[0]
        Assert-True (-not $restart.enabled) 'Restart to update is enabled while saving'
        Assert-True ([bool](Get-Update $a).reason) 'no reason is shown'
        Wait-Until { (Get-TestState $a).activeOperations -eq 0 } 120 'the save'
        $restart = @((Get-Update $a).buttons | Where-Object { $_.kind -eq 'RestartToUpdate' })[0]
        Assert-True ($restart.enabled) 'Restart to update is still disabled after the save'
    } finally { Stop-TestApp $a }
}

Invoke-TestCase 'TC-PKG-20-01' 'portable: the new version and its download page' {
    Assert-Feed
    $a = Start-TestApp -Exe (Expand-PortableVersion '0.9.0')
    try {
        $s = Send-TestCommand $a 'updateCheck' @{ manual = $true }
        Assert-True ($s.message -eq 'Version 0.9.1 is available.') "message: $($s.message)"
        Assert-True ((@($s.buttons | ForEach-Object { $_.kind }) -join ',') -eq 'OpenDownloadPage,ReleaseNotes,Skip') "buttons: $(@($s.buttons | ForEach-Object { $_.kind }) -join ',')"
        [void](Send-TestCommand $a 'updateButton' @{ button = 'OpenDownloadPage' })
        Wait-Until { @(Get-AppLog $a | Where-Object { $_ -like "*Test hooks: launch $($FeedRepo.TrimEnd('/'))/releases/tag/v0.9.1*" }).Count -gt 0 } 10 'the download page'
        Add-TestNote 'TC-PKG-20-01: the URL is taken from the app log (the test build does not start the browser) instead of Edge DevTools.'
    } finally { Stop-TestApp $a }
}

Invoke-TestCase 'TC-PKG-20-02' 'Skip this version' {
    Assert-Feed
    $exe = Expand-PortableVersion '0.9.0'
    $a = Start-TestApp -Exe $exe
    try {
        Wait-Until { (Get-Update $a).messageKey -eq 'Update_Available' } 90 'the automatic notice'
        [void](Send-TestCommand $a 'updateButton' @{ button = 'Skip' })
        [void](Send-TestCommand $a 'advanceUpdateClock' @{ hours = 24.02 })
        Start-Sleep -Seconds 60
        Assert-True (-not (Get-Update $a).barVisible) 'the skipped version was shown again'
        $settings = Get-Content (Join-Path (Split-Path -Parent $exe) 'Data\settings.json') -Raw | ConvertFrom-Json
        Assert-True ($settings.'update.skippedVersion' -eq '0.9.1') "update.skippedVersion = $($settings.'update.skippedVersion')"
        Set-Draft '0.9.2' $false
        try {
            [void](Send-TestCommand $a 'advanceUpdateClock' @{ hours = 24.02 })
            Wait-Until { (Get-Update $a).message -eq 'Version 0.9.2 is available.' } 90 'the notice of 0.9.2'
        } finally { Set-Draft '0.9.2' $true }
    } finally { Stop-TestApp $a }
}

Invoke-TestCase 'TC-PKG-21-01' 'preview channel finds the preview' {
    Assert-Feed
    Install-Version '0.9.1' @{ 'update.channel' = 'preview'; 'update.downloadAutomatically' = $false }
    $a = Start-TestApp -Exe $installedExe
    try {
        $s = Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120
        Assert-True ($s.message -eq 'Version 0.9.2-preview.1 is available.') "message: $($s.message)"
    } finally { Stop-TestApp $a }
}

Invoke-TestCase 'TC-PKG-21-02' 'preview channel receives the new stable version' {
    Assert-Feed
    Install-Version '0.9.2-preview.1' @{ 'update.channel' = 'preview' }
    Set-Draft '0.9.2' $false
    try {
        $a = Start-TestApp -Exe $installedExe
        try {
            [void](Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120)
            Wait-Until { (Get-Update $a).messageKey -eq 'Update_Ready' } 300 'the download'
            Assert-True ((Get-Update $a).offer -eq '0.9.2') "offer: $((Get-Update $a).offer)"
        } finally { Stop-TestApp $a }
    } finally { Set-Draft '0.9.2' $true }
}

Invoke-TestCase 'TC-PKG-21-03' 'back to stable does not downgrade' {
    Assert-Feed
    Install-Version '0.9.2-preview.1' @{ 'update.channel' = 'stable' }
    $a = Start-TestApp -Exe $installedExe
    try {
        $s = Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120
        Assert-True ($s.messageKey -eq 'Update_UpToDate') "message: $($s.message)"
    } finally { Stop-TestApp $a }
    $b = Start-TestApp -Exe $installedExe
    try { Assert-True ((Get-Update $b).current -eq '0.9.2-preview.1') "version: $((Get-Update $b).current)" } finally { Stop-TestApp $b }
}

Invoke-TestCase 'TC-PKG-22-01' 'the same message for the installer and the portable version' {
    Assert-Feed
    foreach ($language in 'en', 'ja') {
        Install-Version '0.9.0' @{ 'update.downloadAutomatically' = $false }
        $a = Start-TestApp -Exe $installedExe -Arguments @('--ui-lang', $language)
        try { $installer = Send-TestCommand $a 'updateCheck' @{ manual = $true } -TimeoutSeconds 120 } finally { Stop-TestApp $a }
        $b = Start-TestApp -Exe (Expand-PortableVersion '0.9.0') -Arguments @('--ui-lang', $language)
        try { $portable = Send-TestCommand $b 'updateCheck' @{ manual = $true } } finally { Stop-TestApp $b }
        Assert-True ($installer.message -eq $portable.message) "$language`: '$($installer.message)' / '$($portable.message)'"
        Assert-True ((@($installer.buttons | ForEach-Object { $_.kind }) -join ',') -eq 'Download,ReleaseNotes,Skip') 'installer buttons'
        Assert-True ((@($portable.buttons | ForEach-Object { $_.kind }) -join ',') -eq 'OpenDownloadPage,ReleaseNotes,Skip') 'portable buttons'
    }
}

Invoke-TestCase 'TC-PKG-22-03' 'the app never restarts by itself' {
    Assert-Feed
    Install-Version '0.9.0'
    $a = Start-TestApp -Exe $installedExe
    try {
        Wait-Until { (Get-Update $a).messageKey -eq 'Update_Ready' } 360 'the automatic download'
        Start-Sleep -Seconds 600
        [void](Send-TestCommand $a 'advanceUpdateClock' @{ hours = 24.02 })
        Start-Sleep -Seconds 120
        Assert-True (-not $a.Process.HasExited) 'the app restarted'
        Assert-True ((Get-Update $a).current -eq '0.9.0') 'the version changed'
    } finally { Stop-TestApp $a }
}

Uninstall-Installed
Complete-TestRun 'Update tests'
