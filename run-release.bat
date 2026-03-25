@echo off
:: ─────────────────────────────────────────────────────────────────────────────
:: run-release.bat  —  Run the self-contained published win-x64 executable
::                     Build it first with: publish-x64.bat
:: ─────────────────────────────────────────────────────────────────────────────
setlocal
set EXE=%~dp0publish\win-x64\SEZ_AccesDB_Module.exe

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
echo ─────────────────────────────────────────────────────────────────────────────
"%EXE%"
endlocal
