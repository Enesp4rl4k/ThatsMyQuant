# Kill-switch: tüm OKX (demo) perpetual swap pozisyonlarını anında kapat.
# Kullanım: .\Kill-OkxPositions.ps1
# Worker'a dokunmaz, sadece OKX'teki açık pozları flatten eder. Bracket SL/TP'ler
# attached olduğu için pozisyon kapanınca onlar da otomatik iptal olur.
[CmdletBinding()] param(
    [string]$WorkDir = "C:\Users\parlak\Downloads\ThatsMyQuant",
    [switch]$AlsoClearWorkerState,
    # When set, only this single instId is closed (e.g. "XRP-USDT-SWAP" or "XRPUSDT").
    # Useful for surgical drift-cleanup without flattening the whole book.
    [string]$Symbol = ""
)

Set-Location -LiteralPath $WorkDir

# ---- 1) Decrypt OKX demo creds (same DPAPI flow as launcher) -----------------
Add-Type -AssemblyName System.Security
function Decrypt-Field([string]$b64) {
    if ([string]::IsNullOrEmpty($b64)) { return '' }
    try {
        $bytes = [Convert]::FromBase64String($b64)
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [Text.Encoding]::UTF8.GetString($plain)
    } catch { return '' }
}
$creds = Get-Content 'exchange_credentials.dpapi' -Raw | ConvertFrom-Json
$apiKey     = Decrypt-Field $creds.DemoApiKey
$secretKey  = Decrypt-Field $creds.DemoSecretKey
$passphrase = Decrypt-Field $creds.DemoPassphrase

if ([string]::IsNullOrEmpty($apiKey) -or [string]::IsNullOrEmpty($secretKey)) {
    Write-Error "OKX demo credentials could not be decrypted. Run launcher once interactively first."
    exit 1
}
Write-Host "[CRED] Decrypted OKX demo creds (key=$($apiKey.Length)c secret=$($secretKey.Length)c)"

# ---- 2) HMAC-SHA256 + Base64 signing helper ----------------------------------
$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [Text.Encoding]::UTF8.GetBytes($secretKey)
function Sign([string]$ts, [string]$method, [string]$path, [string]$body) {
    $prehash = "$ts$method$path$body"
    $bytes = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($prehash))
    return [Convert]::ToBase64String($bytes)
}
function IsoTs() { return (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }

$base = 'https://www.okx.com'
$baseHeaders = @{
    'OK-ACCESS-KEY'        = $apiKey
    'OK-ACCESS-PASSPHRASE' = $passphrase
    'x-simulated-trading'  = '1'
    'Content-Type'         = 'application/json'
}

# ---- 3) Fetch open positions -------------------------------------------------
$ts = IsoTs
$path = '/api/v5/account/positions'
$sig = Sign $ts 'GET' $path ''
$h = $baseHeaders.Clone()
$h['OK-ACCESS-SIGN'] = $sig
$h['OK-ACCESS-TIMESTAMP'] = $ts

$resp = Invoke-RestMethod -Uri "$base$path" -Method Get -Headers $h
if ($resp.code -ne '0') { Write-Error "Positions fetch failed: $($resp.msg)"; exit 1 }
$openPositions = $resp.data | Where-Object { [decimal]$_.pos -ne 0 }
if (-not [string]::IsNullOrEmpty($Symbol)) {
    # Normalize: accept "XRPUSDT" or "XRP-USDT-SWAP" as input.
    $sFilter = if ($Symbol -match '-') { $Symbol } else {
        # Convert XRPUSDT → XRP-USDT-SWAP
        ($Symbol -replace 'USDT$', '') + '-USDT-SWAP'
    }
    Write-Host "[FILTER] Single symbol mode: closing only $sFilter"
    $openPositions = @($openPositions | Where-Object { $_.instId -eq $sFilter })
}
Write-Host "[POS] $($openPositions.Count) open position(s) on OKX demo."
if ($openPositions.Count -eq 0) {
    Write-Host "Nothing to close. Exiting."
    if ($AlsoClearWorkerState) {
        $stateDir = "$env:LOCALAPPDATA\ThatsMyQuant"
        foreach ($f in @('funding_long_state.json','funding_short_state.json','funding_hybrid_state.json',
                         'pairs_long_state.json','pairs_short_state.json','pairs_hybrid_state.json')) {
            '{}' | Out-File "$stateDir\$f" -Encoding ASCII -NoNewline
        }
        Write-Host "[STATE] Worker state files cleared."
    }
    exit 0
}

# ---- 4) Close each position via /api/v5/trade/close-position -----------------
$closed = 0; $failed = 0
foreach ($p in $openPositions) {
    $instId  = $p.instId
    $posSide = $p.posSide   # "net" / "long" / "short"
    $mgnMode = $p.mgnMode   # "isolated" / "cross"
    $body = @{ instId = $instId; mgnMode = $mgnMode }
    if ($posSide -ne 'net') { $body.posSide = $posSide }
    $bodyJson = $body | ConvertTo-Json -Compress

    $ts2 = IsoTs
    $path2 = '/api/v5/trade/close-position'
    $sig2 = Sign $ts2 'POST' $path2 $bodyJson
    $h2 = $baseHeaders.Clone()
    $h2['OK-ACCESS-SIGN'] = $sig2
    $h2['OK-ACCESS-TIMESTAMP'] = $ts2

    try {
        $r = Invoke-RestMethod -Uri "$base$path2" -Method Post -Headers $h2 -Body $bodyJson
        if ($r.code -eq '0') {
            $closed++
            Write-Host "  [CLOSED] $instId (posSide=$posSide mgnMode=$mgnMode)"
        } else {
            $failed++
            $sCode = if ($r.data) { $r.data[0].sCode } else { '' }
            $sMsg  = if ($r.data) { $r.data[0].sMsg } else { '' }
            Write-Warning "  [FAIL] $instId code=$($r.code) msg=$($r.msg) sCode=$sCode sMsg=$sMsg"
        }
    } catch {
        $failed++
        Write-Warning "  [EXC] $instId : $_"
    }
}

Write-Host ""
Write-Host "[DONE] Closed=$closed Failed=$failed"

# ---- 5) Optional: also clear worker state files ------------------------------
if ($AlsoClearWorkerState) {
    $stateDir = "$env:LOCALAPPDATA\ThatsMyQuant"
    foreach ($f in @('funding_long_state.json','funding_short_state.json','funding_hybrid_state.json',
                     'pairs_long_state.json','pairs_short_state.json','pairs_hybrid_state.json')) {
        '{}' | Out-File "$stateDir\$f" -Encoding ASCII -NoNewline
    }
    Write-Host "[STATE] Worker state files cleared."
}
