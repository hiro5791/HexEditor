<#
.SYNOPSIS
  Make the winget manifests of one stable release (PKG-27).

.DESCRIPTION
  Fills the templates in build/winget/ (version, installer, en-US and ja-JP locales) with the version, the Setup.exe URLs
  of the GitHub release and their SHA-256 from SHA256SUMS.txt. Only the installer version (Setup.exe, x64 and arm64) is
  registered; preview versions are never submitted (PKG-27 spec 4), so a -preview.N version fails.
  The first version is submitted by hand with these files; later versions use wingetcreate update --submit
  (.github/workflows/winget.yml), which takes the same URLs.

.EXAMPLE
  ./build/winget-manifests.ps1 -Version 1.2.0 -Sha256Sums SHA256SUMS.txt -OutDir winget-out
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Sha256Sums,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Repository = 'https://github.com/hiro5791/HexEditor',
    [string]$ReleaseDate = (Get-Date -Format 'yyyy-MM-dd')
)

$ErrorActionPreference = 'Stop'
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Host "::error::winget gets stable versions only (PKG-27 spec 4); $Version is not one."
    exit 1
}

$sums = @{}
foreach ($line in [System.IO.File]::ReadAllLines($Sha256Sums)) {
    if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') { $sums[$Matches[2].Trim()] = $Matches[1].ToUpperInvariant() }
}

$values = @{ 'VERSION' = $Version; 'RELEASE_DATE' = $ReleaseDate }
foreach ($arch in 'x64', 'arm64') {
    $name = "HexEditor-$Version-$arch-Setup.exe"
    if (-not $sums.ContainsKey($name)) {
        Write-Host "::error::$name is not in $Sha256Sums."
        exit 1
    }
    $key = $arch.ToUpperInvariant()
    $values["$($key)_URL"] = "$Repository/releases/download/v$Version/$name"
    $values["$($key)_SHA256"] = $sums[$name]
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($template in Get-ChildItem (Join-Path $PSScriptRoot 'winget') -Filter '*.yaml') {
    $text = [System.IO.File]::ReadAllText($template.FullName, $utf8)
    # The comment lines of the templates (other than the schema line) are notes for this repository.
    $text = (($text -split "`n") | Where-Object { -not $_.StartsWith('# ') -or $_.StartsWith('# yaml-language-server') }) -join "`n"
    foreach ($k in $values.Keys) { $text = $text.Replace("{{$k}}", $values[$k]) }
    if ($text -match '\{\{') { throw "Unfilled placeholder in $($template.Name)" }
    [System.IO.File]::WriteAllText((Join-Path $OutDir $template.Name), $text, $utf8)
}
Write-Host "Wrote the winget manifests of $Version to $OutDir"
