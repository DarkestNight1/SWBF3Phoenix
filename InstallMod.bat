@echo off
setlocal
title SWBF3 Phoenix - Install Mod

REM Double-click this file to install a SWBF2 mod (e.g. Battlefront 3 Legacy
REM 3.1) and point the Unity project at your game. Nothing else to do.
REM
REM   - Double-click            : finds your game and the mod download itself
REM   - Drag a mod folder onto  : installs that folder specifically
REM
REM Re-running is safe: components already installed are skipped, so it will
REM not re-copy gigabytes. Pass FORCE to reinstall them anyway.

echo.
echo  ============================================
echo   SWBF3 Phoenix - mod installer
echo  ============================================
echo.

set "EXTRA="

REM A dropped folder arrives as %1. "FORCE" is accepted as a bare keyword so
REM there is something to type when a reinstall is actually wanted.
if /I "%~1"=="FORCE" (
    set EXTRA=-Force
) else if not "%~1"=="" (
    set EXTRA=-ModPath "%~1"
)
if /I "%~2"=="FORCE" set EXTRA=%EXTRA% -Force

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\install_mod.ps1" -Auto %EXTRA%
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
    echo  ------------------------------------------------------------
    echo   Something went wrong ^(exit code %RC%^).
    echo.
    echo   Most likely one of:
    echo     - Battlefront II was not found. Install it, or run:
    echo       powershell -ExecutionPolicy Bypass -File "%~dp0Tools\install_mod.ps1" -GameDir "C:\path\to\game"
    echo     - The mod download was not found. Drag the extracted mod
    echo       folder onto this .bat file instead of double-clicking it.
    echo  ------------------------------------------------------------
) else (
    echo  Done. Open UnityProject in Unity, open Runtime/Scenes/PhxMainScene,
    echo  and press Play - no scene edits needed.
)

echo.
pause
endlocal
