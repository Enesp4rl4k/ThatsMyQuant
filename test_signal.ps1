# Test Signal Script

# Ensure Listener.Api is running on http://localhost:5000
$uri = "http://localhost:5000/api/signals"
$body = @{
    Symbol = "THYAO"
    Side = "Buy"
    Price = 285.50
    Source = "ManualTest"
} | ConvertTo-Json

Write-Host "Sending test signal to Thats My Quant..." -ForegroundColor Cyan
Invoke-RestMethod -Method Post -Uri $uri -ContentType "application/json" -Body $body
Write-Host "Done!" -ForegroundColor Green
