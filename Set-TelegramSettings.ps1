# Set-TelegramSettings.ps1
# Writes telegram_settings.json (gitignored) used by Launch-PaperTrade-Clean.ps1 (bot alerts) and
# Watchdog-PaperTrade.ps1. The bot token is typed hidden; the chat id is detected automatically
# from the first message you send to your bot, then a confirmation message is sent to that chat.
#
# Before running: in Telegram open @BotFather -> /newbot -> copy the token it gives you.
# ASCII-only on purpose (PS 5.1 + Turkish locale).

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$s = Read-Host -Prompt 'Telegram bot token (from @BotFather, hidden)' -AsSecureString
$b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b).Trim() } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
if (-not $token) { throw 'Token is required.' }
$api = "https://api.telegram.org/bot$token"

$me = Invoke-RestMethod "$api/getMe"
if (-not $me.ok) { throw 'Token rejected by Telegram (getMe failed).' }
$botName = $me.result.username
Write-Host "Bot OK: @$botName" -ForegroundColor Green
Write-Host "Now open Telegram, find @$botName, press START (or send any message). Waiting up to 3 minutes..." -ForegroundColor Cyan

$chatId = $null; $deadline = (Get-Date).AddMinutes(3)
while (-not $chatId -and (Get-Date) -lt $deadline) {
    $u = Invoke-RestMethod "$api/getUpdates?timeout=20"
    $msg = $u.result | Where-Object { $_.message } | Select-Object -Last 1
    if ($msg) { $chatId = [string]$msg.message.chat.id }
}
if (-not $chatId) { throw 'No message received from you within 3 minutes. Run the script again.' }

[ordered]@{ BotToken = $token; ChatId = $chatId; EnableReadBot = $false } | ConvertTo-Json |
    Set-Content -Path (Join-Path $PSScriptRoot 'telegram_settings.json') -Encoding utf8
$text = "ThatsMyQuant alerts connected. Watchdog, risk supervisor and order failures will be reported here."
Invoke-RestMethod "$api/sendMessage" -Method Post -Body @{ chat_id = $chatId; text = $text } | Out-Null
$token = $null
Write-Host "Saved telegram_settings.json (chat id $chatId) and sent a test message." -ForegroundColor Green
Write-Host "The bot picks it up on its next restart; the watchdog reads it on every run."
