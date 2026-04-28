Set-Location 'C:\Users\parlak\Downloads\ThatsMyQuant'
Add-Type -AssemblyName System.Security
function Decrypt-Field([string]$b64) {
    if ([string]::IsNullOrEmpty($b64)) { return '' }
    $bytes = [Convert]::FromBase64String($b64)
    $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($plain)
}
$exSettings = Get-Content 'exchange_settings.json' -Raw | ConvertFrom-Json
$isDemo = ($exSettings.OKX -and $exSettings.OKX.IsDemoMode) -eq $true
$creds = Get-Content 'exchange_credentials.dpapi' -Raw | ConvertFrom-Json
$apiKey     = if ($isDemo) { Decrypt-Field $creds.DemoApiKey }     else { Decrypt-Field $creds.ApiKey }
$secretKey  = if ($isDemo) { Decrypt-Field $creds.DemoSecretKey }  else { Decrypt-Field $creds.SecretKey }
$passphrase = if ($isDemo) { Decrypt-Field $creds.DemoPassphrase } else { Decrypt-Field $creds.Passphrase }
$tg = if (Test-Path 'telegram_settings.json') { Get-Content 'telegram_settings.json' -Raw | ConvertFrom-Json } else { $null }
$env:OKX__ApiKey      = $apiKey
$env:OKX__SecretKey   = $secretKey
$env:OKX__Passphrase  = $passphrase
$env:OKX__IsDemoMode  = $isDemo.ToString().ToLower()
$env:Telegram__BotToken      = if ($tg) { [string]$tg.BotToken } else { '' }
$env:Telegram__ChatId        = if ($tg) { [string]$tg.ChatId } else { '' }
$env:Telegram__EnableReadBot = if ($tg) { [string]$tg.EnableReadBot.ToString().ToLower() } else { 'false' }
$env:DOTNET_ENVIRONMENT      = 'Paper'
$env:ASPNETCORE_ENVIRONMENT  = 'Paper'
$env:TMQ_EXCHANGE_MODE       = 'OKXSwap'
$env:TMQ_ALLOW_LIVE_TRADING  = 'false'
Write-Host "[CRED] OKX key=$($apiKey.Length)c demo=$isDemo | starting Listener.Api on :5000"
$exe = 'C:\Users\parlak\Downloads\ThatsMyQuant\01_Services\Listener.Api\bin\Debug\net9.0\Listener.Api.exe'
$logOut = 'C:\Users\parlak\Downloads\ThatsMyQuant\_runtime_logs\listener-api-stdout.log'
$logErr = 'C:\Users\parlak\Downloads\ThatsMyQuant\_runtime_logs\listener-api-stderr.log'
$proc = Start-Process -FilePath $exe -ArgumentList '--urls','http://localhost:5000' `
    -WorkingDirectory 'C:\Users\parlak\Downloads\ThatsMyQuant\01_Services\Listener.Api' `
    -RedirectStandardOutput $logOut -RedirectStandardError $logErr `
    -WindowStyle Hidden -PassThru
Write-Host "[STARTED] Listener.Api PID=$($proc.Id) logs=$logOut"
