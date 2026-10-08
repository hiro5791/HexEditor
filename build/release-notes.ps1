<#
.SYNOPSIS
  Make the release notes for one version (PKG-24 step 9, PKG-26 spec 3, PKG-29 spec 2).

.DESCRIPTION
  Takes the section "## [<version>]" of CHANGELOG.md (Keep a Changelog) and fills
  build/release/release-notes-template.md: the "which file" table first, then the changes,
  known issues, the SmartScreen / Microsoft Store notes and how to check SHA256SUMS.txt.
  Fails (exit 1) if CHANGELOG.md has no section for the version (PKG-24 step 1, PKG-29 "errors").
  -TranslationTable is the Markdown table of the translation progress (PKG-29 spec 3, F1-26) made by
  tools/I18nTool release-table. Without it (or if the file does not exist) the table is left out with a warning.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Changelog,
    [string]$Template,
    [string]$StoreUrl = 'https://apps.microsoft.com/search?query=HexEditor',
    [string]$TranslationTable,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
if (-not $Changelog) { $Changelog = Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md' }
if (-not $Template) { $Template = Join-Path $PSScriptRoot 'release/release-notes-template.md' }
$Version = $Version.TrimStart('v')
$text = [System.IO.File]::ReadAllText($Changelog)
$heading = [regex]::Escape("## [$Version]")
$match = [regex]::Match($text, "(?ms)^$heading[^\n]*\n(?<body>.*?)(?=^## |\z)")
if (-not $match.Success) {
    Write-Host "::error::CHANGELOG.md has no section for $Version (## [$Version])."
    exit 1
}

$body = $match.Groups['body'].Value.Trim()
# Known Issues are shown in their own part (PKG-29 spec 2.4).
$known = ''
$k = [regex]::Match($body, '(?ms)^### Known Issues\s*\n(?<k>.*?)(?=^### |\z)')
if ($k.Success) {
    $known = "## Known issues`n`n" + $k.Groups['k'].Value.Trim()
    $body = $body.Remove($k.Index, $k.Length).Trim()
}

$notes = [System.IO.File]::ReadAllText($Template)
$translations = ''
if ($TranslationTable -and (Test-Path $TranslationTable)) {
    $translations = [System.IO.File]::ReadAllText($TranslationTable).Trim()
} else {
    # PKG-29 errors: without translation-coverage.json the table is left out with a warning.
    Write-Host '::warning::No translation progress table; it is left out of the release notes.'
}

$notes = $notes.Replace('{{VERSION}}', $Version).Replace('{{STORE_URL}}', $StoreUrl).Replace('{{CHANGELOG}}', $body).Replace('{{TRANSLATIONS}}', $translations).Replace('{{KNOWN_ISSUES}}', $known)

if ($OutFile) {
    [System.IO.File]::WriteAllText($OutFile, $notes, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote $OutFile"
} else {
    $notes
}
