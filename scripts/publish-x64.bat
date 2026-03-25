@echo off
:: ─────────────────────────────────────────────────────────────────────────────
:: publish-x64.bat  —  Self-contained single-file publish for Windows x64
::
:: Output: ..\publish\win-x64\
::   - Single .exe with all dependencies bundled
::   - No .NET runtime required on the target machine
::   - Requires Microsoft Access Database Engine 2016 (64-bit) on target
:: ─────────────────────────────────────────────────────────────────────────────
setlocal
set PROJ=%~dp0..\src\SEZ_AccesDB_Module\SEZ_AccesDB_Module.csproj
set OUTPUT=%~dp0..\publish\win-x64

echo.
echo [PUBLISH] Self-contained ^| win-x64 ^| Release
echo [PUBLISH] Output: %OUTPUT%
echo ─────────────────────────────────────────────────────────────────────────────
call "%~dp0check-prereqs.bat" || exit /b 1
echo.
dotnet publish "%PROJ%" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%OUTPUT%" ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    --nologo

if %errorlevel% neq 0 (
    echo.
    echo [FAILED] Publish failed with errors.
    exit /b %errorlevel%
)

echo.
echo [OK] Published to: %OUTPUT%
echo.
dir /b "%OUTPUT%\*.exe" 2>nul
endlocal
