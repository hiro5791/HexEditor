<#
.SYNOPSIS
  Creates and attaches the virtual disks used by the manual tests (TD-VHDX-MBR, TD-VHDX-GPT, TD-VHDX-CLONE).

.DESCRIPTION
  For the person doing the manual test pass (docs/test/manual-test-guide.md). Run it in a PowerShell window
  started with "Run as administrator". It only creates new .vhdx files in the given folder and attaches them;
  it does not change any Windows setting (UAC stays as it is, unlike build/tests/Mount-TestVhd.ps1 for CI).

  - TD-VHDX-MBR  : 64 MiB, MBR, one NTFS partition with a drive letter (label HEXVHD)
  - TD-VHDX-GPT  : 64 MiB, GPT, NTFS + FAT32 partitions without drive letters
  - TD-VHDX-CLONE: 512 MiB, empty (target for clone / restore tests)

  Run again with -Dismount to detach the disks and delete the three files.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\tests\New-ManualTestVhd.ps1
  powershell -ExecutionPolicy Bypass -File build\tests\New-ManualTestVhd.ps1 -Dismount
#>
param(
    [string]$Folder = (Join-Path $env:USERPROFILE 'HexEditorManualTestData'),
    [switch]$Dismount
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Start PowerShell with "Run as administrator" and run this script again.'
}

$mbrPath = Join-Path $Folder 'TD-VHDX-MBR.vhdx'
$gptPath = Join-Path $Folder 'TD-VHDX-GPT.vhdx'
$clonePath = Join-Path $Folder 'TD-VHDX-CLONE.vhdx'

if ($Dismount) {
    foreach ($image in @($mbrPath, $gptPath, $clonePath)) {
        if (Test-Path $image) {
            Dismount-DiskImage -ImagePath $image -ErrorAction SilentlyContinue | Out-Null
            Remove-Item $image -Force
            Write-Host "Removed $image"
        }
    }
    return
}

foreach ($image in @($mbrPath, $gptPath, $clonePath)) {
    if (Test-Path $image) {
        throw "$image already exists. Run with -Dismount first to remove the old test disks."
    }
}
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

Write-Host ''
Write-Host 'Created and attached the test disks (shown as "Msft Virtual Disk" in HexEditor):'
Write-Host ("  TD-VHDX-MBR  : disk {0} (64 MiB, MBR), volume {1}: (HEXVHD)" -f $mbr.Number, $volume.DriveLetter)
Write-Host ("  TD-VHDX-GPT  : disk {0} (64 MiB, GPT)" -f $gpt.Number)
Write-Host ("  TD-VHDX-CLONE: disk {0} (512 MiB, empty)" -f $clone.Number)
Write-Host ''
Write-Host 'When the manual tests are finished, run this script again with -Dismount.'
