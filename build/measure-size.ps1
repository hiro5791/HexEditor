<#
.SYNOPSIS
  Measure the distribution sizes and compare them with the targets (PKG-16).

.DESCRIPTION
  Reads the files that build/publish.ps1 wrote under -ArtifactsDir and measures, per architecture:
    portable zip, .msix, Setup.exe, installed size (the unpacked full package) and the delta package.
  Writes a Markdown table (this build, the previous release, target) to $env:GITHUB_STEP_SUMMARY
  (or to the console). A size 10% or more over its target fails the script (exit 1), unless -WarnOnly
  is given (pull requests, or builds approved with the label size-increase-approved).

  -PreviousJson is the sizes.json of the previous release (optional). -OutJson writes this build's sizes.
#>
[CmdletBinding()]
param(
    [string]$ArtifactsDir,
    [string]$PreviousJson,
    [string]$OutJson,
    [switch]$WarnOnly
)

$ErrorActionPreference = 'Stop'
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$MB = 1MB

# Targets in MB per architecture (PKG-16 spec 1).
$targets = [ordered]@{
    'portable zip'   = 100
    'msix'           = 100
    'Setup.exe'      = 110
    'installed size' = 300
    'delta package'  = 15
}

function Find-One([string]$pattern) {
    Get-ChildItem $ArtifactsDir -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Get-UnpackedSize([string]$nupkg) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try { ($zip.Entries | Measure-Object -Property Length -Sum).Sum } finally { $zip.Dispose() }
}

$sizes = [ordered]@{}
foreach ($arch in 'x64', 'arm64') {
    $values = [ordered]@{}
    $f = Find-One "HexEditor-*-$arch-portable.zip"; if ($f) { $values['portable zip'] = $f.Length }
    $f = Find-One "HexEditor-*-$arch.msix"; if ($f) { $values['msix'] = $f.Length }
    $f = Find-One "HexEditor-*-$arch-Setup.exe"; if ($f) { $values['Setup.exe'] = $f.Length }
    # The architecture is in the folder (artifacts/Installer/<arch>, HexEditor-Installer-<arch>) or in the channel (win-<arch>).
    $archPattern = "(^|[\\/_-])$arch([\\/_.-]|$)"
    $f = Get-ChildItem $ArtifactsDir -Recurse -File -Filter '*-full.nupkg' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match $archPattern } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) { $values['installed size'] = Get-UnpackedSize $f.FullName }
    $f = Get-ChildItem $ArtifactsDir -Recurse -File -Filter '*-delta.nupkg' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match $archPattern } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) { $values['delta package'] = $f.Length }
    $sizes[$arch] = $values
}

$previous = $null
if ($PreviousJson -and (Test-Path $PreviousJson)) { $previous = Get-Content $PreviousJson -Raw | ConvertFrom-Json }

function Format-MB($bytes) { if ($null -eq $bytes) { '-' } else { '{0:N1} MB' -f ($bytes / $MB) } }

$lines = @('## Distribution sizes (PKG-16)', '', '| Item | Arch | This build | Previous release | Target | Status |', '| --- | --- | --- | --- | --- | --- |')
$failures = @()
foreach ($arch in 'x64', 'arm64') {
    foreach ($item in $targets.Keys) {
        $now = $sizes[$arch][$item]
        $before = $null
        if ($previous -and $previous.$arch -and ($previous.$arch.PSObject.Properties.Name -contains $item)) { $before = $previous.$arch.$item }
        $limit = $targets[$item] * $MB
        $status = 'not built'
        if ($null -ne $now) {
            if ($now * 10 -ge $limit * 11) {
                $status = 'OVER (10% or more)'
                $failures += "$item ($arch) is $(Format-MB $now), target $($targets[$item]) MB"
            } elseif ($now -gt $limit) {
                $status = 'over target'
            } else {
                $status = 'ok'
            }
        }
        $lines += "| $item | $arch | $(Format-MB $now) | $(Format-MB $before) | $($targets[$item]) MB | $status |"
    }
}

$text = $lines -join "`n"
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $text -Encoding utf8 } else { Write-Host $text }
if ($OutJson) { $sizes | ConvertTo-Json -Depth 4 | Set-Content -Path $OutJson -Encoding utf8 }

if ($failures.Count -gt 0) {
    foreach ($f in $failures) {
        if ($WarnOnly) { Write-Host "::warning::Size over target: $f" } else { Write-Host "::error::Size over target: $f" }
    }
    if (-not $WarnOnly) { exit 1 }
}
exit 0
