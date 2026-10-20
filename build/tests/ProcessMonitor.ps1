# Process Monitor (Sysinternals) from the command line, for the registry checks of the distribution tests
# (TC-PKG-06-01, TC-PKG-08-04). CI runners only: Process Monitor loads a driver and needs administrator rights.
# Dot-source after TestCase.ps1:  . "$PSScriptRoot/ProcessMonitor.ps1"

$script:ProcmonExe = $null

# Downloads Process Monitor into -Folder and returns the exe for this machine's architecture.
function Get-ProcessMonitor([Parameter(Mandatory)][string]$Folder) {
    if ($script:ProcmonExe) { return $script:ProcmonExe }
    if (-not $env:GITHUB_ACTIONS) { throw 'Process Monitor loads a driver; run it only on CI runners.' }
    New-Item -ItemType Directory -Force $Folder | Out-Null
    $zip = Join-Path $Folder 'ProcessMonitor.zip'
    Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/ProcessMonitor.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $Folder -Force
    $name = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'Procmon64a.exe' } else { 'Procmon64.exe' }
    $script:ProcmonExe = Join-Path $Folder $name
    if (-not (Test-Path $script:ProcmonExe)) { throw "$name is not in ProcessMonitor.zip" }
    $script:ProcmonExe
}

# Starts recording every event into the backing file (no filter; the events are filtered after the export).
function Start-ProcessMonitor([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$BackingFile) {
    Start-Process $Exe -ArgumentList "/AcceptEula /Quiet /Minimized /BackingFile `"$BackingFile`""
    $deadline = (Get-Date).AddSeconds(60)
    $started = $false
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $BackingFile) -and (Get-Item $BackingFile).Length -gt 0) { $started = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $started) { throw 'Process Monitor did not start recording.' }
    # The backing file exists before the driver is capturing. Prove that events are recorded: generate registry reads
    # (reg.exe query only reads) until the file grows.
    $size = (Get-Item $BackingFile).Length
    while ((Get-Date) -lt $deadline) {
        & reg.exe query 'HKCU\Software' | Out-Null
        Start-Sleep -Milliseconds 700
        $now = (Get-Item $BackingFile).Length
        if ($now -gt $size) { Start-Sleep -Seconds 2; return }
        $size = $now
    }
    Write-Host 'Process Monitor: the backing file did not grow during the start probe; continuing (the caller checks the recording).'
}

# Registry writes. RegCreateKey also opens existing keys (RegCreateKeyEx), so it counts only when it created a key
# or was refused.
$script:RegistryWriteOperations = 'RegSetValue', 'RegDeleteKey', 'RegDeleteValue', 'RegRenameKey'

<#
  Stops the recording, exports it as CSV and returns the registry writes of the processes whose name matches
  -ProcessPattern (rows with Process Name, PID, Operation, Path, Result, Detail). The CSV of a whole run is large,
  so it is read line by line and only registry lines are parsed.
#>
function Stop-ProcessMonitor {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(Mandatory)][string]$BackingFile,
        [Parameter(Mandatory)][string]$ProcessPattern
    )
    Start-Process $Exe -ArgumentList '/AcceptEula /Terminate' -Wait
    # Wait until the backing file is closed and its size has settled (the last events are flushed on termination).
    $last = -1L
    $stable = 0
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and $stable -lt 3) {
        Start-Sleep -Seconds 1
        $length = (Get-Item $BackingFile).Length
        try {
            $stream = [System.IO.File]::Open($BackingFile, 'Open', 'Read', 'None')
            $stream.Dispose()
            $free = $true
        } catch { $free = $false }
        if ($free -and $length -eq $last) { $stable++ } else { $stable = 0 }
        $last = $length
    }
    $csv = [System.IO.Path]::ChangeExtension($BackingFile, '.csv')
    Start-Process $Exe -ArgumentList "/AcceptEula /Quiet /OpenLog `"$BackingFile`" /SaveAs `"$csv`"" -Wait
    if (-not (Test-Path $csv)) { throw 'Process Monitor did not export the log.' }
    Read-RegistryWrites $csv $ProcessPattern
}

# The registry writes in an exported CSV of Process Monitor (see Stop-ProcessMonitor).
function Read-RegistryWrites([Parameter(Mandatory)][string]$Csv, [Parameter(Mandatory)][string]$ProcessPattern) {
    # Every field is quoted ("a","b",...); the fields do not contain '","'.
    $columns = $null
    $rows = New-Object System.Collections.Generic.List[object]
    $processes = New-Object 'System.Collections.Generic.HashSet[string]'
    $total = 0
    $lines = 0
    foreach ($line in [System.IO.File]::ReadLines($Csv)) {
        $lines++
        # Remove only the outer quotes (Trim('"') would also eat the quotes of an empty last field).
        $text = $line.TrimStart([char]0xFEFF).Trim()
        if ($text.Length -ge 2 -and $text.StartsWith('"') -and $text.EndsWith('"')) { $text = $text.Substring(1, $text.Length - 2) }
        $fields = $text -split '","'
        if (-not $columns) {
            $columns = @{}
            for ($i = 0; $i -lt $fields.Count; $i++) { $columns[$fields[$i]] = $i }
            foreach ($name in 'Process Name', 'Operation', 'Path', 'Result', 'Detail') {
                if (-not $columns.ContainsKey($name)) { throw "The Process Monitor CSV has no column '$name': $line" }
            }
            continue
        }
        # Blank lines, and the continuation of a value with a line break (in Detail), have fewer fields.
        if ($fields.Count -lt $columns.Count) { continue }
        $operation = $fields[$columns['Operation']]
        if (-not $operation -or -not $operation.StartsWith('Reg')) { continue }
        $total++
        $process = $fields[$columns['Process Name']]
        if ($process -notmatch $ProcessPattern) { continue }
        [void]$processes.Add($process)
        $result = $fields[$columns['Result']]
        $detail = $fields[$columns['Detail']]
        if ($operation -in $script:RegistryWriteOperations -or
            ($operation -eq 'RegCreateKey' -and ($detail -match 'REG_CREATED_NEW_KEY' -or $result -eq 'ACCESS DENIED'))) {
            $rows.Add([pscustomobject]@{ Process = $process; Operation = $operation; Path = $fields[$columns['Path']]; Result = $result; Detail = $detail })
        }
    }
    Write-Host "Process Monitor: registry events of $(@($processes) -join ', '); $($rows.Count) writes. (registry events of all processes: $total; CSV lines: $lines)"
    [pscustomobject]@{ Processes = @($processes); Writes = $rows.ToArray() }
}
