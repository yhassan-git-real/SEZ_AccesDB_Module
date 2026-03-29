@echo off
chcp 65001 >nul
:: -------------------------------------------------------------------------------
:: build.bat  -  Debug build (fast, with symbols, no optimisation)
:: -------------------------------------------------------------------------------
setlocal
set CONFIG=%~dp0..\config_path.json
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Get-Content '%CONFIG%' | ConvertFrom-Json).SolutionPath"`) do set "SLN=%%i"

echo.
echo [BUILD] Configuration: Debug
echo -------------------------------------------------------------------------------
dotnet build "%SLN%" --configuration Debug --nologo
if %errorlevel% neq 0 (
    echo.
    echo [FAILED] Build failed with errors.
    exit /b %errorlevel%
)
echo.
echo [OK] Debug build succeeded.
endlocal
