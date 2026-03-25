@echo off
:: ─────────────────────────────────────────────────────────────────────────────
:: check-prereqs.bat  —  Verifies Microsoft Access Database Engine 2016 (64-bit)
::
:: Checks the Windows registry for DAO.DBEngine.120 (64-bit COM object).
:: If missing, shows the download link and exits with code 1.
::
:: Usage: call "%~dp0check-prereqs.bat" || exit /b 1
:: ─────────────────────────────────────────────────────────────────────────────
setlocal

:: Check 64-bit DAO COM registration
reg query "HKLM\SOFTWARE\Classes\DAO.DBEngine.120" >nul 2>&1
if %errorlevel% equ 0 goto :found

:: Double-check the ACE OLEDB provider as a fallback
reg query "HKLM\SOFTWARE\Classes\Microsoft.ACE.OLEDB.12.0" >nul 2>&1
if %errorlevel% equ 0 goto :found

:: ── Not found ────────────────────────────────────────────────────────────────
echo.
echo  ╔══════════════════════════════════════════════════════════════════════╗
echo  ║  PREREQUISITE MISSING                                               ║
echo  ╠══════════════════════════════════════════════════════════════════════╣
echo  ║  Microsoft Access Database Engine 2016 Redistributable (64-bit)    ║
echo  ║  is NOT installed on this machine.                                  ║
echo  ║                                                                     ║
echo  ║  This is required for DAO/ADOX COM (reading and writing .accdb).   ║
echo  ║                                                                     ║
echo  ║  Download:                                                          ║
echo  ║  https://www.microsoft.com/en-us/download/details.aspx?id=54920    ║
echo  ║                                                                     ║
echo  ║  Install the x64 version:  accessdatabaseengine_X64.exe            ║
echo  ╚══════════════════════════════════════════════════════════════════════╝
echo.
set /p OPEN="  Open download page in browser now? [Y/N]: "
if /i "%OPEN%"=="Y" (
    start "" "https://www.microsoft.com/en-us/download/details.aspx?id=54920"
)
echo.
echo  [ABORTED] Install the prerequisite and re-run the script.
endlocal
exit /b 1

:found
echo  [OK] Microsoft Access Database Engine 2016 (64-bit) is installed.
endlocal
exit /b 0
