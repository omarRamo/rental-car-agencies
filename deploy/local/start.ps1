<#
.SYNOPSIS
    Starts the locally installed platform (see install.ps1): Azurite for the files, then the published application on
    Kestrel, in the background. Stop it with stop.ps1.

.DESCRIPTION
    The application runs with the "Development" environment: the only one that accepts the local substitutes of the Azure
    and partner services (Azurite instead of Blob Storage, e-mails written as .eml files instead of Azure Communication
    Services, simulated Clic to Pay bank, simulated WhatsApp and AI model). Everything else is as in production: Release
    build, SQL Server, database migrated by the bundle and never at start-up. The demo data are loaded at the first start.

.PARAMETER SqlServer
    SQL Server instance, Windows authentication (default: .\SQLEXPRESS).

.PARAMETER Database
    Database name (default: CarRental).

.PARAMETER DataRoot
    Folder of the local installation (default: "carrental-local" next to the repository).
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

$app = Join-Path $DataRoot 'app'
$logs = Join-Path $DataRoot 'logs'
if (-not (Test-Path (Join-Path $app 'CarRental.Web.dll'))) {
    throw "No application in ${app}: run deploy\local\install.ps1 first."
}

function Test-Port([int] $Port) {
    return [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

if (Test-Port 7180) {
    throw 'Port 7180 is already used: the platform (or another application) is running. Stop it first (stop.ps1).'
}

# Files: Azurite (Azure Blob Storage emulator), data kept in <DataRoot>\azurite.
if (-not (Test-Port 10000)) {
    Write-Host 'Starting Azurite (files)...'
    $azuriteData = Join-Path $DataRoot 'azurite'
    $azuriteLog = Join-Path $logs 'azurite.log'
    Start-Process -FilePath 'cmd.exe' -WindowStyle Hidden -ArgumentList '/c', `
        "npx --yes azurite@3.37.0 --location `"$azuriteData`" --skipApiVersionCheck --silent --blobHost 127.0.0.1 --queueHost 127.0.0.1 --tableHost 127.0.0.1 > `"$azuriteLog`" 2>&1" | Out-Null
    for ($i = 0; $i -lt 60 -and -not (Test-Port 10000); $i++) { Start-Sleep -Seconds 1 }
    if (-not (Test-Port 10000)) { throw "Azurite did not start: see $azuriteLog." }
    (Get-NetTCPConnection -LocalPort 10000 -State Listen | Select-Object -First 1).OwningProcess | Set-Content (Join-Path $DataRoot 'azurite.pid')
}
else {
    Write-Host 'Azurite already listens on port 10000: reused.'
}

# The application: published Release build on Kestrel.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'https://localhost:7180;http://localhost:5180'
$env:ConnectionStrings__Sql = "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Encrypt=True"
$env:Database__ApplyMigrationsOnStartup = 'false'
$env:Storage__ConnectionString = 'UseDevelopmentStorage=true'
$env:Email__PickupDirectory = Join-Path $DataRoot 'mail'
$env:PublicSite__BaseUrl = 'https://localhost:7180'

Write-Host 'Starting the application...'
$process = Start-Process -FilePath 'dotnet' -ArgumentList "`"$(Join-Path $app 'CarRental.Web.dll')`"" -WorkingDirectory $app `
    -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs 'app.log') -RedirectStandardError (Join-Path $logs 'app-errors.log') -PassThru
$process.Id | Set-Content (Join-Path $DataRoot 'app.pid')

$ready = $false
for ($i = 0; $i -lt 180 -and -not $ready; $i++) {
    Start-Sleep -Seconds 2
    if ($process.HasExited) { throw "The application stopped: see $logs\app.log and app-errors.log." }
    $code = & curl.exe -sk -o NUL -w '%{http_code}' 'https://localhost:7180/health/ready'
    $ready = $code -eq '200'
}

if (-not $ready) { throw "The application did not become ready in 6 minutes: see $logs\app.log." }

Write-Host ''
Write-Host 'The platform is running:'
Write-Host '  Public site  https://localhost:7180/agence-demo-tunis/fr'
Write-Host '  Back office  https://localhost:7180/admin'
Write-Host '  Accounts     gerant@demo.local, agent@demo.local, comptable@demo.local'
Write-Host '               password and authenticator key: src\CarRental.Web\appsettings.Development.json (Seed section)'
Write-Host "  E-mails      $DataRoot\mail (.eml files)"
Write-Host "  Logs         $logs"
Write-Host 'Stop it with deploy\local\stop.ps1.'
