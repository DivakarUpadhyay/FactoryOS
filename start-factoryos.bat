@echo off
rem Divakar FactoryOS: starts the API (http://localhost:5080), the ingestion worker (reads machines and
rem the simulator) and the web app (http://localhost:3000) in three windows. Close those windows to stop them. Double-click this file to run it again.
cd /d "%~dp0"
start "FactoryOS API" cmd /k "cd /d %~dp0src\Divakar.FactoryOS.Api && dotnet run --launch-profile http"
start "FactoryOS Worker" cmd /k "cd /d %~dp0src\Divakar.FactoryOS.Worker && timeout /t 12 /nobreak >nul && dotnet run"
start "FactoryOS Web" cmd /k "cd /d %~dp0web && npm run dev"
echo Starting... the web app opens at http://localhost:3000 in about 20 seconds.
timeout /t 20 /nobreak >nul
start "" http://localhost:3000/setup
