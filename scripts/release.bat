@echo off
chcp 65001 >nul
:: -------------------------------------------------------------------------------
:: release.bat  -  Release build (optimised, no debug symbols)
:: -------------------------------------------------------------------------------
setlocal
set SLN=%~dp0..\SEZ_AccesDB_Module.sln

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
