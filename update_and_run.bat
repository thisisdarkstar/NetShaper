@echo off
setlocal
cd /d "%~dp0"

echo ===================================================
echo   NetShaper Updater & Runner
echo ===================================================

:: Try to stop running NetShaper instances
powershell -Command "Stop-Process -Name NetShaper -Force -ErrorAction SilentlyContinue" >nul 2>&1
timeout /t 1 /nobreak >nul

:: Update publish folder if unlocked
copy /y publish_live\* publish\ >nul 2>&1
if not exist publish\amd64 mkdir publish\amd64 >nul 2>&1
copy /y publish_live\amd64\* publish\amd64\ >nul 2>&1

:: Launch the updated binary from publish_live directly (always guaranteed fresh)
echo Starting NetShaper (Live Build)...
start "" "%~dp0publish_live\NetShaper.exe"
echo Done.
exit /b 0
