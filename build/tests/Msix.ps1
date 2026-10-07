# MSIX helpers for the distribution tests (CI runners only: this installs packages and trusts a certificate).
# The package is signed with a throwaway certificate made on the runner and trusted only there, like the
# release workflow's verify job (PKG-24 step 7). The certificate and the signed packages are never published.
# Dot-source after TestCase.ps1:  . "$PSScriptRoot/Msix.ps1"

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# The scripts that use this file run in Windows PowerShell (shell: powershell in the workflows): the Appx and
# International modules are not fully supported in PowerShell 7. In PowerShell 7, Appx is loaded through Windows PowerShell.
if ($PSVersionTable.PSEdition -eq 'Core') { Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue }

$script:TestCertificate = $null

# A tool of the Windows SDK (signtool.exe, makepri.exe): the newest version, for this machine's architecture
# (x64 tools run under emulation on ARM64 when there is no arm64 build).
function Find-SdkTool([Parameter(Mandatory)][string]$Name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $preferred = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { @('arm64', 'x64') } else { @('x64') }
    foreach ($arch in $preferred) {
        $tool = Get-ChildItem $kits -Recurse -Filter $Name -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq $arch } | Sort-Object { $_.Directory.Parent.Name } | Select-Object -Last 1
        if ($tool) { return $tool.FullName }
    }
    throw "$Name was not found in $kits (Windows SDK)."
}

# AppxManifest.xml of an .msix as XML.
function Get-MsixManifest([Parameter(Mandatory)][string]$Path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $reader = New-Object System.IO.StreamReader($zip.GetEntry('AppxManifest.xml').Open())
        try { [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
}

# Signs a copy of the package with the throwaway certificate and returns the path of the copy.
function New-SignedTestMsix([Parameter(Mandatory)][string]$Path) {
    if (-not $env:GITHUB_ACTIONS) { throw 'This trusts a test certificate; run it only on CI runners.' }
    $publisher = (Get-MsixManifest $Path).Package.Identity.Publisher
    if (-not $script:TestCertificate) {
        $script:TestCertificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddDays(1)
        $pfx = Join-Path ([System.IO.Path]::GetTempPath()) 'hexeditor-test.pfx'
        $password = ConvertTo-SecureString -String ([guid]::NewGuid().ToString()) -Force -AsPlainText
        Export-PfxCertificate -Cert $script:TestCertificate -FilePath $pfx -Password $password | Out-Null
        Import-PfxCertificate -FilePath $pfx -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Password $password | Out-Null
        Remove-Item $pfx -Force
    }
    $signed = Join-Path ([System.IO.Path]::GetTempPath()) ("signed-" + [guid]::NewGuid().ToString('N') + '-' + (Split-Path -Leaf $Path))
    Copy-Item $Path $signed
    & (Find-SdkTool 'signtool.exe') sign /fd SHA256 /sha1 $script:TestCertificate.Thumbprint $signed | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "signing $signed failed" }
    $signed
}

# Installs the package (signed with the throwaway certificate) and returns the installed package.
function Install-TestMsix([Parameter(Mandatory)][string]$Path) {
    $signed = New-SignedTestMsix $Path
    try { Add-AppxPackage -Path $signed } finally { Remove-Item $signed -Force -ErrorAction SilentlyContinue }
    $package = Get-AppxPackage -Name HexEditor
    if (-not $package) { throw 'the MSIX package was not installed' }
    $package
}

function Uninstall-TestMsix {
    Get-Process HexEditor -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\WindowsApps\*' } | Stop-Process -Force
    foreach ($p in @(Get-AppxPackage -Name HexEditor)) { Remove-AppxPackage -Package $p.PackageFullName }
}

# The execution alias of the MSIX version (PKG-03): %LocalAppData%\Microsoft\WindowsApps\hexeditor.exe.
function Get-MsixAlias { Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\hexeditor.exe' }
