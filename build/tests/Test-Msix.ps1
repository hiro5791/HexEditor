<#
.SYNOPSIS
  Distribution tests of the MSIX version (CI runners only: installs packages signed with a throwaway certificate).

.DESCRIPTION
    TC-PKG-03-02  hexeditor.exe (execution alias) in a new command prompt opens the file of the current folder
    TC-PKG-03-04  resources.pri has the 23 languages; with German as the user's language the Start menu
                  description (AppListEntry.DisplayInfo.Description) is the German one of resources.pri
    TC-PKG-28-02  the 1.3.0 package installs as an update of 1.3.0-preview.2 (1.3.999.0) without -ForceUpdateFromAnyVersion
  -TestMsix is a test build (build/publish.ps1 -Distro Msix -TestHooks). -PreviewMsix and -StableMsix are builds
  with -Version 1.3.0-preview.2 and -Version 1.3.0.
  The display language of the runner is not changed (that needs a language pack and a new sign-in): the user's
  language list is set to de-DE, which is what the resource lookup of the package (MRT) follows.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TestMsix,
    [string]$PreviewMsix,
    [string]$StableMsix,
    [Parameter(Mandatory)][string]$TestDataDir,
    [string]$WorkDir
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
. "$PSScriptRoot/AppDriver.ps1"
. "$PSScriptRoot/Msix.ps1"
if (-not $env:GITHUB_ACTIONS) { throw 'This script installs HexEditor; run it only on CI runners.' }
if (-not $WorkDir) { $WorkDir = Join-Path $env:RUNNER_TEMP 'msix-tests' }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

Uninstall-TestMsix
$package = Install-TestMsix $TestMsix

Invoke-TestCase 'TC-PKG-03-02' 'hexeditor.exe execution alias' {
    $work = 'C:\work'
    New-Item -ItemType Directory -Force $work | Out-Null
    [void](Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $work 'file.bin'))
    # 1. A new command prompt in C:\work runs "hexeditor.exe file.bin" (the test arguments come after the alias).
    $app = Start-TestApp -Exe $env:ComSpec -Prefix '/c', 'hexeditor.exe' -Arguments 'file.bin' -WorkingDirectory $work -ImageFolder $package.InstallLocation
    try {
        # 2. The window and its tab.
        Wait-Until { $null -ne (Get-TestState $app).document } 30 'the tab of file.bin'
        $state = Get-TestState $app
        Write-Host "Process: $($app.Process.Path); tab: $($state.document.path)"
        Assert-True ($state.document.path -ieq (Join-Path $work 'file.bin')) "the tab is $($state.document.path)"
        Assert-True ($app.Process.Path -like "$($package.InstallLocation)*") "the alias started $($app.Process.Path)"
    } finally { Stop-TestApp $app }
}

Invoke-TestCase 'TC-PKG-03-04' 'Start menu description in German' {
    # 1. The display name and description of each language from resources.pri (makepri dump).
    $dump = Join-Path $WorkDir 'resources.xml'
    & (Find-SdkTool 'makepri.exe') dump /if (Join-Path $package.InstallLocation 'resources.pri') /of $dump /dt Detailed /o | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'makepri dump failed' }
    [xml]$pri = Get-Content $dump -Raw -Encoding UTF8
    $descriptions = @{}
    foreach ($candidate in $pri.SelectNodes("//NamedResource[@name='AppDescription']/Candidate")) {
        $language = ($candidate.qualifiers -split ',' | Where-Object { $_ -like 'Language-*' } | Select-Object -First 1) -replace '^Language-', ''
        if ($language) { $descriptions[$language.ToLowerInvariant()] = $candidate.Value.'#text' }
    }
    Write-Host "AppDescription languages: $(($descriptions.Keys | Sort-Object) -join ', ')"
    Assert-True ($descriptions.Count -ge 23) "resources.pri has AppDescription in $($descriptions.Count) languages, expected 23"
    $german = $descriptions['de']
    $expected = if ($german) { $german } else { $descriptions['en'] }

    # 2. The description of the Start menu entry with German as the user's language (in a new process, so that the
    #    resource lookup sees the new language list).
    $saved = Get-WinUserLanguageList
    try {
        Set-WinUserLanguageList -LanguageList de-DE, en-US -Force
        $query = @'
$m = [Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType = WindowsRuntime]::new()
$p = $m.FindPackagesForUser('') | Where-Object { $_.Id.Name -eq 'HexEditor' } | Select-Object -First 1
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$p.GetAppListEntries()[0].DisplayInfo.Description
'@
        $actual = (& powershell.exe -NoProfile -Command $query | Out-String).Trim()
    } finally { Set-WinUserLanguageList -LanguageList $saved -Force }
    Write-Host "Start menu description: $actual"
    Assert-True ($actual -eq $expected) "the Start menu description is '$actual', expected '$expected'"
}

Uninstall-TestMsix

if ($PreviewMsix -and $StableMsix) {
    Invoke-TestCase 'TC-PKG-28-02' 'MSIX update from 1.3.0-preview.2 to 1.3.0' {
        $preview = Install-TestMsix $PreviewMsix
        Assert-True ("$($preview.Version)" -eq '1.3.2.0') "the preview package is $($preview.Version), expected 1.3.2.0"
        # 1. Add-AppxPackage without -ForceUpdateFromAnyVersion: accepted only as a newer version.
        $stable = Install-TestMsix $StableMsix
        # 2. The installed version.
        Assert-True ("$($stable.Version)" -eq '1.3.999.0') "the installed version is $($stable.Version), expected 1.3.999.0"
        Assert-True (@(Get-AppxPackage -Name HexEditor).Count -eq 1) 'two HexEditor packages are installed'
    }
    Uninstall-TestMsix
}

Complete-TestRun 'MSIX tests'
