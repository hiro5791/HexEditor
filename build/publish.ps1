<#
.SYNOPSIS
  Build one HexEditor distribution (PKG-10). CI and local builds use this same script.

.EXAMPLE
  ./build/publish.ps1 -Distro Portable -Arch x64
  ./build/publish.ps1 -Distro Installer -Arch arm64
  ./build/publish.ps1 -Distro Msix -Arch x64

  Output goes to artifacts/<Distro>/<Arch>/:
    Portable  : HexEditor-<ver>-<arch>-Portable.zip (contains portable.marker, PKG-03)
    Installer : Velopack Setup.exe, full package and release files (PKG-07)
    Msix      : unsigned .msix for Microsoft Store submission (PKG-04)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Msix', 'Installer', 'Portable')][string]$Distro,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch,
    [string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/HexEditor.App/HexEditor.App.csproj'
$rid = "win-$Arch"
$platform = if ($Arch -eq 'x64') { 'x64' } else { 'ARM64' }

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}

$out = Join-Path $root "artifacts/$Distro/$Arch"
$publishDir = Join-Path $out 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    Write-Host "> $exe $($arguments -join ' ')"
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

$common = @(
    $project, '-c', $Configuration, '-r', $rid,
    "-p:Platform=$platform", "-p:HexDistro=$Distro", "-p:Version=$Version", '-nologo'
)

switch ($Distro) {
    'Portable' {
        Invoke-Checked 'dotnet' (@('publish') + $common + @('-o', $publishDir))
        if (Get-ChildItem $publishDir -Filter 'Velopack*.dll') { throw 'Portable output must not contain Velopack (PKG-10).' }

        # The marker tells the app to keep its data next to the exe (PKG-12, PKG-13).
        Set-Content -Path (Join-Path $publishDir 'portable.marker') -Value '' -Encoding ascii
        $zip = Join-Path $out "HexEditor-$Version-$Arch-Portable.zip"
        Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip
        Remove-Item $publishDir -Recurse -Force
        Write-Host "Created $zip"
    }
    'Installer' {
        Invoke-Checked 'dotnet' (@('publish') + $common + @('-o', $publishDir))
        Push-Location $root
        try {
            Invoke-Checked 'dotnet' @('tool', 'restore')
            Invoke-Checked 'dotnet' @(
                'vpk', 'pack',
                '--packId', 'HexEditor',
                '--packVersion', $Version,
                '--packDir', $publishDir,
                '--mainExe', 'HexEditor.App.exe',
                '--packTitle', 'HexEditor',
                '--packAuthors', 'Hiroyura',
                '--icon', (Join-Path $root 'src/HexEditor.App/Assets/AppIcon.ico'),
                '--shortcuts', 'StartMenuRoot',
                '--noPortable',
                '--runtime', $rid,
                '--channel', $rid,
                '--outputDir', $out)
        }
        finally { Pop-Location }
        Remove-Item $publishDir -Recurse -Force
        # File name required by PKG-07: HexEditor-<ver>-<arch>-Setup.exe
        Rename-Item (Join-Path $out "HexEditor-$rid-Setup.exe") "HexEditor-$Version-$Arch-Setup.exe"
    }
    'Msix' {
        # Store-only and unsigned: the Store signs it on submission (PKG-04).
        Invoke-Checked 'dotnet' (@('publish') + $common + @(
            '-p:GenerateAppxPackageOnBuild=true',
            '-p:AppxPackageSigningEnabled=false',
            '-p:AppxBundle=Never',
            "-p:AppxPackageDir=$out\"))
    }
}

Get-ChildItem $out -Recurse -File | Where-Object { $_.Extension -in '.zip', '.exe', '.msix', '.nupkg' } |
    ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.FullName }
