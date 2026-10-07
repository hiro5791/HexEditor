# Helper for the distribution tests (test strategy 6.2): each test case is a script block run by
# Invoke-TestCase with its test case ID. Results go to the console and to a JUnit-like summary.
# Dot-source this file: . "$PSScriptRoot/TestCase.ps1"

$script:TestResults = New-Object System.Collections.Generic.List[object]

function Invoke-TestCase {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    # HEX_TEST_CASES=<ID>,<ID>... runs only those cases (to try one case against a local build).
    if ($env:HEX_TEST_CASES -and ($env:HEX_TEST_CASES -split ',' | ForEach-Object { $_.Trim() }) -notcontains $Id) { return }
    Write-Host "=== $Id $Title"
    $start = Get-Date
    try {
        & $Body
        $script:TestResults.Add([pscustomobject]@{ Id = $Id; Title = $Title; Result = 'Passed'; Message = ''; Seconds = ((Get-Date) - $start).TotalSeconds })
        Write-Host "PASS $Id"
    } catch {
        if ("$_" -like 'SKIPPED: *') {
            # Skip-TestCase: the feature the case needs does not exist yet. The reason goes to the summary.
            $reason = "$_".Substring(9)
            $script:TestResults.Add([pscustomobject]@{ Id = $Id; Title = $Title; Result = 'Skipped'; Message = $reason; Seconds = ((Get-Date) - $start).TotalSeconds })
            Write-Host "::notice::SKIP $Id $Title : $reason"
            return
        }
        $script:TestResults.Add([pscustomobject]@{ Id = $Id; Title = $Title; Result = 'Failed'; Message = "$_"; Seconds = ((Get-Date) - $start).TotalSeconds })
        Write-Host "::error::FAIL $Id $Title : $_"
    }
}

# Ends the current test case as Skipped (not Failed). Use only when the feature that the case needs
# does not exist yet or the environment cannot provide it, and give the reason.
function Skip-TestCase([Parameter(Mandatory)][string]$Reason) {
    throw "SKIPPED: $Reason"
}

# A part of the current test case that is not checked, with the reason. Shown in the log and in the summary.
$script:TestNotes = New-Object System.Collections.Generic.List[string]
function Add-TestNote([Parameter(Mandatory)][string]$Note) {
    Write-Host "::notice::$Note"
    $script:TestNotes.Add($Note)
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Complete-TestRun([string]$Name) {
    $failed = @($script:TestResults | Where-Object Result -eq 'Failed')
    $lines = @("## $Name", '', '| Test case | Result | Seconds | Message |', '| --- | --- | --- | --- |')
    foreach ($r in $script:TestResults) { $lines += "| $($r.Id) $($r.Title) | $($r.Result) | $('{0:N1}' -f $r.Seconds) | $($r.Message -replace '\|', '/') |" }
    $text = $lines -join "`n"
    if ($script:TestNotes.Count -gt 0) { $text += "`n`nNot checked:`n`n" + (($script:TestNotes | ForEach-Object { "- $_" }) -join "`n") }
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $text -Encoding utf8 } else { Write-Host $text }
    if ($failed.Count -gt 0) { exit 1 }
    exit 0
}

# Wait until a process has a main window (or the timeout). Never activates the window.
function Wait-MainWindow([System.Diagnostics.Process]$Process, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) { return $false }
        if ($Process.MainWindowHandle -ne 0) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# The 8-digit folder hash of PKG-11 and PKG-13 (same as DataDirectory.FolderHash).
function Get-FolderHash([string]$Folder) {
    $normalized = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\').ToUpperInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalized)) } finally { $sha.Dispose() }
    (-join ($bytes | ForEach-Object { $_.ToString('X2') })).Substring(0, 8)
}
