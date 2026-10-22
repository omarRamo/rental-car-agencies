<#
.SYNOPSIS
    Installs the platform locally, as close to production as a machine without Azure allows: Release build published to a
    folder, database created and migrated by the EF migrations bundle (as the deployment pipeline does), no migration at
    start-up.

.DESCRIPTION
    Steps: publish src/CarRental.Web (Release) to <DataRoot>\app, build the migrations bundle to <DataRoot>\efbundle.exe,
    run it against the SQL Server instance (it creates the database when missing). Demo data are loaded by the application
    itself at its first start (start.ps1). Re-run this script after pulling new code: it republishes and applies the new
    migrations, data are kept.

.PARAMETER SqlServer
    SQL Server instance, Windows authentication (default: .\SQLEXPRESS).

.PARAMETER Database
    Database name (default: CarRental).

.PARAMETER DataRoot
    Folder of the local installation: application, migrations bundle, files, e-mails and logs (default: a folder
    "carrental-local" next to the repository, on the same drive).
#>
[CmdletBinding()]
param(
    [string] $SqlServer = '.\SQLEXPRESS',
    [string] $Database = 'CarRental',
    [string] $DataRoot
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $DataRoot) {
    $DataRoot = Join-Path (Split-Path $repository -Parent) 'carrental-local'
}

$connection = "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Encrypt=True"
foreach ($folder in @('app', 'azurite', 'mail', 'logs')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $DataRoot $folder) | Out-Null
}

Write-Host "1/3 Publishing the application (Release) to $DataRoot\app"
& dotnet publish (Join-Path $repository 'src\CarRental.Web') --configuration Release --output (Join-Path $DataRoot 'app') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host '2/3 Building the EF migrations bundle'
Push-Location $repository
try {
    & dotnet tool restore | Out-Null
    & dotnet ef migrations bundle --project src\CarRental.Infrastructure --startup-project src\CarRental.Web `
        --configuration Release --output (Join-Path $DataRoot 'efbundle.exe') --force
    if ($LASTEXITCODE -ne 0) { throw 'dotnet ef migrations bundle failed.' }
}
finally {
    Pop-Location
}

Write-Host "3/3 Applying the migrations to $SqlServer / $Database (the database is created when missing)"
& (Join-Path $DataRoot 'efbundle.exe') --connection $connection
if ($LASTEXITCODE -ne 0) { throw 'The migrations bundle failed.' }

Write-Host ''
Write-Host "Installed in $DataRoot. Start the platform with deploy\local\start.ps1."
