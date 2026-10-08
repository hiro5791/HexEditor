<#
.SYNOPSIS
  Checks the release that the release workflow has just made (release.yml, publish job).

.DESCRIPTION
    TC-PKG-01-01  x64 and arm64: Setup.exe and portable.zip in the release, .msix in the workflow artifact
                  (store-submission), and one .msixbundle
    TC-PKG-24-01  a stable tag makes a draft release        (stable versions)
    TC-PKG-24-02  a preview tag is published as a pre-release (preview versions)
    TC-PKG-24-04  delta packages for x64 and arm64 when an earlier stable release exists
    TC-PKG-25-01  with signing disabled, Setup.exe and HexEditor.exe have no signature
    TC-PKG-25-04  no .msix, .msixbundle, .appx or .appxbundle in the release
    TC-PKG-26-01  the file list of PKG-26 and the "which file" table at the top of the notes
    TC-PKG-26-02  SHA256SUMS.txt is in sha256sum format, lists every other file and the values match
  The cases run against this repository's own release, so the cases of PKG-23 / PKG-24 that need a deliberately
  broken commit, a fork or branch protection (TC-PKG-23-02, TC-PKG-23-03, TC-PKG-24-03) are not here.

  By default the release is read with gh (GH_TOKEN). For the script's own tests, -ReleaseJson (the output of
  `gh release view --json isDraft,isPrerelease,body,assets`) and -AssetsDir (the downloaded files) replace gh,
  and -PreviousStable tells whether an earlier stable release exists.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$StoreDir,
    [string]$WorkDir,
    [string]$ReleaseJson,
    [string]$AssetsDir,
    [Nullable[bool]]$PreviousStable
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$Version = $Tag.TrimStart('v')
$preview = $Version -match '-'
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) 'release-tests' }
New-Item -ItemType Directory -Force $WorkDir | Out-Null

# ---- The release ----

if ($ReleaseJson) {
    $release = Get-Content $ReleaseJson -Raw | ConvertFrom-Json
} else {
    $release = gh release view $Tag --json isDraft,isPrerelease,body,assets | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "gh release view $Tag failed" }
}
$assets = @($release.assets | ForEach-Object { $_.name })
Write-Host "Release $Tag (draft: $($release.isDraft), pre-release: $($release.isPrerelease)): $($assets -join ', ')"

if (-not $AssetsDir) {
    $AssetsDir = Join-Path $WorkDir 'assets'
    if (Test-Path $AssetsDir) { Remove-Item $AssetsDir -Recurse -Force }
    gh release download $Tag --dir $AssetsDir
    if ($LASTEXITCODE -ne 0) { throw "gh release download $Tag failed" }
}

if ($null -eq $PreviousStable) {
    $others = gh release list --exclude-drafts --limit 100 --json tagName,isPrerelease | ConvertFrom-Json
    $PreviousStable = [bool]@($others | Where-Object { $_.tagName -ne $Tag -and -not $_.isPrerelease }).Count
}

$archs = 'x64', 'arm64'

