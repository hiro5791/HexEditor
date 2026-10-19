<#
.SYNOPSIS
  Creates, attaches, detaches or marks dirty the TD-FOR-VHDX-DELETED test volume for the privileged wipe tests
  (CI runners only: needs administrator rights).

.DESCRIPTION
  Used by tests/HexEditor.UITests/PrivilegedWipeTests.cs (TC-FOR-31-01 to TC-FOR-31-04).
  -Action Create: makes a fixed VHD (a fixed VHD is the raw disk followed by a 512-byte footer, so the app can open the
  file as a disk image), one GPT partition formatted NTFS by Windows (4 KiB clusters, label HEXWIPE), and writes
  keep.txt (the given content), slack.txt (SLACK-REMNANT repeated to 4 KiB, then truncated to 100 bytes so the slack keeps
  the text), tiny.txt (10 bytes, resident) and wipe.bin (WIPE-ME-0123456789 repeated to 1 MiB, then deleted so its
  content stays in unallocated clusters). The VHD stays attached.
  -Action Attach / Detach: attaches or detaches the VHD. -Action Dirty: sets the NTFS dirty flag (fsutil dirty set).
  Attach and Create print JSON {"letter": "X", "disk": N} on the last line.
  The label HEXWIPE marks the only volumes the app's test command may fill (the OS method of the free-space wipe).
  Never run this on a desktop: it creates and attaches virtual disks.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [ValidateSet('Create', 'Attach', 'Detach', 'Dirty')] [string]$Action,
    [string]$KeepFile = '',
    [int]$SizeMB = 64
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This script attaches a virtual disk; run it only on GitHub-hosted CI runners (RUNNER_ENVIRONMENT=github-hosted).'
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are needed.' }

function Get-Letter([string]$imagePath) {
    $disk = Get-DiskImage -ImagePath $imagePath | Get-Disk
    $partition = $disk | Get-Partition | Where-Object { $_.Type -eq 'Basic' -or $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' } | Select-Object -First 1
    if (-not $partition.DriveLetter -or $partition.DriveLetter -eq [char]0) {
        $partition | Add-PartitionAccessPath -AssignDriveLetter | Out-Null
        $partition = $disk | Get-Partition -PartitionNumber $partition.PartitionNumber
    }

    return [pscustomobject]@{ letter = [string]$partition.DriveLetter; disk = $disk.Number }
}

function Write-Repeated([string]$file, [string]$unit, [int]$length) {
    $bytes = [byte[]]::new($length)
    $pattern = [System.Text.Encoding]::ASCII.GetBytes($unit)
    for ($i = 0; $i -lt $length; $i++) { $bytes[$i] = $pattern[$i % $pattern.Length] }
    # Write through to the disk: a file that is deleted (or truncated) before the lazy writer ran never puts its data on the disk,
    # and the tests need the old content to stay in the unallocated clusters and in the slack.
    $stream = [System.IO.File]::Open($file, 'Create', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

switch ($Action) {
    'Create' {
        if (Test-Path $Path) { Remove-Item $Path -Force }
        New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
        $script = Join-Path ([System.IO.Path]::GetTempPath()) ('hexeditor-wipevhd-' + [guid]::NewGuid().ToString('N') + '.txt')
        Set-Content -Path $script -Value "create vdisk file=`"$Path`" maximum=$SizeMB type=fixed" -Encoding ascii
        & diskpart.exe /s $script | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'diskpart could not create the VHD' }
        Remove-Item $script -Force

        $disk = Mount-DiskImage -ImagePath $Path -PassThru | Get-Disk
        $volume = $disk | Initialize-Disk -PartitionStyle GPT -PassThru |
            New-Partition -AssignDriveLetter -UseMaximumSize |
            Format-Volume -FileSystem NTFS -AllocationUnitSize 4096 -NewFileSystemLabel 'HEXWIPE' -Confirm:$false
        $root = "$($volume.DriveLetter):\"
        Copy-Item -Path $KeepFile -Destination (Join-Path $root 'keep.txt') -Force
        $slack = Join-Path $root 'slack.txt'
        Write-Repeated $slack 'SLACK-REMNANT' 4096
        $stream = [System.IO.File]::Open($slack, 'Open', 'ReadWrite')
        try { $stream.SetLength(100); $stream.Flush($true) } finally { $stream.Dispose() }
        [System.IO.File]::WriteAllBytes((Join-Path $root 'tiny.txt'), [System.Text.Encoding]::ASCII.GetBytes('tiny-file!'))
        $wipe = Join-Path $root 'wipe.bin'
        Write-Repeated $wipe 'WIPE-ME-0123456789' 1048576
        Remove-Item $wipe -Force
        Write-VolumeCache -DriveLetter $volume.DriveLetter
        Write-Output (ConvertTo-Json -Compress -InputObject (Get-Letter $Path))
    }
    'Attach' {
        Mount-DiskImage -ImagePath $Path | Out-Null
        Write-Output (ConvertTo-Json -Compress -InputObject (Get-Letter $Path))
    }
    'Detach' {
        Dismount-DiskImage -ImagePath $Path -ErrorAction SilentlyContinue | Out-Null
    }
    'Dirty' {
        $info = Get-Letter $Path
        & fsutil.exe dirty set "$($info.letter):" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'fsutil dirty set failed' }
    }
}
