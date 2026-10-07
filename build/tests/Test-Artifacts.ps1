<#
.SYNOPSIS
  Distribution tests that inspect the build output without running the app.

.DESCRIPTION
  Run after build/publish.ps1 has built the distributions into -ArtifactsDir.
    TC-PKG-10-01  6 builds exist; Velopack only in the installer
    TC-PKG-10-02  an invalid HexDistro fails the build
    TC-UI-46-03   a stable build contains no pseudo-locale resources (only with -Stable)
  Also checks the portable zip layout (PKG-05 spec 1) and the generated MSIX version (PKG-03 spec 4).
#>
[CmdletBinding()]
param(
    [string]$ArtifactsDir,
    [string]$Version = '0.0.0-local',
    [switch]$Stable,
    [switch]$SkipInvalidDistro
)

$ErrorActionPreference = 'Stop'
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts' }
. "$PSScriptRoot/TestCase.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Version = $Version.TrimStart('v')

function Get-ZipEntries([string]$Path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try { @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }) } finally { $zip.Dispose() }
}

function Test-ZipContains([string]$Path, [string]$Pattern) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($e in $zip.Entries) {
            if ($e.FullName -like '*.pri') {
                $s = $e.Open(); $m = New-Object System.IO.MemoryStream
                try { $s.CopyTo($m) } finally { $s.Dispose() }
                $bytes = $m.ToArray()
                $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
                $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
                if ($ascii -match $Pattern -or $utf16 -match $Pattern) { return $true }
            }
            if ($e.FullName -match $Pattern) { return $true }
        }
        return $false
    } finally { $zip.Dispose() }
}

Invoke-TestCase 'TC-PKG-10-01' '6 builds and Velopack DLL' {
    foreach ($arch in 'x64', 'arm64') {
        $zip = Join-Path $ArtifactsDir "Portable/$arch/HexEditor-$Version-$arch-portable.zip"
        $setup = Join-Path $ArtifactsDir "Installer/$arch/HexEditor-$Version-$arch-Setup.exe"
        $msix = Join-Path $ArtifactsDir "Msix/$arch/HexEditor-$Version-$arch.msix"
        foreach ($f in $zip, $setup, $msix) { Assert-True (Test-Path $f) "missing $f" }
        $entries = Get-ZipEntries $zip
        Assert-True (-not ($entries | Where-Object { $_ -match '/Velopack[^/]*\.dll$' })) "Velopack DLL in $zip"
        $full = Get-ChildItem (Join-Path $ArtifactsDir "Installer/$arch") -Filter '*-full.nupkg' | Select-Object -First 1
        Assert-True ($null -ne $full) "no full package for $arch"
        Assert-True ((Get-ZipEntries $full.FullName) -match 'Velopack\.dll$') "Velopack.dll not in $($full.Name)"
    }
}

Invoke-TestCase 'PKG-05-1' 'portable zip layout' {
    foreach ($arch in 'x64', 'arm64') {
        $entries = Get-ZipEntries (Join-Path $ArtifactsDir "Portable/$arch/HexEditor-$Version-$arch-portable.zip")
        foreach ($required in 'HexEditor/HexEditor.exe', 'HexEditor/portable.marker', 'HexEditor/README.txt', 'HexEditor/THIRD-PARTY-NOTICES.txt') {
            Assert-True ($entries -contains $required) "$required missing ($arch)"
        }
        Assert-True (-not ($entries | Where-Object { -not $_.StartsWith('HexEditor/') })) "entries outside the HexEditor/ root folder ($arch)"
    }
}

Invoke-TestCase 'PKG-03-4' 'MSIX version generated from HexVersion' {
    $expected = ((& dotnet msbuild (Join-Path $root 'src/HexEditor.Platform/HexEditor.Platform.csproj') -nologo "-p:HexVersion=$Version" -getProperty:HexMsixVersion) | Out-String).Trim()
    foreach ($arch in 'x64', 'arm64') {
        $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $ArtifactsDir "Msix/$arch/HexEditor-$Version-$arch.msix"))
        try {
            $s = $zip.GetEntry('AppxManifest.xml').Open(); $r = New-Object System.IO.StreamReader($s)
            try { [xml]$manifest = $r.ReadToEnd() } finally { $r.Dispose() }
        } finally { $zip.Dispose() }
        Assert-True ($manifest.Package.Identity.Version -eq $expected) "$arch msix version $($manifest.Package.Identity.Version), expected $expected"
        Assert-True ($manifest.Package.Identity.Name -eq 'HexEditor') "Identity Name is $($manifest.Package.Identity.Name)"
    }
}

if (-not $SkipInvalidDistro) {
    Invoke-TestCase 'TC-PKG-10-02' 'invalid HexDistro' {
        $log = & dotnet build (Join-Path $root 'src/HexEditor.App/HexEditor.App.csproj') -p:HexDistro=Foo -p:Platform=x64 -r win-x64 -nologo 2>&1 | Out-String
        Assert-True ($LASTEXITCODE -ne 0) 'the build with HexDistro=Foo succeeded'
        Assert-True ($log -match 'HexDistro') 'the error does not mention HexDistro'
    }
}

if ($Stable) {
    Invoke-TestCase 'TC-UI-46-03' 'no pseudo-locales in a stable build' {
        foreach ($arch in 'x64', 'arm64') {
            foreach ($f in (Join-Path $ArtifactsDir "Portable/$arch/HexEditor-$Version-$arch-portable.zip"), (Join-Path $ArtifactsDir "Msix/$arch/HexEditor-$Version-$arch.msix")) {
                Assert-True (-not (Test-ZipContains $f 'qps-ploc')) "pseudo-locale resources in $f"
            }
            $full = Get-ChildItem (Join-Path $ArtifactsDir "Installer/$arch") -Filter '*-full.nupkg' | Select-Object -First 1
            Assert-True (-not (Test-ZipContains $full.FullName 'qps-ploc')) "pseudo-locale resources in $($full.Name)"
        }
    }
}

Complete-TestRun 'Artifact tests'
