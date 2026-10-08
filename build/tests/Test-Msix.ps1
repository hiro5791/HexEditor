<#
.SYNOPSIS
  Distribution tests of the MSIX version (CI runners only: installs packages signed with a throwaway certificate).

.DESCRIPTION
    TC-PKG-03-01  file type associations: proj.hexproj opens HexEditor of the package; the default app of .iso is
                  unchanged and HexEditor is among its "Open with" handlers (SHAssocEnumHandlers)
    TC-PKG-03-02  hexeditor.exe (execution alias) in a new command prompt opens the file of the current folder
    TC-PKG-03-04  resources.pri has the 23 languages; with German as the user's language the Start menu
                  description (AppListEntry.DisplayInfo.Description) is the German one of resources.pri
    TC-UI-55-01   Windows 11: the new context menu has one top-level "Open with HexEditor" without a submenu, and
                  it starts HexEditor with the selected file (the packaged COM class is also called directly)
    TC-UI-55-04   the COM class answers a null item array with HRESULTs; deleting the file while the menu is open
                  and choosing "Open with HexEditor" leaves explorer.exe running with the same process IDs
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
. "$PSScriptRoot/Explorer.ps1"
if (-not $env:GITHUB_ACTIONS) { throw 'This script installs HexEditor; run it only on CI runners.' }
if (-not $WorkDir) { $WorkDir = Join-Path $env:RUNNER_TEMP 'msix-tests' }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

Uninstall-TestMsix
# TC-PKG-03-01: the default app of .iso before the installation.
$isoDefaultBefore = [HexTest.ExplorerNative]::DefaultApp('.iso')
$package = Install-TestMsix $TestMsix

Invoke-TestCase 'TC-PKG-03-01' 'file type associations of the MSIX version' {
    # TD-UI-HEXPROJ: proj.hexproj next to seq.bin (TD-SEQ-1M).
    $folder = Join-Path $WorkDir 'hexproj'
    [void](Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $folder 'seq.bin'))
    $project = Join-Path $folder 'proj.hexproj'
    Set-Content -Path $project -Value '{ "file": "seq.bin", "bookmarks": [ { "name": "proj-mark", "offset": 64, "length": 1 } ] }' -Encoding ascii
    Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
    # 1. Open it with the default verb (ShellExecute "open", like a double click).
    Enable-NoActivate
    Start-Process $project
    try {
        Wait-Until { @(Get-HexEditorFor 'proj.hexproj').Count -gt 0 } 30 'HexEditor started for proj.hexproj'
        $started = @(Get-HexEditorFor 'proj.hexproj')[0]
        Assert-True ($started.ExecutablePath -like "$($package.InstallLocation)*") "proj.hexproj started $($started.ExecutablePath)"
    } finally { Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force }
    Add-TestNote 'TC-PKG-03-01: opening the project (the seq.bin tab) is the project file feature (UI-33); only the association is checked.'
    # 2. The default app of .iso is unchanged and HexEditor is an "Open with" handler.
    # Without a default app, AssocQueryString answers '' while the type has no handler and OpenWith.exe (the "How do
    # you want to open" dialog) once a handler such as HexEditor exists: both mean "no default app" (Windows Server runners).
    $isoDefault = [HexTest.ExplorerNative]::DefaultApp('.iso')
    if ($isoDefault -like '*\OpenWith.exe' -and -not ($isoDefaultBefore -like '*\OpenWith.exe')) {
        Assert-True ($isoDefaultBefore -eq '') "the default app of .iso changed: '$isoDefaultBefore' -> '$isoDefault'"
        $isoDefault = ''
    }
    Assert-True ($isoDefault -eq $isoDefaultBefore) "the default app of .iso changed: '$isoDefaultBefore' -> '$isoDefault'"
    $handlers = @([HexTest.ExplorerNative]::OpenWithHandlers('.iso'))
    Write-Host "Open with .iso: $($handlers -join '; ')"
    Assert-True (@($handlers | Where-Object { $_ -match 'HexEditor' }).Count -ge 1) 'HexEditor is not an "Open with" handler of .iso'
}

