param(
    [ValidateSet("", "host", "client")]
    [string]$Role = "",
    [string]$Server = "",
    [string]$Token = "",
    [ValidateRange(0, 65535)]
    [int]$Port = 0
)

$ErrorActionPreference = "Stop"
$sources = Get-ChildItem (Join-Path $PSScriptRoot "src") -Filter '*.cs'

if ($PSVersionTable.PSEdition -ne "Core") {
    throw "run.ps1 需要 PowerShell 7 (pwsh)。构建正式 EXE 请使用 .NET 8 SDK。"
}

$refs = @((Join-Path $PSHOME "System.Private.CoreLib.dll")) + @(
    Get-ChildItem $PSHOME -Filter "*.dll" | ForEach-Object {
        try {
            [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null
            $_.FullName
        } catch { }
    }
) | Select-Object -Unique

if (-not ('KvmSwitch.Program' -as [type])) {
    Add-Type -Path $sources.FullName -ReferencedAssemblies $refs
}

$argsList = @()
if ($Role) { $argsList += "--$Role" }
if ($Server) { $argsList += @("--server", $Server) }
if ($Token) { $argsList += @("--token", $Token) }
if ($Port -gt 0) { $argsList += @("--port", $Port) }

[KvmSwitch.Program]::Main([string[]]$argsList)
