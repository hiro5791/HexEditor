<#
.SYNOPSIS
  Creates and attaches the virtual disks used by the manual tests (TD-VHDX-MBR, TD-VHDX-GPT, TD-VHDX-CLONE).

.DESCRIPTION
  For the person doing the manual test pass. The manual test tool (tools/ManualTestRunner) runs it elevated when the
  user presses its create-virtual-disk button (Windows shows the UAC prompt), and reads the log file (-LogPath) to show
  the disk numbers. It can also be run in a PowerShell window started with "Run as administrator".
  It only creates new .vhdx files in the given folder and attaches them; it does not change any Windows setting
  (UAC stays as it is, unlike build/tests/Mount-TestVhd.ps1 for CI).

  - TD-VHDX-MBR  : 64 MiB, MBR, one NTFS partition with a drive letter (label HEXVHD)
  - TD-VHDX-GPT  : 64 MiB, GPT, NTFS + FAT32 partitions without drive letters
  - TD-VHDX-CLONE: 512 MiB, empty (target for clone / restore tests)

  When the files already exist (for example after a restart, which detaches them), the script attaches them again
  and reports the disk numbers. Run with -Dismount to detach the disks and delete the three files.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\tests\New-ManualTestVhd.ps1
  powershell -ExecutionPolicy Bypass -File build\tests\New-ManualTestVhd.ps1 -Dismount
#>
param(
    [string]$Folder = (Join-Path $env:USERPROFILE 'HexEditorManualTestData'),
    [string]$LogPath,
    [switch]$Dismount
)

$ErrorActionPreference = 'Stop'
if ($LogPath) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $LogPath) | Out-Null
    Set-Content -Path $LogPath -Value '' -Encoding utf8
}

function Log([string]$Text) {
    Write-Host $Text
    if ($LogPath) { Add-Content -Path $LogPath -Value $Text -Encoding utf8 }
}

function Disk-Of([string]$Image) {
    $attached = Get-DiskImage -ImagePath $Image
    if (-not $attached.Attached) { Mount-DiskImage -ImagePath $Image | Out-Null }
    return (Get-DiskImage -ImagePath $Image | Get-Disk)
}

try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Start PowerShell with "Run as administrator" and run this script again.'
    }

    $mbrPath = Join-Path $Folder 'TD-VHDX-MBR.vhdx'
    $gptPath = Join-Path $Folder 'TD-VHDX-GPT.vhdx'
    $clonePath = Join-Path $Folder 'TD-VHDX-CLONE.vhdx'
    $all = @($mbrPath, $gptPath, $clonePath)

    if ($Dismount) {
        foreach ($image in $all) {
            if (Test-Path $image) {
                Dismount-DiskImage -ImagePath $image -ErrorAction SilentlyContinue | Out-Null
                Remove-Item $image -Force
                Log "Removed $image"
            }
        }
        Log 'RESULT: removed'
        return
    }

    $existing = @($all | Where-Object { Test-Path $_ })
    if ($existing.Count -gt 0 -and $existing.Count -lt $all.Count) {
        # Some files are missing (an earlier run stopped half way): start again from nothing.
        foreach ($image in $existing) {
            Dismount-DiskImage -ImagePath $image -ErrorAction SilentlyContinue | Out-Null
            Remove-Item $image -Force
        }
        $existing = @()
    }

    if ($existing.Count -eq $all.Count) {
        # Already created: attach them again (a restart detaches them).
        $mbr = Disk-Of $mbrPath
        $gpt = Disk-Of $gptPath
        $clone = Disk-Of $clonePath
        $letter = (Get-Partition -DiskNumber $mbr.Number | Get-Volume | Where-Object DriveLetter | Select-Object -First 1).DriveLetter
    }
    else {
        New-Item -ItemType Directory -Force $Folder | Out-Null

        function New-TestVhd([string]$File, [int]$MiB) {
            $commands = Join-Path ([System.IO.Path]::GetTempPath()) 'hexeditor-manual-vhd.txt'
            Set-Content -Path $commands -Value "create vdisk file=`"$File`" maximum=$MiB type=expandable" -Encoding ascii
            & diskpart.exe /s $commands | Out-Null
            $code = $LASTEXITCODE
            Remove-Item $commands -Force
            if ($code -ne 0) { throw "diskpart could not create $File" }
            return (Mount-DiskImage -ImagePath $File -PassThru | Get-Disk)
        }

        # TD-VHDX-MBR: MBR with one NTFS partition that has a drive letter.
        $mbr = New-TestVhd $mbrPath 64
        $volume = $mbr | Initialize-Disk -PartitionStyle MBR -PassThru |
            New-Partition -AssignDriveLetter -UseMaximumSize |
            Format-Volume -FileSystem NTFS -NewFileSystemLabel 'HEXVHD' -Confirm:$false
        $letter = $volume.DriveLetter

        # TD-VHDX-GPT: GPT with NTFS + FAT32 partitions, no drive letters.
        $gpt = New-TestVhd $gptPath 64
        $gpt | Initialize-Disk -PartitionStyle GPT
        $ntfs = New-Partition -DiskNumber $gpt.Number -Size 24MB
        $ntfs | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'GPTNTFS' -Confirm:$false | Out-Null
        $fat = New-Partition -DiskNumber $gpt.Number -UseMaximumSize
        $fat | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'GPTFAT' -Confirm:$false | Out-Null
        foreach ($part in Get-Partition -DiskNumber $gpt.Number) {
            foreach ($access in @($part.AccessPaths | Where-Object { $_ -match '^[A-Z]:\\$' })) {
                Remove-PartitionAccessPath -DiskNumber $gpt.Number -PartitionNumber $part.PartitionNumber -AccessPath $access
            }
        }

        # TD-VHDX-CLONE: empty target for clone and restore tests.
        $clone = New-TestVhd $clonePath 512
    }

    Log 'Attached the test disks (shown as "Msft Virtual Disk" in HexEditor):'
    Log ("DISK TD-VHDX-MBR {0} (64 MiB, MBR, volume {1}: HEXVHD)" -f $mbr.Number, $letter)
    Log ("DISK TD-VHDX-GPT {0} (64 MiB, GPT)" -f $gpt.Number)
    Log ("DISK TD-VHDX-CLONE {0} (512 MiB, empty)" -f $clone.Number)
    Log 'RESULT: attached'
    Log 'When the manual tests are finished, run this script again with -Dismount.'
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    exit 1
}
