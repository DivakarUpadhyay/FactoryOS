<#
  Installs the published API and worker as Windows services that start with the machine (run as Administrator).
  The web app runs under Node; install it as a service with NSSM (https://nssm.cc) as shown at the end, or run it in IIS.
  Usage:  powershell -ExecutionPolicy Bypass -File deploy\install-windows-services.ps1 -ConnectionString "Server=.;Database=DivakarFactoryOS;..." -JwtKey "<48+ random characters>"
#>
param(
    [Parameter(Mandatory)] [string]$ConnectionString,
    [Parameter(Mandatory)] [string]$JwtKey,
    [string]$InstallDir = "C:\DivakarFactoryOS",
    [string]$WebOrigin = "http://localhost:3000"
)
$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script as Administrator."
}
if ($JwtKey.Length -lt 32) { throw "The JWT key must be at least 32 characters." }
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "publish"
if (-not (Test-Path "$src\api")) { throw "Run deploy\publish.ps1 first." }

Copy-Item $src\* $InstallDir -Recurse -Force

# machine-wide settings read by both services (FACTORYOS_ prefix; "__" separates sections)
[Environment]::SetEnvironmentVariable("FACTORYOS_ConnectionStrings__FactoryOS", $ConnectionString, "Machine")
[Environment]::SetEnvironmentVariable("FACTORYOS_Jwt__SigningKey", $JwtKey, "Machine")
[Environment]::SetEnvironmentVariable("FACTORYOS_Cors__Origins__0", $WebOrigin, "Machine")
[Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:5080", "Machine")

foreach ($svc in @(
    @{ Name = "DivakarFactoryOSApi";    Display = "Divakar FactoryOS API";    Exe = "$InstallDir\api\Divakar.FactoryOS.Api.exe" },
    @{ Name = "DivakarFactoryOSWorker"; Display = "Divakar FactoryOS Worker"; Exe = "$InstallDir\worker\Divakar.FactoryOS.Worker.exe" })) {
    if (Get-Service $svc.Name -ErrorAction SilentlyContinue) {
        Stop-Service $svc.Name -Force
        sc.exe delete $svc.Name | Out-Null
        Start-Sleep 2
    }
    New-Service -Name $svc.Name -DisplayName $svc.Display -BinaryPathName "`"$($svc.Exe)`"" -StartupType Automatic | Out-Null
    sc.exe failure $svc.Name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    Start-Service $svc.Name
    Write-Host "Started $($svc.Display)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Web app: install Node.js 20+, then (with NSSM):" -ForegroundColor Cyan
Write-Host "  nssm install DivakarFactoryOSWeb `"C:\Program Files\nodejs\node.exe`" `"$InstallDir\web\server.js`""
Write-Host "  nssm set DivakarFactoryOSWeb AppDirectory `"$InstallDir\web`""
Write-Host "  nssm set DivakarFactoryOSWeb AppEnvironmentExtra PORT=3000 HOSTNAME=0.0.0.0"
Write-Host "  nssm start DivakarFactoryOSWeb"
Write-Host "Then open $WebOrigin/setup on the server itself to create the platform operator."
