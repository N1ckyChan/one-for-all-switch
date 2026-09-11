param(
    [switch]$Publish,
    [switch]$WithDeskflow,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "KvmSwitch.csproj"
$bundled = Join-Path $PSScriptRoot ".dotnet-sdk\dotnet.exe"
$dotnetPath = if (Test-Path $bundled) {
    $bundled
} else {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $command.Source } else { $null }
}
if (-not $dotnetPath) { throw "Install the .NET 8 SDK before building." }

if ($Publish) {
    & $dotnetPath publish $project -c $Configuration -r win-x64 --self-contained true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

    $publishDir = Join-Path $PSScriptRoot "bin\$Configuration\net8.0-windows\win-x64\publish"
    $distDir = Join-Path $PSScriptRoot "dist"
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDir "one-for-all-switch.exe") -Destination $distDir -Force
    foreach ($name in @('README.md', 'screenshot.png', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'setup-deskflow.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $distDir -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $distDir -Recurse -Force
    if ($WithDeskflow) {
        & (Join-Path $PSScriptRoot 'setup-deskflow.ps1') -Destination (Join-Path $distDir 'Deskflow')
    }
    Write-Host "发布目录：$distDir"
} else {
    & $dotnetPath build $project -c $Configuration -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $LASTEXITCODE" }
}
