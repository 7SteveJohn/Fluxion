@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

echo ============================================================
echo   GameBoost-DLSSG  -  build Windows installer (Inno Setup)
echo ============================================================
echo.

if not exist "GameBoost-DLSSG.exe" (
    echo [!] GameBoost-DLSSG.exe not found in this folder.
    echo     Run rebuild.bat first, then run this script again.
    echo.
    pause
    exit /b 1
)
if not exist "icon\icon.ico" (
    echo [!] icon\icon.ico is missing.
    echo.
    pause
    exit /b 1
)
if not exist "installer\GameBoost-DLSSG.iss" (
    echo [!] installer\GameBoost-DLSSG.iss is missing.
    echo.
    pause
    exit /b 1
)

set "ISCC="
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do set "ISCC=%%I"

if not defined ISCC (
    echo [!] Inno Setup 6 was not found on this machine.
    echo.
    echo     Install it, then run this script again:
    echo       winget install --id JRSoftware.InnoSetup -e
    echo       https://jrsoftware.org/isdl.php
    echo.
    set /p ANS="Try installing Inno Setup with winget now? [y/N] "
    if /i "!ANS!"=="y" (
        winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
        if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
        if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
        if not defined ISCC if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
    )
)

if not defined ISCC (
    echo.
    echo [!] Still no ISCC.exe. Aborting.
    echo.
    pause
    exit /b 1
)

REM --- pre-flight: validate the .iss before invoking ISCC (optional, needs Python) ---
set "PYEXE="
for /f "delims=" %%I in ('where python 2^>nul') do if not defined PYEXE set "PYEXE=%%I"

if defined PYEXE (
    "%PYEXE%" "%~dp0tools\iss_check.py" "%~dp0installer\GameBoost-DLSSG.iss" "%~dp0." >nul 2>&1
    if errorlevel 1 (
        echo [!] Installer script check FAILED. Details:
        echo.
        "%PYEXE%" "%~dp0tools\iss_check.py" "%~dp0installer\GameBoost-DLSSG.iss" "%~dp0."
        echo.
        pause
        exit /b 1
    )
    echo [*] Script check : PASS
) else (
    echo [i] Python not found - skipping the .iss static check.
)

echo [*] Compiler : !ISCC!
echo [*] Script   : installer\GameBoost-DLSSG.iss
echo [*] Building ...
echo.

"!ISCC!" /Qp "installer\GameBoost-DLSSG.iss"
set "RC=%ERRORLEVEL%"
echo.

if not "%RC%"=="0" (
    echo [!] Installer build FAILED ^(exit code %RC%^).
    echo     Re-run without /Qp to see the full log:
    echo       "!ISCC!" "installer\GameBoost-DLSSG.iss"
    echo.
    pause
    exit /b %RC%
)

echo [OK] Installer built successfully:
echo.
if exist "installer_out\*.exe" (
    for %%F in ("installer_out\*.exe") do echo      %%~nxF   %%~zF bytes
) else (
    echo      ^(no exe produced - check output above^)
)
echo.
echo      Folder: %~dp0installer_out
echo.
start "" "%~dp0installer_out"
pause
