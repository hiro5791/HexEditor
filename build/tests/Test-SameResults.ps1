<#
.SYNOPSIS
  TC-PKG-01-02: the UI tests give the same results in the three distributions.

.DESCRIPTION
  -Trx are the results (TRX) of the same UI tests (tests/HexEditor.UITests, Category=UI) run against the
  installer, portable and MSIX test builds, one file per distribution named ui-<Distro>.trx (ci.yml, ui-distro).
  Every test must have the same outcome in all of them; the differences are listed with the test case ID and
  the distribution.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Trx
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/TestCase.ps1"

# Test name -> @{ Outcome; Tc } of one TRX file.
function Read-Trx([string]$Path) {
    [xml]$x = Get-Content $Path -Raw -Encoding UTF8
    $ns = New-Object System.Xml.XmlNamespaceManager($x.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $tcById = @{}
    foreach ($test in $x.SelectNodes('//t:UnitTest', $ns)) {
        $ids = @($test.SelectNodes("t:Properties/t:Property[t:Key='TC']/t:Value", $ns) | ForEach-Object { $_.InnerText })
        $tcById[$test.id] = $ids -join ' '
    }
    $results = @{}
    foreach ($r in $x.SelectNodes('//t:UnitTestResult', $ns)) {
        $results[$r.testName] = [pscustomobject]@{ Outcome = $r.outcome; Tc = $tcById[$r.testId] }
    }
    $results
}

Invoke-TestCase 'TC-PKG-01-02' 'same UI test results in the three distributions' {
    $byDistro = [ordered]@{}
    foreach ($file in $Trx) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($file) -replace '^ui-', ''
        $byDistro[$name] = Read-Trx $file
        Write-Host "$name : $($byDistro[$name].Count) tests, $(@($byDistro[$name].Values | Where-Object Outcome -eq 'Passed').Count) passed"
        Assert-True ($byDistro[$name].Count -gt 0) "no test results in $file"
    }
    Assert-True ($byDistro.Count -eq 3) "results of $($byDistro.Count) distributions, expected 3 ($($byDistro.Keys -join ', '))"
    $names = @($byDistro.Values | ForEach-Object { $_.Keys } | Sort-Object -Unique)
    $differences = @()
    foreach ($test in $names) {
        $outcomes = @($byDistro.Keys | ForEach-Object { $r = $byDistro[$_][$test]; if ($r) { "$_=$($r.Outcome)" } else { "$_=missing" } })
        $distinct = @($outcomes | ForEach-Object { ($_ -split '=', 2)[1] } | Sort-Object -Unique)
        if ($distinct.Count -gt 1) {
            $tc = @($byDistro.Values | ForEach-Object { $_[$test] } | Where-Object { $_ -and $_.Tc } | Select-Object -First 1).Tc
            $differences += "$tc $test ($($outcomes -join ', '))"
        }
    }
    foreach ($d in $differences) { Write-Host "DIFFERENT: $d" }
    Assert-True ($differences.Count -eq 0) "$($differences.Count) tests differ: $($differences -join '; ')"
}

Complete-TestRun 'UI tests in the three distributions'
