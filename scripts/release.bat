@echo off
chcp 65001 >nul
:: -------------------------------------------------------------------------------
:: release.bat  -  Release build (optimised, no debug symbols)
:: -------------------------------------------------------------------------------
setlocal
set CONFIG=%~dp0..\config_path.json
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Get-Content '%CONFIG%' | ConvertFrom-Json).SolutionPath"`) do set "SLN=%%i"

echo.
echo [BUILD] Configuration: Release
echo -------------------------------------------------------------------------------
dotnet build "%SLN%" --configuration Release --nologo
if %errorlevel% neq 0 (
    echo.
    echo [FAILED] Release build failed with errors.
    exit /b %errorlevel%
)
echo.
echo [OK] Release build succeeded.
endlocal
