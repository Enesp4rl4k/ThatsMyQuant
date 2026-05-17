$localUtc = [DateTime]::UtcNow
$okx = Invoke-RestMethod 'https://www.okx.com/api/v5/public/time' -TimeoutSec 5
$okxMs = [long]$okx.data[0].ts
$okxUtc = [DateTimeOffset]::FromUnixTimeMilliseconds($okxMs).UtcDateTime
$drift = ($localUtc - $okxUtc).TotalSeconds
"Local UTC: $($localUtc.ToString('o'))"
"OKX UTC:   $($okxUtc.ToString('o'))"
"Drift:     $($drift.ToString('F2')) seconds (positive = local AHEAD)"
