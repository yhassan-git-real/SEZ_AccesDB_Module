@echo off
chcp 65001 >nul
:: -------------------------------------------------------------------------------
:: run-release.bat  -  Run the self-contained published win-x64 executable
::                     Build it first with: publish-x64.bat
:: -------------------------------------------------------------------------------
setlocal
set CONFIG=%~dp0config_path.json
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Get-Content '%CONFIG%' | ConvertFrom-Json).ExecutablePath"`) do set "EXE=%%i"

call "%~dp0scripts\check-prereqs.bat" || exit /b 1

if not exist "%EXE%" (
    echo.
    echo  [ERROR] Published executable not found:
    echo          %EXE%
    echo.
    echo  Run scripts\publish-x64.bat first to create it.
    exit /b 1
)

echo.
echo [RUN] Launching published release build...
echo -------------------------------------------------------------------------------
"%EXE%"
endlocal
