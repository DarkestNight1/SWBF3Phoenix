@echo off
setlocal
title SWBF3 Phoenix - Build Self-Contained Player

REM Double-click to build Phoenix straight into your Battlefront II folder as a
REM self-contained install:
REM
REM   <BF2>\BattlefrontII.exe              the original game, untouched
REM   <BF2>\Phoenix\Phoenix.exe            Phoenix + BF3 Legacy
REM   <BF2>\Play Phoenix (BF3 Legacy).lnk  shortcut to it
REM
REM Nothing is written outside <BF2>\Phoenix - no registry, no AppData. Delete
REM that folder and the shortcut to uninstall.
REM
REM Requires the native libs to exist already: run BuildAndCopyLibsWin.bat once
REM first (and InstallMod.bat if you want the BF3 Legacy content).

echo.
echo  ============================================
echo   SWBF3 Phoenix - build self-contained player
echo  ============================================
echo.

if not exist "%~dp0UnityProject\Assets\Lib\LibSWBF2.dll" (
    echo  The native libraries are missing:
    echo    UnityProject\Assets\Lib\LibSWBF2.dll
    echo.
    echo  Run BuildAndCopyLibsWin.bat first, then try again.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\build_phoenix.ps1" %*
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
    echo  ------------------------------------------------------------
    echo   Build failed ^(exit code %RC%^). See the log path printed above.
    echo  ------------------------------------------------------------
)

echo.
pause
endlocal
