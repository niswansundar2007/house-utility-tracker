@echo off
REM Builds HouseUtility.exe using the C# compiler that ships with Windows (no installs needed)
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist dist mkdir dist
if not exist dist\public mkdir dist\public

"%CSC%" /nologo /target:exe /optimize+ /out:dist\HouseUtility.exe /r:System.Web.Extensions.dll src\Server.cs
if errorlevel 1 (
  echo Build FAILED
  exit /b 1
)
copy /y src\public\*.* dist\public\ >nul
echo.
echo Build OK: dist\HouseUtility.exe
echo Double-click dist\HouseUtility.exe to start the app.
