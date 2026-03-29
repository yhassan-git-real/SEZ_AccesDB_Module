@echo off
chcp 65001 >nul
:: -------------------------------------------------------------------------------
:: run.bat  -  Build (Debug) and run the application
:: -------------------------------------------------------------------------------
setlocal
set CONFIG=%~dp0..\config_path.json
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Get-Content '%CONFIG%' | ConvertFrom-Json).ProjectPath"`) do set "PROJ=%%i"

echo.
echo [RUN] Starting SEZ AccesDB Module...
echo -------------------------------------------------------------------------------
call "%~dp0check-prereqs.bat" || exit /b 1
echo.
dotnet run --project "%PROJ%" --configuration Debug --nologo
if %errorlevel% neq 0 (
    echo.
    echo [FAILED] Application exited with error code %errorlevel%.
    exit /b %errorlevel%
)
endlocal
