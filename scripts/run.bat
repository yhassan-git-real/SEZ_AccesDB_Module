@echo off
:: ─────────────────────────────────────────────────────────────────────────────
:: run.bat  —  Build (Debug) and run the application
:: ─────────────────────────────────────────────────────────────────────────────
setlocal
set PROJ=%~dp0..\src\SEZ_AccesDB_Module\SEZ_AccesDB_Module.csproj

echo.
echo [RUN] Starting SEZ AccesDB Module...
echo ─────────────────────────────────────────────────────────────────────────────
call "%~dp0check-prereqs.bat" || exit /b 1
echo.
dotnet run --project "%PROJ%" --configuration Debug --nologo
if %errorlevel% neq 0 (
    echo.
    echo [FAILED] Application exited with error code %errorlevel%.
    exit /b %errorlevel%
)
endlocal
