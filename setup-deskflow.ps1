# SPDX-License-Identifier: GPL-2.0-only
param(
    [string]$Destination = (Join-Path $PSScriptRoot 'Deskflow'),
    [string]$MsiPath = ''
)

$ErrorActionPreference = 'Stop'
$version = '1.26.0'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$core = Join-Path $destinationPath 'deskflow-core.exe'
if (Test-Path -LiteralPath $core) {
    $installed = (Get-Item -LiteralPath $core).VersionInfo.ProductVersion
    if ($installed -ne "$version.0" -and $installed -ne $version) {
        throw "Deskflow $installed already exists at $destinationPath. Version $version is tested; use a new destination to keep your existing copy."
    }
    Write-Host "Deskflow $version is ready: $destinationPath"
    return
}

$cache = Join-Path $PSScriptRoot '.cache'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (-not $MsiPath) {
    $MsiPath = Join-Path $cache "deskflow-$version-win-x64.msi"
    if (-not (Test-Path -LiteralPath $MsiPath)) {
        $url = "https://github.com/deskflow/deskflow/releases/download/v$version/deskflow-$version-win-x64.msi"
        Write-Host "Downloading Deskflow from its official release: $url"
        Invoke-WebRequest -Uri $url -OutFile "$MsiPath.download" -UseBasicParsing
        Move-Item -LiteralPath "$MsiPath.download" -Destination $MsiPath
    }
}
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$extractDir = Join-Path $cache ('extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractDir | Out-Null
$logPath = Join-Path $extractDir 'extract.log'
# Administrative extraction only: does not install Deskflow or start its service.
$arguments = @('/a', ('"' + $MsiPath + '"'), '/qn', '/norestart', ('TARGETDIR="' + $extractDir + '"'), '/l*v', ('"' + $logPath + '"'))
$extractor = Start-Process -FilePath "$env:WINDIR\System32\msiexec.exe" -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($extractor.ExitCode -ne 0) { throw "Deskflow extraction failed ($($extractor.ExitCode)); see $logPath" }
$extracted = Join-Path $extractDir 'PFiles64\Deskflow'
if (-not (Test-Path -LiteralPath (Join-Path $extracted 'deskflow-core.exe'))) {
    throw "Deskflow core is missing from the extracted package: $extractDir"
}
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
Get-ChildItem -LiteralPath $extracted | Copy-Item -Destination $destinationPath -Recurse -Force
Write-Host "Deskflow $version is ready: $destinationPath"
Write-Host 'The official package licenses are preserved. Cached downloads/extraction are in .cache.'
