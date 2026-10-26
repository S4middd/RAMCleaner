@echo off
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo GAGAL: csc.exe .NET Framework tidak ditemukan di:
  echo   %CSC%
  echo Pastikan .NET Framework 4.x terpasang.
  pause
  exit /b 1
)

echo Mengompilasi RAMCleaner.exe ...
"%CSC%" -nologo -target:winexe -platform:anycpu -optimize+ -out:RAMCleaner.exe -win32manifest:app.manifest -win32icon:icon.ico -r:System.dll -r:System.Core.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll Program.cs

if errorlevel 1 (
  echo.
  echo BUILD GAGAL - lihat pesan error di atas.
  pause
  exit /b 1
)

echo.
echo BUILD SUKSES: RAMCleaner.exe
pause
