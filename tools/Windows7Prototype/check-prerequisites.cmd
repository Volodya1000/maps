@echo off
setlocal
if not "%PROCESSOR_ARCHITECTURE%"=="AMD64" if not "%PROCESSOR_ARCHITEW6432%"=="AMD64" goto wrong_arch
set "DOTNET48_RELEASE="
for /f "tokens=3" %%V in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul') do set /a DOTNET48_RELEASE=%%V
if not defined DOTNET48_RELEASE goto missing_framework
if %DOTNET48_RELEASE% LSS 528040 goto missing_framework
ver
echo .NET Framework Release: %DOTNET48_RELEASE%
exit /b 0
:wrong_arch
echo This package requires Windows 7 SP1 x64. A 32-bit OS needs a different package.
exit /b 4
:missing_framework
echo Install Prerequisites\ndp48-x86-x64-allos-enu.exe, restart Windows and retry.
echo The installer is offline. Windows 7 SP1 and prerequisite OS updates are required.
exit /b 3
