@echo off
setlocal
echo [TokLens v2.1] Building portable single-file TokLens.exe (with embedded Linux VPS agent)...

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
set "SRC=%~dp0src"
set "VPS=%~dp0vps"
set "OUT=%~dp0TokLens.exe"

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /utf8output ^
  /win32icon:"%SRC%\TokLens.ico" ^
  /resource:"%VPS%\toklens_agent.py",TokLens.toklens_agent.py ^
  /r:"%WPF%\PresentationFramework.dll" ^
  /r:"%WPF%\PresentationCore.dll" ^
  /r:"%WPF%\WindowsBase.dll" ^
  /r:System.Xaml.dll ^
  /r:System.Web.Extensions.dll ^
  /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll ^
  /out:"%OUT%" ^
  "%SRC%\ModelsAndPricing.cs" ^
  "%SRC%\SyncService.cs" ^
  "%SRC%\TokenScanner.cs" ^
  "%SRC%\HtmlExporter.cs" ^
  "%SRC%\SyncWindow.cs" ^
  "%SRC%\MainWindow.cs"

set "BUILD_ERR=%ERRORLEVEL%"
if %BUILD_ERR% EQU 0 (
    echo [OK] Built successfully: %OUT%
) else (
    echo [ERROR] Build failed with exit code %BUILD_ERR%
)
if /I not "%~1"=="--nopause" if /I not "%CI%"=="true" pause
exit /b %BUILD_ERR%
