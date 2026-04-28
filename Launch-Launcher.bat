@echo off
REM ============================================================
REM  ThatsMyQuant Launcher (services + dashboard orchestrator)
REM  Pre-builds all service projects so dotnet run --no-build
REM  starts instantly without recompilation delays.
REM ============================================================
setlocal
set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%04_UI\ThatsMyQuant.Launcher\bin\Release\net9.0-windows\ThatsMyQuant.Launcher.exe"

pushd "%SCRIPT_DIR%"

echo [1/5] Building QuantEngine.Worker...
dotnet build 01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj -c Release --nologo -v q
if errorlevel 1 ( echo [WARN] QuantEngine.Worker build failed — services may not start. )

echo [2/5] Building Execution.Worker...
dotnet build 01_Services\Execution.Worker\Execution.Worker.csproj -c Release --nologo -v q
if errorlevel 1 ( echo [WARN] Execution.Worker build failed — services may not start. )

echo [3/5] Building Listener.Api...
dotnet build 01_Services\Listener.Api\Listener.Api.csproj -c Release --nologo -v q
if errorlevel 1 ( echo [WARN] Listener.Api build failed — services may not start. )

echo [4/5] Building Gateway.Api (if present)...
if exist "01_Services\Gateway.Api" (
    dotnet build 01_Services\Gateway.Api -c Release --nologo -v q
    if errorlevel 1 ( echo [WARN] Gateway.Api build failed. )
) else (
    echo [skip] Gateway.Api not found, skipping.
)

echo [5/6] Building Dashboard (Release)...
dotnet build 04_UI\ThatsMyQuant.Dashboard\ThatsMyQuant.Dashboard.csproj -c Release --nologo -v q
if errorlevel 1 ( echo [WARN] Dashboard build failed — Launcher will still open but Dashboard button may show stale UI. )

echo [6/6] Building and launching Launcher...
dotnet build 04_UI\ThatsMyQuant.Launcher\ThatsMyQuant.Launcher.csproj -c Release --nologo -v q

popd

if not exist "%EXE%" (
    echo [error] Launcher build failed. Run 'dotnet build' manually to diagnose.
    pause
    exit /b 1
)

start "" "%EXE%"
exit /b 0
