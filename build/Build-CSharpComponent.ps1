<#
.SYNOPSIS
  Builds the C# script component (08 AUTO-03) zip and its verification manifest.

.DESCRIPTION
  Publishes src/HexEditor.Scripting.CSharp (Roslyn + the component entry assembly, without HexEditor.Sdk, which the app shares),
  zips it as hexeditor-csharp-<version>.zip and writes csharp-component.json (SHA-256 of the zip and of every file, and the list of
  Microsoft-signed Roslyn assemblies). Pass the manifest to the app build with -p:HexCSharpComponentManifest=<path> so the app
  embeds it (AUTO-03 spec 3). Upload the zip to the release of the same version (the app downloads
  <repository>/releases/download/v<version>/<zip>). The zip is also the offline package users can extract by hand into
  <data folder>\components\csharp\<version>\.
  Nothing is signed or installed by this script.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0-local',
    [string]$OutDir,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'artifacts/CSharpComponent' }
$publish = Join-Path $OutDir 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $publish | Out-Null

& dotnet publish (Join-Path $root 'src/HexEditor.Scripting.CSharp/HexEditor.Scripting.CSharp.csproj') -c $Configuration -o $publish -nologo "-p:HexVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# アプリと共有するもの (SDK) と、実行に要らないものは入れない。
Get-ChildItem $publish -Recurse -File | Where-Object { $_.Name -like 'HexEditor.Sdk.*' -or $_.Extension -in '.pdb', '.xml' } | Remove-Item -Force

$zip = Join-Path $OutDir "hexeditor-csharp-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip)

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 $Path).Hash.ToLowerInvariant() }

$files = [ordered]@{}
Get-ChildItem $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($publish.Length).TrimStart('\', '/').Replace('\', '/')
    $files[$relative] = Get-Sha256 $_.FullName
}

$manifest = [ordered]@{
    version         = $Version
    packageName     = Split-Path -Leaf $zip
    packageSha256   = Get-Sha256 $zip
    packageSize     = (Get-Item $zip).Length
    files           = $files
    microsoftSigned = @($files.Keys | Where-Object { $_ -like 'Microsoft.CodeAnalysis*.dll' })
}
$manifestPath = Join-Path $OutDir 'csharp-component.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $manifestPath
Write-Host "Component: $zip"
Write-Host "Manifest:  $manifestPath (build the app with -p:HexCSharpComponentManifest=$manifestPath)"
