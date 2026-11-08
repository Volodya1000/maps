@echo off
setlocal
cd /d "%~dp0"
set "MONO_ENV_OPTIONS=--trace=N:Avalonia.Win32.WinRT"
echo Starting the Windows 7 test. Please wait...
echo Diagnostics are saved in test-log.txt.
Windows7MonoPrototype.exe >test-log.txt 2>&1
echo.
echo Program ended with exit code %errorlevel%.
echo If it did not open, send us test-log.txt and a photo of this window.
pause
