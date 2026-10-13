<#
.SYNOPSIS
    Downloads the tools Tray Mixer's build needs into ./deps.

.DESCRIPTION
    - Inno Setup (compiler for the installer, from its NuGet package) -> deps/innosetup/
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is much faster without the progress bar

$root = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $root 'deps'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("traymixer-deps-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $deps, $tmp | Out-Null

try {
    $inno = Join-Path $deps 'innosetup'
    if ($Force -or -not (Test-Path (Join-Path $inno 'ISCC.exe'))) {
        $version = '6.7.3'
        $package = Join-Path $tmp 'innosetup.zip'
        Write-Host "Downloading Inno Setup $version..."
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$version/tools.innosetup.$version.nupkg" -OutFile $package
        $out = Join-Path $tmp 'innosetup'
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        tar -xf $package -C $out
        if ($LASTEXITCODE -ne 0) { throw 'Extracting Inno Setup failed.' }
        if (Test-Path $inno) { Remove-Item -Recurse -Force $inno }
        Copy-Item (Join-Path $out 'tools') $inno -Recurse
        Write-Host "Inno Setup -> $inno"
    } else {
        Write-Host 'Inno Setup already present (use -Force to update).'
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
