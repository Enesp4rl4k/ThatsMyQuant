@echo off
REM ============================================================
REM  ThatsMyQuant Dashboard Launcher (direct, Release build)
REM  Always rebuilds so latest source changes are reflected.
REM ============================================================
setlocal
set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%04_UI\ThatsMyQuant.Dashboard\bin\Release\net9.0-windows\ThatsMyQuant.Dashboard.exe"

echo [info] Building Dashboard Release (incremental)...
pushd "%SCRIPT_DIR%"
dotnet build 04_UI\ThatsMyQuant.Dashboard\ThatsMyQuant.Dashboard.csproj -c Release --nologo -v q
popd

if not exist "%EXE%" (
    echo [error] Build failed. Run 'dotnet build' manually to diagnose.
    pause
    exit /b 1
)

echo [launch] Starting ThatsMyQuant Dashboard...
start "" "%EXE%"
exit /b 0
