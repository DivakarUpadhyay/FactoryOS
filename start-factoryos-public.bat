@echo off
rem Divakar FactoryOS, public: API + worker on this PC, reachable from https://factoryos-alpha.vercel.app
rem through a Cloudflare tunnel. Close the API, worker and cloudflared windows to take it offline.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-factoryos-public.ps1"
pause
