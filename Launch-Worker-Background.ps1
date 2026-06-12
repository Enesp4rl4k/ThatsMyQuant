$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot
Add-Type -AssemblyName System.Security

function D($b) {
    if (-not $b) { return '' }
    try {
        $by = [Convert]::FromBase64String($b)
        $p = [System.Security.Cryptography.ProtectedData]::Unprotect($by, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [Text.Encoding]::UTF8.GetString($p)
    } catch {
        Write-Host "[DPAPI] decrypt failed"
        return ''
    }
}

$s = Get-Content 'exchange_settings.json' -Raw | ConvertFrom-Json
$isDemo = ($s.OKX -and $s.OKX.IsDemoMode)
$j = Get-Content 'exchange_credentials.dpapi' -Raw | ConvertFrom-Json

$ak = if ($isDemo -and $j.DemoApiKey)      { D $j.DemoApiKey }      else { D $j.ApiKey }
$sk = if ($isDemo -and $j.DemoSecretKey)   { D $j.DemoSecretKey }   else { D $j.SecretKey }
$pp = if ($isDemo -and $j.DemoPassphrase)  { D $j.DemoPassphrase }  else { D $j.Passphrase }

Write-Host "[DPAPI] OKX Demo=$isDemo ApiKey=$($ak.Length) Secret=$($sk.Length) Pass=$($pp.Length)"

if ($ak.Length -eq 0) { Write-Host "[ABORT] No creds"; exit 1 }

$env:OKX__ApiKey = $ak
$env:OKX__SecretKey = $sk
$env:OKX__Passphrase = $pp
$env:OKX__IsDemoMode = $isDemo.ToString().ToLower()

if (Test-Path 'telegram_settings.json') {
    $tg = Get-Content 'telegram_settings.json' -Raw | ConvertFrom-Json
    if ($tg.BotToken) { $env:Telegram__BotToken = $tg.BotToken }
    if ($tg.ChatId)   { $env:Telegram__ChatId   = [string]$tg.ChatId }
    if ($tg.EnableReadBot) { $env:Telegram__EnableReadBot = ([string]$tg.EnableReadBot).ToLower() }
}

$env:DOTNET_ENVIRONMENT     = 'Paper'
$env:ASPNETCORE_ENVIRONMENT = 'Paper'
$env:TMQ_MODE               = 'paper'
$env:TMQ_EXCHANGE_MODE      = 'OKXSwap'
$env:TMQ_ALLOW_LIVE_TRADING = 'false'

# Force-set strategy keys via env (.NET config double-underscore = ":") so they
# override regardless of which appsettings file the host picks. This makes the
# launch self-contained: if env loading falls back to Development, these still apply.
$env:Funding__Long__HybridMode  = 'live'
$env:Funding__Short__HybridMode = 'off'
$env:Funding__Long__AllocationCsv = 'LTCUSDT:0.17,DOGEUSDT:0.10'
$env:Funding__Long__MaxConcurrentPositions = '3'
$env:Funding__Long__TrailingStopPct = '0.005'
$env:Funding__Long__TrailingArmProfitPct = '0.005'
$env:Funding__EntryThresholdRate = '0.0001'
$env:Funding__ExitThresholdRate  = '0.00002'
$env:Funding__TakeProfitPct      = '0.02'
$env:Funding__StopLossPct        = '0.010'
$env:Funding__MaxHoldingBars     = '20'
$env:Funding__BaseSizeFraction   = '0.015'
$env:Funding__MaxSizeFraction    = '0.04'
$env:Funding__RoundTripCostRate  = '0.0028'
$env:Funding__Timeframe          = '1h'
$env:Funding__IntervalSeconds    = '1800'
$env:Funding__CandleLookback     = '300'
$env:Hybrid__CircuitBreakerDailyLossPct = '0.01'
$env:Tca__AssumedRoundTripFeeRate = '0.0028'
$env:Tca__AlertThresholdPercent = '20.0'
$env:OrderExecution__PostOnlyMode = 'true'
$env:OrderExecution__LimitOrderTimeoutSeconds = '20'
$env:OrderExecution__LimitFillPollIntervalMs = '1000'
# Track B activation 2026-05-09: maker routing on; was 0% maker fill due to default EnableMakerRouting=false
$env:OrderExecution__EnableMakerRouting = 'true'
$env:OrderExecution__MakerUrgency = '0.3'
$env:OrderExecution__MakerPatientTimeoutMs = '5000'
$env:OrderExecution__MakerUrgentTimeoutMs = '1000'

Write-Host "[BUILD]"
& dotnet build '01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj' -c Release --nologo -v minimal | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] build"; exit 1 }

Write-Host "[RUN]"
# 2026-06-12 FIX: exec the exe with CWD = worker PROJECT dir so ContentRootPath
# resolves appsettings.json + appsettings.Paper.json exactly like the
# `dotnet run --project` path does. Running from repo root made the host miss
# BOTH files silently -> worker booted with ZERO EdgeLab strategies and idled
# (log: 'no symbols discovered', 0/336 maker fills). Program.cs now also
# fail-fasts (exit 78) if the Paper overlay cannot be found.
Set-Location '01_Services\QuantEngine.Worker'
& 'bin\Release\net9.0\ThatsMyQuant.Bot.exe'
