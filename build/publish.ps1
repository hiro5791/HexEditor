<#
.SYNOPSIS
  Build one HexEditor distribution (PKG-10). CI and local builds use this same script.

.EXAMPLE
  ./build/publish.ps1 -Distro Portable -Arch x64
  ./build/publish.ps1 -Distro Installer -Arch arm64 -Version 1.2.0
  ./build/publish.ps1 -Distro Msix -Arch x64 -Version 1.2.0-preview.1

  Output goes to artifacts/<Distro>/<Arch>/ (file names follow PKG-26):
    Portable  : HexEditor-<ver>-<arch>-portable.zip
                (root folder HexEditor\ with HexEditor.exe, portable.marker, README.txt,
                 THIRD-PARTY-NOTICES.txt, LICENSE. PKG-05)
    Installer : HexEditor-<ver>-<arch>-Setup.exe, full/delta packages and release files (Velopack. PKG-07)
    Msix      : unsigned .msix for Microsoft Store submission (PKG-02, PKG-04).
                build/bundle-msix.ps1 combines the x64 and arm64 packages into one .msixbundle.

  -Version is the SemVer of the release (PKG-28). Without it the build is 0.0.0-local.
  -PreviousReleaseDir is a folder with the previous Velopack release (vpk download github),
  so that vpk pack can make a delta package (PKG-24 step 5).
  -TestHooks makes a test build (-p:HexTestHooks=true: the test channel and the Test menu, test strategy 7.2)
  for the distribution tests in build/tests/. Never release a test build.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Msix', 'Installer', 'Portable')][string]$Distro,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [string]$Version = '0.0.0-local',
    [string]$Commit,
    [string]$Configuration = 'Release',
    [string]$PreviousReleaseDir,
    [switch]$TestHooks
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/HexEditor.App/HexEditor.App.csproj'
$rid = "win-$Arch"
$platform = if ($Arch -eq 'x64') { 'x64' } else { 'ARM64' }
$Version = $Version.TrimStart('v')

$out = Join-Path $root "artifacts/$Distro/$Arch"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
$staging = Join-Path $root "artifacts/staging/$Distro-$Arch"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$publishDir = Join-Path $staging 'HexEditor'

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    Write-Host "> $exe $($arguments -join ' ')"
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

$common = @(
    $project, '-c', $Configuration, '-r', $rid,
    "-p:Platform=$platform", "-p:HexDistro=$Distro", "-p:HexVersion=$Version", '-nologo'
)
if ($Commit) { $common += "-p:HexCommit=$Commit" }
if ($TestHooks) { $common += '-p:HexTestHooks=true' }
# Pseudo-locales (qps-ploc, qps-plocm) ship only in preview and local builds, not in stable releases (UI-46 spec 3).
$isStable = $Version -notmatch '-'
if ($isStable) { $common += '-p:HexPseudoLocales=false' }
# Velopack channel (PKG-21 spec 3). The app checks the same name (VelopackChannels in HexEditor.Platform).
$channel = "win-$Arch-" + $(if ($isStable) { 'stable' } else { 'preview' })

# Code signing (PKG-25): a no-op unless HEX_SIGNING_ENABLED is true.
$sign = Join-Path $PSScriptRoot 'sign.ps1'

