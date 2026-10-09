<#
.SYNOPSIS
  Marks the UI tests of priority-high test cases with [Trait(UiTest.Priority, UiTest.High)] (test strategy 9:
  pull requests run only the priority-high UI tests). The priority comes from the test case documents
  (docs/test/cases/*.md, the priority row of each case); a test is high when one of its test case IDs is high.
  Run it after changing a priority or the test case IDs of a UI test. Core.Tests TestSpec/UiTestPriorityTests
  fails when the traits and the documents disagree.
.EXAMPLE
  ./build/Update-UiTestPriority.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

# The priority row and the value "high" of the documents (Japanese; written as escapes to keep this file ASCII).
$priorityWord = [string][char]0x512A + [char]0x5148 + [char]0x5EA6
$priorityRow = [regex]::new('^\|\s*' + $priorityWord + '\s*\|\s*(.+?)\s*\|')
$high = [string][char]0x9AD8

$priority = @{}
foreach ($doc in Get-ChildItem (Join-Path $root 'docs/test/cases') -Filter '*.md') {
    $current = $null
    foreach ($line in [System.IO.File]::ReadAllLines($doc.FullName, $utf8)) {
        if ($line -match '^### (TC-[A-Z0-9]+-\d+-\d+)') { $current = $Matches[1]; continue }
        if ($current) {
            $m = $priorityRow.Match($line)
            if ($m.Success) { $priority[$current] = $m.Groups[1].Value; $current = $null }
        }
    }
}

$mark = '[Trait(UiTest.Priority, UiTest.High)]'
$changed = 0
foreach ($file in Get-ChildItem (Join-Path $root 'tests/HexEditor.UITests') -Filter '*.cs') {
    $lines = [System.Collections.Generic.List[string]]::new([System.IO.File]::ReadAllLines($file.FullName, $utf8))
    $out = [System.Collections.Generic.List[string]]::new()
    $block = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('[') -or $trimmed.StartsWith('///')) { $block.Add($line); continue }
        if ($block.Count -gt 0 -and $trimmed -match '^public (async )?(Task|void)\b') {
            $ids = @($block | ForEach-Object { [regex]::Matches($_, 'Trait\(UiTest\.TC, "(TC-[^"]+)"\)') | ForEach-Object { $_.Groups[1].Value } })
            $isHigh = @($ids | Where-Object { $priority[$_] -eq $high }).Count -gt 0
            $hasMark = @($block | Where-Object { $_.Trim() -eq $mark }).Count -gt 0
            if ($hasMark -and -not $isHigh) {
                $block = [System.Collections.Generic.List[string]]::new([string[]]@($block | Where-Object { $_.Trim() -ne $mark }))
                $changed++
            } elseif ($isHigh -and -not $hasMark) {
                $last = -1
                for ($i = 0; $i -lt $block.Count; $i++) { if ($block[$i] -match 'Trait\(UiTest\.TC, ') { $last = $i } }
                $indent = ($block[$last] -replace '^(\s*).*$', '$1')
                $block.Insert($last + 1, $indent + $mark)
                $changed++
            }
        }
        foreach ($b in $block) { $out.Add($b) }
        $block.Clear()
        $out.Add($line)
    }
    foreach ($b in $block) { $out.Add($b) }
    $text = ($out -join "`n")
    if (-not $text.EndsWith("`n")) { $text += "`n" }
    $original = [System.IO.File]::ReadAllText($file.FullName, $utf8)
    if ($text -ne $original) { [System.IO.File]::WriteAllText($file.FullName, $text, $utf8) }
}
Write-Host "UI tests changed: $changed"
