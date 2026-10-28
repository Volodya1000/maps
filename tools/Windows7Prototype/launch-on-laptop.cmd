@echo off
setlocal
set "PAYLOAD=%~dp0"
call "%PAYLOAD%check-prerequisites.cmd"
if errorlevel 1 goto failed
if not exist "%PAYLOAD%results" mkdir "%PAYLOAD%results"
call "%PAYLOAD%check-prerequisites.cmd" > "%PAYLOAD%results\preflight.txt" 2>&1
"%PAYLOAD%Windows7Prototype\Windows7Prototype.exe"
set "PROTOTYPE_EXIT=%ERRORLEVEL%"
echo Exit code: %PROTOTYPE_EXIT% >> "%PAYLOAD%results\preflight.txt"
if exist "%TEMP%\maps-win7-prototype-error.log" copy /y "%TEMP%\maps-win7-prototype-error.log" "%PAYLOAD%results\prototype-error.log" >nul
if not "%PROTOTYPE_EXIT%"=="0" goto failed
exit /b 0
:failed
echo Read README.txt. Send the results folder if the application does not start.
pause
exit /b 1