switch ($Distro) {
    'Portable' {
        Invoke-Checked 'dotnet' (@('publish') + $common + @('-o', $publishDir))
        if (Get-ChildItem $publishDir -Filter 'Velopack*.dll') { throw 'Portable output must not contain Velopack (PKG-10).' }
        & $sign -Path $publishDir

        # The marker tells the app to keep its data next to the exe (PKG-05, PKG-12, PKG-13).
        Set-Content -Path (Join-Path $publishDir 'portable.marker') -Value '' -Encoding ascii -NoNewline
        Copy-Item (Join-Path $PSScriptRoot 'portable/README.txt') $publishDir
        Copy-Item (Join-Path $root 'LICENSE') (Join-Path $publishDir 'LICENSE.txt')
        foreach ($required in 'HexEditor.exe', 'portable.marker', 'README.txt', 'THIRD-PARTY-NOTICES.txt') {
            if (-not (Test-Path (Join-Path $publishDir $required))) { throw "Portable output is missing $required (PKG-05)." }
        }

        $zip = Join-Path $out "HexEditor-$Version-$Arch-portable.zip"
        # The zip has one root folder HexEditor\ (PKG-05 spec 1).
        # Entries are written one by one with '/' separators (Windows PowerShell 5.1 would write '\').
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $base = Split-Path -Parent $publishDir
            foreach ($file in Get-ChildItem $publishDir -Recurse -File) {
                $entry = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $archive.Dispose() }
        Write-Host "Created $zip"
    }
    'Installer' {
        Invoke-Checked 'dotnet' (@('publish') + $common + @('-o', $publishDir))
        if (-not (Get-ChildItem $publishDir -Filter 'Velopack*.dll')) { throw 'Installer output must contain Velopack (PKG-10).' }
        & $sign -Path $publishDir
        Push-Location $root
        try {
            Invoke-Checked 'dotnet' @('tool', 'restore')
            $pack = @(
                'vpk', 'pack',
                '--packId', 'HexEditor',
                # Velopack needs a version >= 0.0.1, so a local build (0.0.0-local) is packed as 0.0.1-local (PKG-28 spec 2).
                '--packVersion', $(if ($Version -eq '0.0.0-local') { '0.0.1-local' } else { $Version }),
                '--packDir', $publishDir,
                '--mainExe', 'HexEditor.exe',
                '--packTitle', 'HexEditor',
                '--packAuthors', 'Hiroyura',
                '--icon', (Join-Path $root 'src/HexEditor.App/Assets/AppIcon.ico'),
                '--shortcuts', 'StartMenuRoot',
                '--noPortable',
                '--runtime', $rid,
                # Channels (PKG-21 spec 3): win-<arch>-stable for stable versions, win-<arch>-preview otherwise.
                '--channel', $channel,
                '--outputDir', $out)
            if ($PreviousReleaseDir -and (Test-Path $PreviousReleaseDir)) {
                # vpk pack makes a delta package against the newest full package in the output folder.
                Get-ChildItem $PreviousReleaseDir -File | Copy-Item -Destination $out
            }
            if ($env:HEX_SIGNING_ENABLED -eq 'true') {
                # Setup.exe and Update.exe are signed through the same script (PKG-25 spec 5).
                $pack += @('--signTemplate', "pwsh -NoProfile -File `"$sign`" -Path {{file}}")
            }
            Invoke-Checked 'dotnet' $pack
        }
        finally { Pop-Location }
        # File name required by PKG-07 and PKG-26: HexEditor-<ver>-<arch>-Setup.exe. The Velopack name stays
        # as well, because vpk upload reads it from the release files.
        Copy-Item (Join-Path $out "HexEditor-$channel-Setup.exe") (Join-Path $out "HexEditor-$Version-$Arch-Setup.exe")
    }
    'Msix' {
        # Store-only and unsigned: the Store signs it on submission (PKG-02, PKG-04).
        Invoke-Checked 'dotnet' (@('publish') + $common + @(
            '-p:GenerateAppxPackageOnBuild=true',
            '-p:AppxPackageSigningEnabled=false',
            '-p:AppxBundle=Never',
            "-p:AppxPackageDir=$staging\AppPackages\"))
        $msix = Get-ChildItem (Join-Path $staging 'AppPackages') -Recurse -Filter '*.msix' |
            Where-Object { $_.FullName -notmatch '\\Dependencies\\' } | Select-Object -First 1
        if (-not $msix) { throw 'No .msix was produced.' }
        Copy-Item $msix.FullName (Join-Path $out "HexEditor-$Version-$Arch.msix")
    }
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Get-ChildItem $out -Recurse -File | Where-Object { $_.Extension -in '.zip', '.exe', '.msix', '.nupkg' } |
    ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.FullName }
