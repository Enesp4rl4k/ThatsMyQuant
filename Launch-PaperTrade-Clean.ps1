# Clean paper-trade launcher — bypasses Launch-PaperTrade.bat to avoid the
# `|` parsing bug in the DPAPI Write-Host block. Same logic, no cmd.exe quirks.
[CmdletBinding()] param(
    [string]$WorkDir   = "C:\Users\parlak\Downloads\ThatsMyQuant",
    [string]$LogDir    = "C:\Users\parlak\Downloads\ThatsMyQuant\_runtime_logs",
    [switch]$SkipBuild
)

Set-Location -LiteralPath $WorkDir
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
$stdout = Join-Path $LogDir 'paper-stdout.log'
$stderr = Join-Path $LogDir 'paper-stderr.log'
foreach ($f in @($stdout, $stderr)) { if (Test-Path $f) { Remove-Item $f -Force } }

Add-Type -AssemblyName System.Security
function Decrypt-Field([string]$b64) {
    if ([string]::IsNullOrEmpty($b64)) { return '' }
    try {
        $bytes = [Convert]::FromBase64String($b64)
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [Text.Encoding]::UTF8.GetString($plain)
    } catch {
        Write-Warning "Decrypt failed: $_"
        return ''
    }
}

$exSettings = Get-Content 'exchange_settings.json' -Raw | ConvertFrom-Json
$isDemo = ($exSettings.OKX -and $exSettings.OKX.IsDemoMode) -eq $true
$creds = Get-Content 'exchange_credentials.dpapi' -Raw | ConvertFrom-Json

if ($isDemo) {
    $apiKey     = Decrypt-Field $creds.DemoApiKey
    $secretKey  = Decrypt-Field $creds.DemoSecretKey
    $passphrase = Decrypt-Field $creds.DemoPassphrase
} else {
    $apiKey     = Decrypt-Field $creds.ApiKey
    $secretKey  = Decrypt-Field $creds.SecretKey
    $passphrase = Decrypt-Field $creds.Passphrase
}

$tg = if (Test-Path 'telegram_settings.json') { Get-Content 'telegram_settings.json' -Raw | ConvertFrom-Json } else { $null }
$tgToken = if ($tg) { [string]$tg.BotToken } else { '' }
$tgChat  = if ($tg) { [string]$tg.ChatId } else { '' }
$tgRead  = if ($tg) { [string]$tg.EnableReadBot } else { 'false' }

$env:OKX__ApiKey      = $apiKey
$env:OKX__SecretKey   = $secretKey
$env:OKX__Passphrase  = $passphrase
$env:OKX__IsDemoMode  = $isDemo.ToString().ToLower()
$env:Telegram__BotToken      = $tgToken
$env:Telegram__ChatId        = $tgChat
$env:Telegram__EnableReadBot = $tgRead.ToLower()
$env:DOTNET_ENVIRONMENT      = 'Paper'
$env:ASPNETCORE_ENVIRONMENT  = 'Paper'
$env:TMQ_MODE                = 'paper'
$env:TMQ_EXCHANGE_MODE       = 'OKXSwap'
$env:TMQ_ALLOW_LIVE_TRADING  = 'false'

Write-Host ("[CRED] OKX ApiKey={0}c Secret={1}c Pass={2}c (Demo={3})" -f $apiKey.Length, $secretKey.Length, $passphrase.Length, $isDemo)
Write-Host ("[CRED] Telegram BotToken={0}c ChatId={1}" -f $tgToken.Length, $tgChat)

$proj = Join-Path $WorkDir '01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj'
if (-not $SkipBuild) {
    Write-Host '[BUILD] dotnet build (Release)'
    & dotnet build $proj -c Release --nologo -v minimal 2>&1 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

Write-Host '[RUN] dotnet run (Release, paper mode, --no-launch-profile to honour DOTNET_ENVIRONMENT=Paper)'
# CRITICAL: --no-launch-profile prevents Properties/launchSettings.json from overriding our
# DOTNET_ENVIRONMENT=Paper env var with its own DOTNET_ENVIRONMENT=Development. Without this
# flag, appsettings.Paper.json is silently never loaded and the worker runs on stale defaults.

# Spawn the live log viewer in its OWN visible window so the user can watch trades + decisions
# in real time. The viewer waits for the worker's CLEF log to appear before tailing.
$logViewerPath = Join-Path $WorkDir 'Show-PaperTradeLogs.ps1'
if (Test-Path $logViewerPath)
{
    try
    {
        Start-Process -FilePath 'powershell.exe' -ArgumentList @(
            '-NoExit',
            '-NoProfile',
            '-ExecutionPolicy','Bypass',
            '-File', $logViewerPath
        ) | Out-Null
        Write-Host '[LOG VIEWER] Spawned Show-PaperTradeLogs.ps1 in a separate window.'
    }
    catch
    {
        Write-Warning "Failed to spawn log viewer: $_"
    }
}

& dotnet run --no-build --no-launch-profile -c Release --project $proj 2>&1 | ForEach-Object { Write-Host $_ }
