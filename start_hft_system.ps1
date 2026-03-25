# Start HFT Ecosystem Script
Write-Host "Starting HFT Ecosystem Components..." -ForegroundColor Cyan

# 1. Gateway API
Write-Host "Launching Gateway API..." -ForegroundColor Yellow
Start-Process dotnet "run --project 01_Services\Gateway.Api\ThatsMyQuant.Gateway.Api.csproj" -NoNewWindow
Start-Sleep -Seconds 3

# 2. Listener API
Write-Host "Launching Listener API..." -ForegroundColor Yellow
Start-Process dotnet "run --project 01_Services\Listener.Api\Listener.Api.csproj" -NoNewWindow
Start-Sleep -Seconds 3

# 3. Execution Worker
Write-Host "Launching Execution Worker..." -ForegroundColor Yellow
Start-Process dotnet "run --project 01_Services\Execution.Worker\Execution.Worker.csproj" -NoNewWindow
Start-Sleep -Seconds 3

# 4. Quant Engine Worker
Write-Host "Launching Quant Engine Worker..." -ForegroundColor Yellow
Start-Process dotnet "run --project 01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj" -NoNewWindow
Start-Sleep -Seconds 3

# 5. HFT Dashboard
Write-Host "Launching HFT Dashboard..." -ForegroundColor Green
Start-Process dotnet "run --project 04_UI\ThatsMyQuant.Dashboard\ThatsMyQuant.Dashboard.csproj"

Write-Host "Ecosystem started. Check spawned windows for logs." -ForegroundColor Green
