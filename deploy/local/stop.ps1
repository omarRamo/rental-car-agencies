<#
.SYNOPSIS
    Stops the platform started by start.ps1: the application, then Azurite when start.ps1 started it.

.PARAMETER DataRoot
    Folder of the local installation (default: "carrental-local" next to the repository).
#>
[CmdletBinding()]
param(
    [string] $DataRoot
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $DataRoot) {
    $DataRoot = Join-Path (Split-Path $repository -Parent) 'carrental-local'
}

foreach ($name in @('app', 'azurite')) {
    $pidFile = Join-Path $DataRoot "$name.pid"
    if (-not (Test-Path $pidFile)) { continue }
    $id = [int](Get-Content $pidFile | Select-Object -First 1)
    $process = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($process) {
        Stop-Process -Id $id -Confirm:$false
        Write-Host "Stopped $name (process $id)."
    }

    Remove-Item $pidFile
}
