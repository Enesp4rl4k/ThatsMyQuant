@echo off
REM Standalone log viewer — opens the live tail in a new window. Use this any time
REM the paper trade worker is running and you want to peek at decisions/trades.
REM (The launcher Launch-PaperTrade-Clean.ps1 already spawns this automatically.)
title ThatsMyQuant Paper - Live Logs
powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -File "%~dp0Show-PaperTradeLogs.ps1"