Invoke-TestCase 'TC-UI-55-01' 'Windows 11 context menu: one top-level "Open with HexEditor"' {
    if ([Environment]::OSVersion.Version.Build -lt 22000) { Skip-TestCase 'the new context menu exists only on Windows 11 (UI-55 spec 4).' }
    # The packaged COM class (dllhost.exe loads HexEditor.ShellExtension.dll) answers like Explorer asks it.
    $probe = [HexTest.ExplorerNative]::ProbeExplorerCommand($script:ExplorerCommandClsid)
    Assert-True ($probe[0] -eq 'Open with HexEditor') "GetTitle: '$($probe[0])'"
    Assert-True ($probe[4] -eq '0') "GetFlags: $($probe[4]) (ECF_DEFAULT: no submenu)"
    $file = Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $WorkDir 'menu55\seq.bin')
    Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
    Enable-NoActivate
    # 1. Select the file in Explorer and press the Application key (Shift+F10 opens the classic menu).
    $window = Open-ExplorerSelection $file
    try {
        # 2. The items of the new menu (UI Automation).
        $items = @(Get-NewContextMenuItems 'Open with HexEditor')
        Assert-True ($items.Count -eq 1) "$($items.Count) items named 'Open with HexEditor'"
        Assert-True (-not $items[0].Submenu) 'the item opens a submenu'
        Assert-True ($items[0].ParentType -like '*Menu') "the item is under $($items[0].ParentType), not the top level of the menu"
        # 3. Choose it.
        Invoke-UiaElement $items[0].Element
        # 4. HexEditor of the package starts with the file.
        Wait-Until { @(Get-HexEditorFor 'seq.bin').Count -gt 0 } 30 'HexEditor started for seq.bin'
        $started = @(Get-HexEditorFor 'seq.bin')[0]
        Assert-True ($started.ExecutablePath -like "$($package.InstallLocation)*") "the menu started $($started.ExecutablePath)"
        Assert-True ($started.CommandLine -like "*$file*") "command line: $($started.CommandLine)"
    } finally {
        Close-ExplorerWindow $window
        Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    Add-TestNote 'TC-UI-55-01: the tab is checked through the command line of the started process (the menu starts the app without the test channel).'
}

Invoke-TestCase 'TC-UI-55-04' 'exceptions in the COM server do not crash Explorer' {
    if ([Environment]::OSVersion.Version.Build -lt 22000) { Skip-TestCase 'the new context menu exists only on Windows 11 (UI-55 spec 4).' }
    # 1-2. A null item array: HRESULTs only (the deleted file and the virtual folder item are covered by the
    #      HexEditor.Platform.Tests ShellExtensionTests, which pass fake items).
    $probe = [HexTest.ExplorerNative]::ProbeExplorerCommand($script:ExplorerCommandClsid)
    Assert-True ($probe[1] -eq '0' -and $probe[2] -eq '0') "GetState: hr $($probe[1]), state $($probe[2])"
    Assert-True ([int]$probe[3] -lt 0) "Invoke with no items returned $($probe[3])"
    # 3. Open the menu of a file, delete the file, then choose "Open with HexEditor".
    $file = Copy-TestData $TestDataDir 'TD-SEQ-1M' (Join-Path $WorkDir 'menu55-deleted\seq-deleted.bin')
    Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
    Enable-NoActivate
    $window = Open-ExplorerSelection $file
    $explorerBefore = Get-ExplorerIds
    try {
        $items = @(Get-NewContextMenuItems 'Open with HexEditor')
        Assert-True ($items.Count -ge 1) 'the new context menu has no "Open with HexEditor"'
        [System.IO.File]::Delete($file)
        Invoke-UiaElement $items[0].Element
        Start-Sleep -Seconds 5
        # 4. explorer.exe runs with the same process IDs.
        $explorerAfter = Get-ExplorerIds
        Assert-True (($explorerBefore -join ',') -eq ($explorerAfter -join ',')) "explorer.exe processes: $($explorerBefore -join ',') -> $($explorerAfter -join ',')"
    } finally {
        Close-ExplorerWindow $window
        Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

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
    # The detailed dump: <Candidate><QualifierSet><Qualifier name="Language" value="DE" .../></QualifierSet><Value>...</Value></Candidate>
    foreach ($candidate in $pri.SelectNodes("//NamedResource[@name='AppDescription']/Candidate")) {
        $qualifier = $candidate.SelectSingleNode("QualifierSet/Qualifier[@name='Language']")
        $value = $candidate.SelectSingleNode('Value')
        if ($qualifier -and $value) { $descriptions[$qualifier.GetAttribute('value').ToLowerInvariant()] = $value.InnerText }
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
