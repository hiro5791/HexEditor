<#
.SYNOPSIS
  Creates and attaches TD-VHDX-MBR for the privileged UI tests (CI runners only: needs administrator rights).

.DESCRIPTION
  TD-VHDX-MBR (docs/test/test-data.md): a 64 MiB expandable VHDX with an MBR and one NTFS partition that has a drive
  letter. diskpart creates the file (New-VHD needs the Hyper-V module, which the runners do not have); Mount-DiskImage
  attaches it. Writes HEXEDITOR_TEST_VHD_DISK (the physical disk number) and HEXEDITOR_TEST_VHD_VOLUME (the drive
  letter) to GITHUB_ENV for tests/HexEditor.UITests/PrivilegedTests.cs.

  It also sets UAC to "elevate without prompting" for administrators (test strategy 6.5, normal user on CI), so the
  elevated helper that the app starts with runas does not wait for a consent prompt. GitHub's Windows images already
  use this value; the script makes sure of it.

  -Dismount detaches and deletes the image (the end of the job; the runner is thrown away anyway).
  Never run this on a desktop: it attaches a disk and changes a UAC policy value.
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path $env:RUNNER_TEMP 'TD-VHDX-MBR.vhdx'),
    [switch]$Dismount
)

$ErrorActionPreference = 'Stop'
if (-not $env:GITHUB_ACTIONS) { throw 'This script attaches a virtual disk and changes UAC; run it only on CI runners.' }

if ($Dismount) {
    if (Test-Path $Path) {
        Dismount-DiskImage -ImagePath $Path -ErrorAction SilentlyContinue | Out-Null
        Remove-Item $Path -Force -ErrorAction SilentlyContinue
    }
    return
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are needed.' }

# UAC: elevate administrators without prompting (ConsentPromptBehaviorAdmin = 0).
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name ConsentPromptBehaviorAdmin -Value 0 -Type DWord
Write-Host "UAC: EnableLUA=$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA), ConsentPromptBehaviorAdmin=0"

if (Test-Path $Path) {
    Dismount-DiskImage -ImagePath $Path -ErrorAction SilentlyContinue | Out-Null
    Remove-Item $Path -Force
}
New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
$script = Join-Path ([System.IO.Path]::GetTempPath()) 'hexeditor-vhd.txt'
Set-Content -Path $script -Value "create vdisk file=`"$Path`" maximum=64 type=expandable" -Encoding ascii
& diskpart.exe /s $script | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'diskpart could not create the VHDX' }
Remove-Item $script -Force

$disk = Mount-DiskImage -ImagePath $Path -PassThru | Get-Disk
$volume = $disk | Initialize-Disk -PartitionStyle MBR -PassThru |
    New-Partition -AssignDriveLetter -UseMaximumSize |
    Format-Volume -FileSystem NTFS -NewFileSystemLabel 'HEXVHD' -Confirm:$false
$number = $disk.Number
$letter = $volume.DriveLetter
if (-not $letter) { throw 'The NTFS volume of TD-VHDX-MBR has no drive letter.' }

# The MBR signature (55 AA at 0x1FE) that the tests read.
$stream = [System.IO.File]::Open("\\.\PhysicalDrive$number", 'Open', 'Read', 'ReadWrite')
try {
    $sector = New-Object byte[] 512
    [void]$stream.Read($sector, 0, 512)
} finally { $stream.Dispose() }
if ($sector[0x1FE] -ne 0x55 -or $sector[0x1FF] -ne 0xAA) { throw 'TD-VHDX-MBR has no MBR signature.' }

Write-Host "TD-VHDX-MBR: \\.\PhysicalDrive$number, volume ${letter}: ($((Get-Disk -Number $number).FriendlyName))"
if ($env:GITHUB_ENV) {
    Add-Content -Path $env:GITHUB_ENV -Value "HEXEDITOR_TEST_VHD_DISK=$number" -Encoding utf8
    Add-Content -Path $env:GITHUB_ENV -Value "HEXEDITOR_TEST_VHD_VOLUME=$letter" -Encoding utf8
}
