# Set-OkxDemoCredentials.ps1
# Saves OKX DEMO credentials without the Dashboard, in the exact format
# ExchangeSettingsViewModel.SaveCredentialsDpapi() writes:
#   exchange_settings.json      { ExchangeMode = OKXSwap, OKX.IsDemoMode = true }
#   exchange_credentials.dpapi  per-field DPAPI(CurrentUser) -> Base64
# Values are typed hidden and never echoed or written in plain text.
# Existing non-demo fields (live OKX / Binance) are preserved.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Set-Location $PSScriptRoot

function Read-Secret([string]$label) {
    $s = Read-Host -Prompt $label -AsSecureString
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

function Protect([string]$value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($value)
    $enc = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [Array]::Clear($bytes, 0, $bytes.Length)
    return [Convert]::ToBase64String($enc)
}

Write-Host "OKX DEMO credentials (Demo Trading -> API). Input is hidden." -ForegroundColor Cyan
$apiKey     = Read-Secret 'Demo API Key'
$secretKey  = Read-Secret 'Demo Secret Key'
$passphrase = Read-Secret 'Demo Passphrase'
if (-not $apiKey -or -not $secretKey -or -not $passphrase) { throw 'All three values are required.' }

$credPath = Join-Path $PSScriptRoot 'exchange_credentials.dpapi'
$existing = if (Test-Path $credPath) { Get-Content $credPath -Raw | ConvertFrom-Json } else { $null }
function Keep([string]$field) { if ($existing) { $existing.$field } else { $null } }

$creds = [ordered]@{
    BinanceApiKey    = Keep 'BinanceApiKey'
    BinanceApiSecret = Keep 'BinanceApiSecret'
    ApiKey           = Keep 'ApiKey'
    SecretKey        = Keep 'SecretKey'
    Passphrase       = Keep 'Passphrase'
    DemoApiKey       = Protect $apiKey
    DemoSecretKey    = Protect $secretKey
    DemoPassphrase   = Protect $passphrase
}
$apiKey = $secretKey = $passphrase = $null

$creds | ConvertTo-Json | Set-Content -Path $credPath -Encoding utf8
[ordered]@{ ExchangeMode = 'OKXSwap'; OKX = [ordered]@{ IsDemoMode = $true } } |
    ConvertTo-Json | Set-Content -Path (Join-Path $PSScriptRoot 'exchange_settings.json') -Encoding utf8

Write-Host "Saved exchange_credentials.dpapi + exchange_settings.json (demo mode)." -ForegroundColor Green
Write-Host "Next: .\Launch-PaperTrade-Clean.ps1 -SkipBuild"
