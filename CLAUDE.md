# CLAUDE.md — operational rules for this repo

These are hard rules. Violating them has burned the operator's time twice.
Re-read this file before touching the worker.

## 🚨 Worker launch — NEVER bypass `Launch-PaperTrade-Clean.ps1`

**WRONG** (silently falls back to Mock+Chaos, every "fill" is fake, hours of debugging "why no trades"):

```powershell
Start-Process .\01_Services\QuantEngine.Worker\bin\Release\net9.0\ThatsMyQuant.Bot.exe
# or
dotnet run --project ThatsMyQuant.Bot.csproj
```

**RIGHT** (decrypts the DPAPI credential blob with the user's interactive
session — .NET 9 native context cannot do this — and exports
`OKX__ApiKey`, `OKX__SecretKey`, `OKX__Passphrase` plus Telegram + env vars):

```powershell
.\Launch-PaperTrade-Clean.ps1 -SkipBuild        # if already built
.\Launch-PaperTrade-Clean.ps1                   # if rebuild needed
```

To capture stdout while still using the launcher:

```powershell
$out = "C:\Users\parlak\AppData\Local\ThatsMyQuant\worker_logs\worker_$(Get-Date -Format yyyyMMdd_HHmmss).out.log"
Start-Process powershell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass',
  '-File','.\Launch-PaperTrade-Clean.ps1','-SkipBuild') `
  -RedirectStandardOutput $out -RedirectStandardError "$out.err" -WindowStyle Hidden
```

### Why this matters

- `exchange_credentials.dpapi` is encrypted with `DataProtectionScope.CurrentUser`.
- .NET 9 `System.Security.Cryptography.ProtectedData` throws `PlatformNotSupportedException`
  when invoked from the bare worker process — DPAPI is technically Windows-only and the
  runtime ships a stub on the cross-plat build.
- The launcher does the decryption from PowerShell (where the Windows-shipped DPAPI works)
  and pushes the plaintext into env vars the worker reads.
- Without the launcher: `OKX_Creds=False` → falls into the `else` branch → Mock + Chaos.

### Fail-fast guard (post-2026-05-01)

`Program.cs` now refuses to start when `TMQ_MODE=paper|live` and credentials weren't
resolved. Exit code **78** (EX_CONFIG). Override only with explicit
`TMQ_ALLOW_MOCK_FALLBACK=true` for unit-test smoke runs.

If you see `[FATAL] REFUSING TO START: TMQ_MODE=paper but no real-exchange credentials`
in the log → you bypassed the launcher. Use `Launch-PaperTrade-Clean.ps1`.

## Build is locked while worker is running

`ThatsMyQuant.Bot.exe` holds locks on every transitively-referenced DLL
(Domain, Application, Infrastructure, Contracts, AI.DeepSeek). To rebuild:

```powershell
Stop-Process -Name 'ThatsMyQuant.Bot' -Force
dotnet build .\01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj -c Release
.\Launch-PaperTrade-Clean.ps1 -SkipBuild
```

The csproj file is `ThatsMyQuant.Bot.csproj`, NOT `QuantEngine.Worker.csproj`.

## Heartbeat to confirm the worker is alive

`WorkerHeartbeatConsoleHostedService` writes `[HB]` every 60s. If you don't
see `[HB BOOT]` within ~10s of launch, something is wrong with the launch
itself — check the log header for `[CRED] OKX ApiKey=...c` and
`OKX execution service registered (Demo=True)`.

```powershell
Get-Content $log -Wait | Select-String '\[HB|CRED|OKX execution|REFUSING'
```

## Pre-registered tests are running — do NOT touch thresholds

- Funding-Long: `EntryThresholdRate=0.0001` (LTC 1bp), DOGE override 0.5bp,
  N=200 stopping rule. ETA 6-12 weeks.
- Pairs-Long: `ZScoreEntry=2.5` on 4h, 3-pair allowlist (ADA-XRP, BCH-LTC,
  TRX-XLM), N=50 stopping rule. ETA 10-14 weeks.

Lowering thresholds during the test invalidates the Bonferroni / DSR gate and
turns the test into noise-fitting. Even if no trades fire for days — wait.
See `docs/PRE_REGISTERED_HYPOTHESIS_*.md`.
