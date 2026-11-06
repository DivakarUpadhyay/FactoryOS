# Divakar FactoryOS, public: runs the API and the worker on this PC, opens a Cloudflare quick tunnel to the API,
# points the Vercel site (https://factoryos-alpha.vercel.app) at the tunnel and redeploys it.
# A quick tunnel gets a new address every time it starts, so the site is rebuilt on every run (about 1-2 minutes).
# The site works only while these windows stay open. Run it with start-factoryos-public.bat.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$site = "https://factoryos-alpha.vercel.app"
$vercel = "vercel@64.7.0"

$cf = (Get-Command cloudflared -ErrorAction SilentlyContinue).Source
if (-not $cf) {
    $cf = @("${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe", "$env:ProgramFiles\cloudflared\cloudflared.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $cf) { throw "cloudflared is not installed: winget install Cloudflare.cloudflared" }

function Wait-Url([string]$url, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        try { if ((Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

Write-Host "Starting the API and the worker..."
Start-Process cmd -ArgumentList "/k", "title FactoryOS API && set `"FACTORYOS_Security__WebBaseUrl=$site`" && cd /d `"$root\src\Divakar.FactoryOS.Api`" && dotnet run --launch-profile http"
Start-Process cmd -ArgumentList "/k", "title FactoryOS Worker && cd /d `"$root\src\Divakar.FactoryOS.Worker`" && timeout /t 12 /nobreak >nul && dotnet run"
if (-not (Wait-Url "http://localhost:5080/health" 180)) { throw "The API did not start on http://localhost:5080; see the API window." }

Write-Host "Opening the tunnel..."
$log = Join-Path $env:TEMP "factoryos-tunnel.log"
Remove-Item $log -ErrorAction SilentlyContinue
Start-Process $cf -ArgumentList "tunnel", "--no-autoupdate", "--url", "http://localhost:5080", "--logfile", "`"$log`""
$tunnel = $null
$until = (Get-Date).AddSeconds(60)
while (-not $tunnel -and (Get-Date) -lt $until) {
    Start-Sleep -Seconds 2
    if (Test-Path $log) {
        $m = Select-String -Path $log -Pattern "https://[a-z0-9-]+\.trycloudflare\.com" | Select-Object -First 1
        if ($m) { $tunnel = $m.Matches[0].Value }
    }
}
if (-not $tunnel) { throw "The tunnel did not report an address; see the cloudflared window." }
if (-not (Wait-Url "$tunnel/health" 90)) { throw "The tunnel $tunnel does not reach the API yet; see the cloudflared window." }
Write-Host "API is public at $tunnel"

Write-Host "Pointing the Vercel site at the tunnel and redeploying..."
# The Vercel CLI writes progress to stderr, which Windows PowerShell would treat as an error, so run it through cmd.
Push-Location "$root\web"
try {
    cmd /c "npx --yes $vercel env rm NEXT_PUBLIC_API_URL production --yes >nul 2>&1"
    cmd /c "echo $tunnel| npx --yes $vercel env add NEXT_PUBLIC_API_URL production 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Could not set NEXT_PUBLIC_API_URL on Vercel." }
    # The project builds from the GitHub repo (Root Directory "web"), so rebuild the current production deployment.
    cmd /c "npx --yes $vercel redeploy factoryos-alpha.vercel.app --target production 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "The Vercel deploy failed." }
} finally { Pop-Location }

Write-Host ""
Write-Host "Ready: $site/login  (keep the API, worker and cloudflared windows open)"
Start-Process "$site/login"
