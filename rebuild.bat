@echo off
REM GameBoost-DLSSG rebuild + install
REM English-only on purpose: cmd mangles UTF-8 Chinese in .bat depending on codepage.
setlocal

set SRC=D:\youhua\GameBoost-DLSSG
set DST=D:\GameBoost-DLSSG

echo [1/4] Compiling (Core.cs + Dlssg.cs + Ui.cs) ...
taskkill /IM GameBoost-DLSSG.exe /F >nul 2>&1
REM Delete the old report first. A stale COMPILE_OK would make a failed build look
REM successful, because the previous exe is still on disk - testing for the exe
REM alone can never detect failure. Exit code + fresh report are what we trust.
del /q "%SRC%\compile_result.txt" >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%SRC%\compile.ps1"
if errorlevel 1 goto build_failed
findstr /b /c:"COMPILE_OK" "%SRC%\compile_result.txt" >nul 2>&1
if errorlevel 1 goto build_failed
echo OK
goto build_ok

:build_failed
echo.
echo BUILD FAILED - real compiler output below (also kept in compile_result.txt)
echo ------------------------------------------------------------
if exist "%SRC%\compile_result.txt" type "%SRC%\compile_result.txt"
if not exist "%SRC%\compile_result.txt" echo   (no report produced - script did not run?)
echo ------------------------------------------------------------
exit /b 1

:build_ok

echo [2/4] Stopping running instance (so the file is not locked) ...
taskkill /IM GameBoost-DLSSG.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

echo [3/4] Installing to %DST% ...
if not exist "%DST%" mkdir "%DST%"
copy /Y "%SRC%\GameBoost-DLSSG.exe" "%DST%\GameBoost-DLSSG.exe" >nul
if errorlevel 1 (
  echo COPY FAILED - close GameBoost-DLSSG from the tray and retry.
  exit /b 1
)
REM Installed copies (unins000.exe present) keep config/logs/backups in %ProgramData%.
REM Dropping a config.json next to the exe would flip the app into portable mode and
REM move all user data into the install folder - never do that for an installed copy.
if exist "%DST%\unins000.exe" (
  echo   config : skipped - installed copy keeps its data in %ProgramData%
) else (
  if not exist "%DST%\config.json" copy /Y "%SRC%\config.json" "%DST%\config.json" >nul
)
if not exist "%DST%\icon" mkdir "%DST%\icon"
copy /Y "%SRC%\icon\icon.ico" "%DST%\icon\icon.ico" >nul

echo [4/4] Done.
echo.
echo   exe    : %DST%\GameBoost-DLSSG.exe
echo   config : %DST%\config.json  (kept as-is if it already existed)
echo.
echo Run it (it will ask for admin on first launch). Enable auto-start from the UI if wanted.
endlocal
