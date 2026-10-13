<#
.SYNOPSIS
    Builds a Tray Mixer release into .\publish: the installer (TrayMixer-<version>-setup.exe).
    The version comes from TrayMixer.csproj.

.DESCRIPTION
    The app targets .NET Framework 4.8, which ships with Windows 10 and 11, so the installer needs nothing else.
    Needs Inno Setup from scripts\get-deps.ps1 (run automatically if it's missing).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'TrayMixer.csproj'
$iscc = Join-Path $root 'deps\innosetup\ISCC.exe'

$version = (Select-Xml -Path $csproj -XPath '//Version').Node.InnerText
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "Unexpected version '$version' in TrayMixer.csproj" }
$numeric = "$version.0"

if (-not (Test-Path $iscc)) {
    & (Join-Path $PSScriptRoot 'get-deps.ps1')
}

$out = Join-Path $root 'publish'
$app = Join-Path $out 'TrayMixer'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }

Write-Host "Building Tray Mixer $version..."
dotnet publish $csproj -c Release -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if (-not (Test-Path (Join-Path $app 'TrayMixer.exe'))) { throw 'The build is missing TrayMixer.exe.' }

Write-Host 'Building the installer...'
& $iscc /Q "/DAppVersion=$version" "/DNumericVersion=$numeric" "/DSourceDir=$app" "/DOutputDir=$out" (Join-Path $root 'installer\TrayMixer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }

Get-ChildItem $out -File | ForEach-Object {
    '{0}  {1,8:N1} KB  sha256 {2}' -f $_.Name, ($_.Length / 1KB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
