<#
.SYNOPSIS
  Installs (or extracts) one test build for the UI tests of TC-PKG-01-02 and sets HEXEDITOR_APP_EXE (CI runners only).

.DESCRIPTION
  -Distro Portable  : -Path is the portable zip; it is extracted
  -Distro Installer : -Path is Setup.exe; it is installed with --silent (the app that it starts is closed)
  -Distro Msix      : -Path is the .msix; it is signed with a throwaway certificate and installed (Msix.ps1)
  The UI tests (tests/HexEditor.UITests) then start HEXEDITOR_APP_EXE (HEXEDITOR_APP_DISTRO is the distribution): the extracted HexEditor.exe,
  %LocalAppData%\HexEditor\current\HexEditor.exe or the execution alias hexeditor.exe of the package.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Portable', 'Installer', 'Msix')][string]$Distro,
    [Parameter(Mandatory)][string]$Path
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"
. "$PSScriptRoot/Msix.ps1"
if (-not $env:GITHUB_ACTIONS) { throw 'This script installs HexEditor; run it only on CI runners.' }

switch ($Distro) {
    'Portable' {
        $target = Join-Path $env:RUNNER_TEMP 'ui-portable'
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        Expand-Archive -Path $Path -DestinationPath $target
        $exe = Join-Path $target 'HexEditor\HexEditor.exe'
    }
    'Installer' {
        $p = Start-Process $Path -ArgumentList '--silent' -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw "Setup.exe exit code $($p.ExitCode)" }
        Start-Sleep -Seconds 3
        Get-Process HexEditor -ErrorAction SilentlyContinue | Stop-Process -Force
        $exe = Join-Path $env:LOCALAPPDATA 'HexEditor\current\HexEditor.exe'
    }
    'Msix' {
        [void](Install-TestMsix $Path)
        $exe = Get-MsixAlias
    }
}

if (-not (Test-Path $exe)) { throw "$exe does not exist" }
Write-Host "HEXEDITOR_APP_EXE=$exe"
if ($env:GITHUB_ENV) {
    Add-Content -Path $env:GITHUB_ENV -Value "HEXEDITOR_APP_EXE=$exe" -Encoding utf8
    # The distribution, for the UI tests that run only with one of them (CiPrivilegedFact in tests/HexEditor.UITests).
    Add-Content -Path $env:GITHUB_ENV -Value "HEXEDITOR_APP_DISTRO=$Distro" -Encoding utf8
}
