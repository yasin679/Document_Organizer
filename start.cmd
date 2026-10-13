@echo off
setlocal
title Document Organizer
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 is not installed. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

echo.
echo  Document Organizer
echo  ------------------
echo  To set or reset the password for user "admin", type it now (at least 8 characters).
echo  Press Enter to keep the current password.
echo.
set "NEWPW="
set /p "NEWPW=  New admin password: "
if defined NEWPW set "DOCORG_ADMIN_PASSWORD=%NEWPW%"
set "NEWPW="
echo.

if exist "app\DocumentOrganizer.dll" (
  echo Starting on http://localhost:5080 ... keep this window open while you use the app.
  cd /d "%~dp0app"
  start "" http://localhost:5080
  dotnet DocumentOrganizer.dll
) else (
  echo Building and starting on http://localhost:5080 ... keep this window open while you use the app.
  cd /d "%~dp0src\DocumentOrganizer"
  start "" http://localhost:5080
  dotnet run -c Release
)
pause
