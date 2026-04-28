@echo off
REM ============================================================================
REM  ThatsMyQuant — Paper Trade Launcher (DPAPI auto-injection)
REM
REM  PowerShell decrypts the active OKX credential set from
REM  exchange_credentials.dpapi and injects it into the worker process as
REM  environment variables, sidestepping the in-worker DPAPI flow that has
REM  been unreliable on this machine. Demo / Live slot is picked from
REM  exchange_settings.json's OKX.IsDemoMode flag.
REM ============================================================================

setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%"

REM --- Decrypt OKX creds via PowerShell, write to %TEMP%\tmq-creds.cmd ---
set "TMQ_CREDS_FILE=%TEMP%\tmq-creds.cmd"
if exist "%TMQ_CREDS_FILE%" del "%TMQ_CREDS_FILE%" >nul 2>&1

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Add-Type -AssemblyName System.Security; $s = Get-Content 'exchange_settings.json' -Raw | ConvertFrom-Json; $isDemo = ($s.OKX -and $s.OKX.IsDemoMode); $j = Get-Content 'exchange_credentials.dpapi' -Raw | ConvertFrom-Json; function D($b) { if (-not $b) { return '' }; try { $by=[Convert]::FromBase64String($b); $p=[System.Security.Cryptography.ProtectedData]::Unprotect($by,$null,[System.Security.Cryptography.DataProtectionScope]::CurrentUser); return [Text.Encoding]::UTF8.GetString($p) } catch { return '' } }; $ak = if ($isDemo -and $j.DemoApiKey) { D $j.DemoApiKey } else { D $j.ApiKey }; $sk = if ($isDemo -and $j.DemoSecretKey) { D $j.DemoSecretKey } else { D $j.SecretKey }; $pp = if ($isDemo -and $j.DemoPassphrase) { D $j.DemoPassphrase } else { D $j.Passphrase }; $tg = if (Test-Path 'telegram_settings.json') { Get-Content 'telegram_settings.json' -Raw | ConvertFrom-Json } else { $null }; $tgTok = if ($tg) { $tg.BotToken } else { '' }; $tgChat = if ($tg) { [string]$tg.ChatId } else { '' }; $tgEnable = if ($tg) { [string]$tg.EnableReadBot } else { 'false' }; $lines = @(); if ($ak) { $lines += \"set `\"OKX__ApiKey=$ak`\"\" }; if ($sk) { $lines += \"set `\"OKX__SecretKey=$sk`\"\" }; if ($pp) { $lines += \"set `\"OKX__Passphrase=$pp`\"\" }; $lines += \"set `\"OKX__IsDemoMode=$($isDemo.ToString().ToLower())`\"\"; if ($tgTok) { $lines += \"set `\"Telegram__BotToken=$tgTok`\"\" }; if ($tgChat) { $lines += \"set `\"Telegram__ChatId=$tgChat`\"\" }; $lines += \"set `\"Telegram__EnableReadBot=$($tgEnable.ToLower())`\"\"; Set-Content -LiteralPath $env:TMQ_CREDS_FILE -Value $lines -Encoding ASCII; Write-Host (\"[DPAPI] OKX: ApiKey={0}c Secret={1}c Pass={2}c (Demo={3}) | Telegram: Token={4}c ChatId={5}\" -f $ak.Length, $sk.Length, $pp.Length, $isDemo, $tgTok.Length, $tgChat)"

if exist "%TMQ_CREDS_FILE%" call "%TMQ_CREDS_FILE%"
if exist "%TMQ_CREDS_FILE%" del "%TMQ_CREDS_FILE%" >nul 2>&1

set "DOTNET_ENVIRONMENT=Paper"
set "ASPNETCORE_ENVIRONMENT=Paper"
set "TMQ_MODE=paper"
set "TMQ_EXCHANGE_MODE=OKX"
set "TMQ_ALLOW_LIVE_TRADING=false"

echo.
echo === ThatsMyQuant Paper Trade ==================================
echo   Environment     : %DOTNET_ENVIRONMENT%
echo   Mode            : %TMQ_MODE%
echo   Exchange        : %TMQ_EXCHANGE_MODE% (Demo=%OKX__IsDemoMode%)
echo   OKX ApiKey      : %OKX__ApiKey:~0,4%...%OKX__ApiKey:~-4% (length envvar)
echo   Logs            : %%LOCALAPPDATA%%\ThatsMyQuant\logs
echo ================================================================
echo.

echo [1/2] Building QuantEngine.Worker (Release)...
dotnet build 01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj -c Release --nologo -v minimal
if errorlevel 1 (
    echo.
    echo [FAILED] Build failed — see errors above.
    popd
    pause
    exit /b 1
)

echo.
echo [2/2] Launching paper-trade worker. Ctrl+C to stop (graceful flush).
echo.

dotnet run --no-build -c Release --project 01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj

popd
endlocal
