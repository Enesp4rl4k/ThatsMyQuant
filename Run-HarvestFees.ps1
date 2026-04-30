# Decrypts OKX credentials from exchange_credentials.dpapi (DPAPI/CurrentUser scope),
# injects them as OKX__* env vars, and runs `harvest-fees` against the OKX demo
# account so we can replace the 28bp fee assumption with measured ground truth.
[CmdletBinding()] param(
    [string]$WorkDir = "C:\Users\parlak\Downloads\ThatsMyQuant",
    [string]$Since   = "2026-04-25T00:00:00Z",
    [string]$Until   = "2026-04-30T23:59:59Z",
    [string]$OutCsv  = ""
)

Set-Location -LiteralPath $WorkDir
Add-Type -AssemblyName System.Security
function Decrypt-Field([string]$b64) {
    if ([string]::IsNullOrEmpty($b64)) { return '' }
    try {
        $bytes = [Convert]::FromBase64String($b64)
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [Text.Encoding]::UTF8.GetString($plain)
    } catch { return '' }
}

$exSettings = Get-Content 'exchange_settings.json' -Raw | ConvertFrom-Json
$isDemo = ($exSettings.OKX -and $exSettings.OKX.IsDemoMode) -eq $true
$creds  = Get-Content 'exchange_credentials.dpapi' -Raw | ConvertFrom-Json

if ($isDemo) {
    $apiKey     = Decrypt-Field $creds.DemoApiKey
    $secretKey  = Decrypt-Field $creds.DemoSecretKey
    $passphrase = Decrypt-Field $creds.DemoPassphrase
} else {
    $apiKey     = Decrypt-Field $creds.ApiKey
    $secretKey  = Decrypt-Field $creds.SecretKey
    $passphrase = Decrypt-Field $creds.Passphrase
}

$env:OKX__ApiKey      = $apiKey
$env:OKX__SecretKey   = $secretKey
$env:OKX__Passphrase  = $passphrase
$env:OKX__IsDemoMode  = $isDemo.ToString().ToLower()
$env:DOTNET_ENVIRONMENT      = 'Paper'
$env:ASPNETCORE_ENVIRONMENT  = 'Paper'
$env:TMQ_EXCHANGE_MODE       = 'OKXSwap'

Write-Host ("[CRED] OKX ApiKey={0}c Secret={1}c Pass={2}c (Demo={3})" -f $apiKey.Length, $secretKey.Length, $passphrase.Length, $isDemo)

$cliProj = Join-Path $WorkDir '01_Services\CLI.Tool\ThatsMyQuant.CLI\ThatsMyQuant.CLI.csproj'

$cliArgs = @('harvest-fees', "--since=$Since", "--until=$Until")
if ($OutCsv) { $cliArgs += "--out=$OutCsv" }

& dotnet run --no-build -c Debug --project $cliProj -- @cliArgs 2>&1 | ForEach-Object { Write-Host $_ }
