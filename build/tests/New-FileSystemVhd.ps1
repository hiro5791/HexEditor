<#
.SYNOPSIS
  Creates a fixed VHD with one partition formatted by Windows (NTFS, FAT32 or exFAT), copies a folder of files onto it
  with the OS file APIs, records the OS view of the files, and detaches the VHD (CI runners only: needs administrator rights).

.DESCRIPTION
  Used by tests/HexEditor.UITests/PrivilegedFileSystemTests.cs (TC-FOR-09-01, TC-FOR-09-03, TC-FOR-10-05): the app then opens
  the detached VHD file as a disk image and its file-system parser must list the same files with the same sizes, modified
  times and SHA-256 as Windows. A fixed VHD is the raw disk followed by a 512-byte footer, so the app reads it directly.

  -Streams 'name:stream=text;...' adds alternate data streams (NTFS). -Compress 'name;...' sets the NTFS compression.
  The listing (JSON: path, size, modifiedUtcTicks, sha256) goes to -Listing.
  Never run this on a desktop: it creates and attaches a virtual disk.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Path,
    [Parameter(Mandatory)] [ValidateSet('NTFS', 'FAT32', 'exFAT')] [string]$FileSystem,
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Listing,
    [int]$SizeMB = 128,
    [string]$Streams = '',
    [string]$Compress = ''
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This script attaches a virtual disk; run it only on GitHub-hosted CI runners (RUNNER_ENVIRONMENT=github-hosted).'
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are needed.' }

if (Test-Path $Path) { Remove-Item $Path -Force }
New-Item -ItemType Directory -Force (Split-Path -Parent $Path) | Out-Null
$script = Join-Path ([System.IO.Path]::GetTempPath()) ('hexeditor-fsvhd-' + [guid]::NewGuid().ToString('N') + '.txt')
Set-Content -Path $script -Value "create vdisk file=`"$Path`" maximum=$SizeMB type=fixed" -Encoding ascii
& diskpart.exe /s $script | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'diskpart could not create the VHD' }
Remove-Item $script -Force

$disk = Mount-DiskImage -ImagePath $Path -PassThru | Get-Disk
try {
    $volume = $disk | Initialize-Disk -PartitionStyle MBR -PassThru |
        New-Partition -AssignDriveLetter -UseMaximumSize |
        Format-Volume -FileSystem $FileSystem -NewFileSystemLabel 'HEXFS' -Confirm:$false
    $root = "$($volume.DriveLetter):\"

    Copy-Item -Path (Join-Path $Source '*') -Destination $root -Recurse -Force
    foreach ($item in ($Streams -split ';' | Where-Object { $_ })) {
        $nameAndStream, $text = $item -split '=', 2
        $name, $stream = $nameAndStream -split ':', 2
        Set-Content -Path (Join-Path $root $name) -Stream $stream -Value $text -NoNewline -Encoding ascii
    }

    foreach ($name in ($Compress -split ';' | Where-Object { $_ })) {
        & compact.exe /c (Join-Path $root $name) | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "compact failed for $name" }
    }

    # Timestamps copied from the source (Copy-Item keeps the modified time).
    $rows = foreach ($file in Get-ChildItem -Path $root -Recurse -File -Force | Where-Object { $_.FullName -notmatch 'System Volume Information' }) {
        [pscustomobject]@{
            path             = $file.FullName.Substring($root.Length - 1)
            size             = $file.Length
            modifiedUtcTicks = $file.LastWriteTimeUtc.Ticks
            sha256           = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    ConvertTo-Json -InputObject @($rows) | Set-Content -Path $Listing -Encoding utf8
}
finally {
    Dismount-DiskImage -ImagePath $Path | Out-Null
}
