<#
.SYNOPSIS
  Combine the x64 and arm64 .msix packages into one .msixbundle (PKG-02 spec 1, PKG-24 step 6).

.EXAMPLE
  ./build/publish.ps1 -Distro Msix -Arch x64 -Version 1.2.0
  ./build/publish.ps1 -Distro Msix -Arch arm64 -Version 1.2.0
  ./build/bundle-msix.ps1 -Version 1.2.0

  Output: artifacts/Msix/HexEditor-<ver>.msixbundle (unsigned; the Store signs it. PKG-04).
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0-local'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Version = $Version.TrimStart('v')
$msixRoot = Join-Path $root 'artifacts/Msix'
$packages = Get-ChildItem $msixRoot -Recurse -Filter "HexEditor-$Version-*.msix"
if ($packages.Count -lt 2) { throw "Expected the x64 and arm64 packages in $msixRoot, found $($packages.Count)." }

# The bundle version is the MSIX version of PKG-28, computed by Directory.Build.props (single source).
$props = & dotnet msbuild (Join-Path $root 'src/HexEditor.Platform/HexEditor.Platform.csproj') -nologo "-p:HexVersion=$Version" -getProperty:HexMsixVersion
if ($LASTEXITCODE -ne 0) { throw "Could not compute the MSIX version for $Version." }
$bundleVersion = ($props | Out-String).Trim()

# MakeAppx.exe from the Windows SDK.
$makeAppx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeAppx) {
    $makeAppx = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget/packages/microsoft.windows.sdk.buildtools') -Recurse -Filter 'makeappx.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
}
if (-not $makeAppx) { throw 'makeappx.exe was not found (install the Windows SDK).' }

$stage = Join-Path $root 'artifacts/staging/msixbundle'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
$packages | Copy-Item -Destination $stage
$bundle = Join-Path $msixRoot "HexEditor-$Version.msixbundle"
if (Test-Path $bundle) { Remove-Item $bundle -Force }
& $makeAppx.FullName bundle /d $stage /p $bundle /bv $bundleVersion /o
if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed with exit code $LASTEXITCODE" }
Remove-Item $stage -Recurse -Force
Write-Host "Created $bundle ($bundleVersion)"
