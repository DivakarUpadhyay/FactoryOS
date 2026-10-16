<#
  Builds a ready-to-copy Divakar FactoryOS release into .\publish:
    publish\api     .NET API (Divakar.FactoryOS.Api.exe)
    publish\worker  ingestion and operations worker (Divakar.FactoryOS.Worker.exe)
    publish\web     Next.js server (node server.js)
  The web build goes to web\.next-prod, so it never disturbs a running "npm run dev".
  Usage:  powershell -ExecutionPolicy Bypass -File deploy\publish.ps1 [-ApiUrl http://factory-server:5080]
#>
param([string]$ApiUrl = "http://localhost:5080")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "publish"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "1/3 API" -ForegroundColor Cyan
dotnet publish "$root\src\Divakar.FactoryOS.Api" -c Release -o "$out\api" --nologo -v q
if ($LASTEXITCODE) { throw "API publish failed" }

Write-Host "2/3 Worker" -ForegroundColor Cyan
dotnet publish "$root\src\Divakar.FactoryOS.Worker" -c Release -o "$out\worker" --nologo -v q
if ($LASTEXITCODE) { throw "Worker publish failed" }

Write-Host "3/3 Web (API at $ApiUrl)" -ForegroundColor Cyan
Push-Location "$root\web"
try {
    $env:FOS_DIST = ".next-prod"
    $env:NEXT_PUBLIC_API_URL = $ApiUrl
    npm run build
    if ($LASTEXITCODE) { throw "Web build failed" }
} finally {
    Remove-Item Env:FOS_DIST, Env:NEXT_PUBLIC_API_URL -ErrorAction SilentlyContinue
    Pop-Location
}
# the standalone server plus its static files and public folder
Copy-Item "$root\web\.next-prod\standalone" "$out\web" -Recurse
New-Item -ItemType Directory -Force "$out\web\.next-prod" | Out-Null
Copy-Item "$root\web\.next-prod\static" "$out\web\.next-prod\static" -Recurse
if (Test-Path "$root\web\public") { Copy-Item "$root\web\public" "$out\web\public" -Recurse }

Write-Host "Done: $out" -ForegroundColor Green
Write-Host "Next: set the connection string and JWT key (see docs\GUIDE.md, section 'Install on a factory server'), then run deploy\install-windows-services.ps1 as administrator."
