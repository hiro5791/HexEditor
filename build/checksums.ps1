<#
.SYNOPSIS
  Write SHA256SUMS.txt for the release files (PKG-26 spec 2).

.DESCRIPTION
  Every file directly in -Dir (except SHA256SUMS.txt itself) gets one line in sha256sum format:
  "<64 lowercase hex digits><two spaces><file name>". Lines are sorted by file name.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Dir
)

$ErrorActionPreference = 'Stop'
$output = Join-Path $Dir 'SHA256SUMS.txt'
$lines = Get-ChildItem $Dir -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
# LF line endings and no BOM, like sha256sum.
[System.IO.File]::WriteAllText($output, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $output ($($lines.Count) files)"
