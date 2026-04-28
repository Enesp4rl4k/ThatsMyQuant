@echo off
set DOTNET_ENVIRONMENT=Paper
set ASPNETCORE_ENVIRONMENT=Paper
set TMQ_MODE=paper
set TMQ_EXCHANGE_MODE=OKXSwap
set TMQ_ALLOW_LIVE_TRADING=false
set OKX__ApiKey=8c522e98-1c89-40f7-9180-51d6fa1f8721
set OKX__SecretKey=1CD5A8096323BCB2FB57011BE28BD16D
set OKX__Passphrase=Enes1910ep33!
set OKX__IsDemoMode=true
set Telegram__BotToken=8792043680:AAHzP5hz60Yq5g6cjkh_IvCi1iU_OMCN4AQ
set Telegram__ChatId=5085292583
set Telegram__EnableReadBot=false
dotnet run --no-build --no-launch-profile -c Release --project "C:\Users\parlak\Downloads\ThatsMyQuant\01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj"
