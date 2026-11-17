@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0packaging\sync_version.ps1"
if errorlevel 1 exit /b 1
set /p TOKLENS_VERSION=<"%~dp0version.txt"
echo [TokLens v%TOKLENS_VERSION%] Building portable TokLens.exe...

set "FWDIR=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FWDIR%\csc.exe" (
    set "FWDIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
)
set "CSC=%FWDIR%\csc.exe"
set "WPF=%FWDIR%\WPF"
set "SRC=%~dp0src"
set "VPS=%~dp0vps"
set "OUT=%~dp0TokLens.exe"

if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4.x compiler not found: %CSC%
    exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /utf8output ^
  /win32icon:"%SRC%\TokLens.ico" ^
  /win32manifest:"%SRC%\app.manifest" ^
  /resource:"%VPS%\toklens_agent.py",TokLens.toklens_agent.py ^
  /resource:"%~dp0pricing\service_tier_prices.json",TokLens.service_tier_prices.json ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:System.Xaml.dll ^
  /r:System.Security.dll ^
  /r:System.Web.Extensions.dll ^
  /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll ^
  /out:"%OUT%" ^
  "%SRC%\*.cs"

set "BUILD_ERR=%ERRORLEVEL%"
if %BUILD_ERR% EQU 0 (
    echo [OK] Built successfully: %OUT%
) else (
    echo [ERROR] Build failed with exit code %BUILD_ERR%
)
if /I not "%~1"=="--nopause" if /I not "%CI%"=="true" pause
exit /b %BUILD_ERR%
