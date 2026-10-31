@echo off
setlocal
call :check %*
set "RESULT=%ERRORLEVEL%"
if /I not "%~1"=="--no-pause" pause
exit /b %RESULT%

:check
set "EXPECTED_ARCH=x64"
if exist "%~dp0architecture.txt" set /p EXPECTED_ARCH=<"%~dp0architecture.txt"
if /I "%EXPECTED_ARCH%"=="x64" if not "%PROCESSOR_ARCHITECTURE%"=="AMD64" if not "%PROCESSOR_ARCHITEW6432%"=="AMD64" goto wrong_arch
if /I not "%EXPECTED_ARCH%"=="x64" if /I not "%EXPECTED_ARCH%"=="x86" goto invalid_arch
set "DOTNET48_RELEASE="
for /f "tokens=3" %%V in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul') do set /a DOTNET48_RELEASE=%%V
if not defined DOTNET48_RELEASE goto missing_framework
if %DOTNET48_RELEASE% LSS 528040 goto missing_framework
ver
echo Package architecture: %EXPECTED_ARCH%
echo .NET Framework Release: %DOTNET48_RELEASE%
exit /b 0
:wrong_arch
echo This package requires Windows 7 SP1 x64. Download the x86 package for 32-bit Windows.
exit /b 4
:invalid_arch
echo The package architecture marker is invalid: %EXPECTED_ARCH%
exit /b 5
:missing_framework
echo Install Prerequisites\ndp48-x86-x64-allos-enu.exe, restart Windows and retry.
echo The installer is offline. Windows 7 SP1 and prerequisite OS updates are required.
exit /b 3
