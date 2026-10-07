<#
.SYNOPSIS
  Code signing (PKG-25). The only place that signs anything.

.DESCRIPTION
  For now nothing is code-signed (decided 2026-10-07). This script is called from
  build/publish.ps1 (step 3 of PKG-24) and from the vpk pack signing hook, and does
  nothing unless the repository variable HEX_SIGNING_ENABLED is 'true'. While signing
  is disabled it needs no secrets and prints "Code signing: disabled" (in Japanese too).

  -Path is a folder (its files that match $SignTargets are signed) or one file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path
)

$ErrorActionPreference = 'Stop'

# The files to sign (PKG-25 spec 1). Third-party binaries are shipped as they are.
$SignTargets = @(
    'HexEditor.exe',
    'hexed.exe',
    'HexEditor.*.dll',
    'HexEditor.Elevated.exe',
    'Setup.exe',
    '*-Setup.exe',
    'Update.exe'
)

if ($env:HEX_SIGNING_ENABLED -ne 'true') {
    # "Code signing: disabled" in Japanese, written with char codes so that this file stays ASCII.
    $ja = (-join [char[]](0x30B3, 0x30FC, 0x30C9, 0x7F72, 0x540D)) + ': ' + (-join [char[]](0x7121, 0x52B9))
    Write-Host "Code signing: disabled ($ja)"
    exit 0
}

$files = if (Test-Path $Path -PathType Container) {
    Get-ChildItem $Path -File | Where-Object { $name = $_.Name; $SignTargets | Where-Object { $name -like $_ } }
} else {
    Get-Item $Path
}

# The signing method and certificate are decided when signing is introduced (PKG-25 spec 5).
throw "HEX_SIGNING_ENABLED is true, but no signing method is configured yet. Files: $($files.Name -join ', ')"
