@echo off
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found: %CSC%
  pause
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:RedmiHelper.exe /win32manifest:app.manifest /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll src\*.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED - copy the errors above and send them to Claude.
) else (
  echo.
  echo BUILD OK - RedmiHelper.exe is ready.
)
pause
