@echo off
chcp 65001 >nul
:: -------------------------------------------------------------------------------
:: build.bat  -  Debug build (fast, with symbols, no optimisation)
:: -------------------------------------------------------------------------------
setlocal
set SLN=%~dp0..\SEZ_AccesDB_Module.sln

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