Invoke-TestCase 'TC-PKG-01-01' '3 distributions x 2 architectures' {
    foreach ($arch in $archs) {
        foreach ($name in "HexEditor-$Version-$arch-Setup.exe", "HexEditor-$Version-$arch-portable.zip") {
            Assert-True ($assets -contains $name) "$name is not in the release"
        }
        Assert-True (@(Get-ChildItem $StoreDir -Recurse -Filter "HexEditor-$Version-$arch.msix").Count -eq 1) "HexEditor-$Version-$arch.msix is not in the workflow artifact"
    }
    $bundles = @(Get-ChildItem $StoreDir -Recurse -Filter '*.msixbundle')
    Assert-True ($bundles.Count -eq 1) "$($bundles.Count) .msixbundle files in the workflow artifact"
    # The bundle has both architectures.
    $zip = [System.IO.Compression.ZipFile]::OpenRead($bundles[0].FullName)
    try { $inBundle = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
    foreach ($arch in $archs) {
        Assert-True (@($inBundle | Where-Object { $_ -like "*$arch*.msix" }).Count -ge 1) "the bundle has no $arch package ($($inBundle -join ', '))"
    }
}

if ($preview) {
    Invoke-TestCase 'TC-PKG-24-02' 'a preview tag is published as a pre-release' {
        Assert-True (-not $release.isDraft) 'the preview release is a draft'
        Assert-True ($release.isPrerelease) 'the preview release is not a pre-release'
    }
} else {
    Invoke-TestCase 'TC-PKG-24-01' 'a stable tag makes a draft release' {
        Assert-True ($release.isDraft) 'the stable release is not a draft'
        Assert-True (-not $release.isPrerelease) 'the stable release is a pre-release'
    }
}

Invoke-TestCase 'TC-PKG-24-04' 'delta packages from the second release' {
    if (-not $PreviousStable) { Skip-TestCase 'there is no earlier stable release, so there is nothing to make a delta from.' }
    foreach ($arch in $archs) {
        Assert-True (@($assets | Where-Object { $_ -like "*$Version*win-$arch*-delta.nupkg" }).Count -ge 1) "no delta package for win-$arch"
    }
}

Invoke-TestCase 'TC-PKG-25-01' 'unsigned when signing is disabled' {
    if ($env:HEX_SIGNING_ENABLED -eq 'true') { Skip-TestCase 'signing is enabled (HEX_SIGNING_ENABLED).' }
    foreach ($arch in $archs) {
        $setup = Join-Path $AssetsDir "HexEditor-$Version-$arch-Setup.exe"
        Assert-True ((Get-AuthenticodeSignature $setup).Status -eq 'NotSigned') "$setup is signed"
        $portable = Join-Path $WorkDir "portable-$arch"
        if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
        Expand-Archive (Join-Path $AssetsDir "HexEditor-$Version-$arch-portable.zip") $portable
        $exe = Join-Path $portable 'HexEditor\HexEditor.exe'
        Assert-True ((Get-AuthenticodeSignature $exe).Status -eq 'NotSigned') "HexEditor.exe ($arch) is signed"
    }
}

Invoke-TestCase 'TC-PKG-25-04' 'no MSIX in the release' {
    $packages = @($assets | Where-Object { [System.IO.Path]::GetExtension($_) -in '.msix', '.msixbundle', '.appx', '.appxbundle' })
    Assert-True ($packages.Count -eq 0) "MSIX files in the release: $($packages -join ', ')"
}

Invoke-TestCase 'TC-PKG-26-01' 'the files of the release' {
    foreach ($arch in $archs) {
        # Velopack feeds (PKG-21 spec 3): a preview version is in win-<arch>-preview, a stable version in both channels.
        $feeds = if ($Version -match '-') { @("releases.win-$arch-preview.json") } else { @("releases.win-$arch-stable.json", "releases.win-$arch-preview.json") }
        foreach ($name in @("HexEditor-$Version-$arch-Setup.exe", "HexEditor-$Version-$arch-portable.zip") + $feeds) {
            Assert-True ($assets -contains $name) "$name is not in the release"
        }
        Assert-True (@($assets | Where-Object { $_ -like "*$Version*win-$arch*-full.nupkg" }).Count -ge 1) "no full package for win-$arch"
    }
    Assert-True ($assets -contains 'SHA256SUMS.txt') 'SHA256SUMS.txt is not in the release'
    Assert-True ("$($release.body)".TrimStart().StartsWith('## Which file should I download?')) 'the notes do not start with the "which file" table'
    Assert-True ("$($release.body)" -match '\| --- \| --- \|') 'the notes have no table'
    Add-TestNote 'TC-PKG-26-01: HexEditor-<ver>-<arch>-csharp.zip is not checked (the C# script component, AUTO-03, does not exist yet).'
}

Invoke-TestCase 'TC-PKG-26-02' 'SHA256SUMS.txt' {
    $sums = Join-Path $AssetsDir 'SHA256SUMS.txt'
    $listed = @{}
    foreach ($line in ([System.IO.File]::ReadAllText($sums) -split "`n" | Where-Object { $_ })) {
        Assert-True ($line -match '^([0-9a-f]{64})  ([^ ].*)$') "not in sha256sum format: '$line'"
        $listed[$Matches[2]] = $Matches[1]
    }
    foreach ($name in $assets | Where-Object { $_ -ne 'SHA256SUMS.txt' }) {
        Assert-True ($listed.ContainsKey($name)) "$name is not in SHA256SUMS.txt"
        $actual = (Get-FileHash (Join-Path $AssetsDir $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-True ($actual -eq $listed[$name]) "the SHA-256 of $name does not match"
    }
    foreach ($name in $listed.Keys) { Assert-True ($assets -contains $name) "SHA256SUMS.txt lists $name, which is not in the release" }
}

Complete-TestRun "Release tests ($Tag)"
