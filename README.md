# ThatsMyQuant

Quantitative crypto trading system for OKX (demo / live), with a .NET 9 worker, WPF command center, and pre-registered strategy tests.

Paper trading is the supported path today. Live mode is gated behind the same credential and risk controls.

---

## Stack

| Layer | Tech |
| --- | --- |
| Runtime | .NET 9 |
| Worker | `ThatsMyQuant.Bot` (hosted services) |
| Exchange | OKX REST / WebSocket (demo or live) |
| UI | WPF Dashboard + Launcher |
| Persistence | SQLite (Postgres-ready) |
| Observability | Prometheus `/metrics`, health endpoints, Serilog |
| Optional AI | DeepSeek / Semantic Kernel (analysis assist, not required for core strategies) |

---

## Repository layout

```
01_Services/          Worker, Listener API, Execution, Gateway, CLI
02_Core/              Domain + application logic
03_Infrastructure/    Persistence, exchange adapters, AI.DeepSeek
04_UI/                WPF Dashboard and Launcher
05_Tests/             Unit / integration / dashboard tests
06_Contracts/         Shared interfaces and options
docs/                 Runbooks, hypotheses, observability configs
```

| Path | Role |
| --- | --- |
| `01_Services/QuantEngine.Worker` | Main bot (`ThatsMyQuant.Bot`) — strategies, risk, paper/live execution |
| `01_Services/Listener.Api` | Signed webhook ingress (e.g. TradingView) |
| `01_Services/Execution.Worker` | Order execution service |
| `01_Services/Gateway.Api` | API gateway |
| `04_UI/ThatsMyQuant.Dashboard` | Operator UI |
| `04_UI/ThatsMyQuant.Launcher` | Desktop launcher |

---

## Requirements

- Windows 10/11 (DPAPI credential store and WPF UI)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- OKX API keys (demo recommended for paper)
- Optional: Telegram bot token for alerts

---

## Quick start (paper trade)

Credentials are stored locally as DPAPI-protected blobs (`exchange_credentials.dpapi`). They are **not** committed. Decrypting them requires an interactive Windows session, so the worker must be started through the launcher — not with bare `dotnet run`.

### 1. Configure credentials

Use the Dashboard / Launcher to save OKX demo keys and optional Telegram settings. Files written at the repo root (gitignored):

- `exchange_credentials.dpapi`
- `exchange_settings.json`
- `telegram_settings.json`

See [docs/PAPER_TRADE_LAUNCH_CHECKLIST.md](docs/PAPER_TRADE_LAUNCH_CHECKLIST.md) for the full checklist.

### 2. Build and launch

From the repository root:

```powershell
# First run (builds Release, then starts paper mode)
.\Launch-PaperTrade-Clean.ps1

# Already built
.\Launch-PaperTrade-Clean.ps1 -SkipBuild
```

The launcher:

1. Decrypts OKX credentials via Windows DPAPI
2. Exports `OKX__ApiKey`, `OKX__SecretKey`, `OKX__Passphrase`, Telegram env vars
3. Sets `TMQ_MODE=paper` and `DOTNET_ENVIRONMENT=Paper`
4. Starts `ThatsMyQuant.Bot` in Release

**Do not** start the worker with:

```powershell
# Wrong — falls back to mock fills (or exits with code 78)
dotnet run --project 01_Services\QuantEngine.Worker
Start-Process .\01_Services\QuantEngine.Worker\bin\Release\net9.0\ThatsMyQuant.Bot.exe
```

If you see `REFUSING TO START: TMQ_MODE=paper but no real-exchange credentials`, you bypassed the launcher.

### 3. Confirm the worker is alive

Within ~10 seconds of launch you should see credential and heartbeat lines:

```text
[CRED] OKX ApiKey=...c
OKX execution service registered (Demo=True)
[HB BOOT]
```

Heartbeat (`[HB]`) continues every 60 seconds.

### 4. Ops helpers

| Script | Purpose |
| --- | --- |
| `Get-PaperTradeStatus.ps1` | Process / status snapshot |
| `Show-PaperTradeLogs.ps1` | Tail worker logs |
| `Restart-Worker.ps1` | Stop and relaunch cleanly |
| `Watchdog-PaperTrade.ps1` | Auto-recover on worker death |
| `Kill-OkxPositions.ps1` | Emergency flatten (use with care) |

Full runbook: [docs/PAPER_TRADE_RUNBOOK_2026_04_24.md](docs/PAPER_TRADE_RUNBOOK_2026_04_24.md).

---

## Dashboard

```powershell
dotnet run --project 04_UI\ThatsMyQuant.Dashboard
```

Or use `Launch-Dashboard.bat` / `Launch-Launcher.bat` from the repo root.

---

## Build and test

```powershell
dotnet restore ThatsMyQuant.sln
dotnet build ThatsMyQuant.sln -c Release
dotnet test 05_Tests\ThatsMyQuant.Tests\ThatsMyQuant.Tests.csproj -c Release
```

CI (GitHub Actions) restores, builds the dashboard, and runs core + dashboard tests on `windows-latest`.

**Note:** Stop `ThatsMyQuant.Bot` before rebuilding — it locks referenced DLLs.

```powershell
Stop-Process -Name 'ThatsMyQuant.Bot' -Force -ErrorAction SilentlyContinue
dotnet build .\01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj -c Release
.\Launch-PaperTrade-Clean.ps1 -SkipBuild
```

---

## Strategies (pre-registered)

Active paper tests use fixed thresholds. Do not retune mid-test — that invalidates the statistical gate.

| Strategy | Notes | Spec |
| --- | --- | --- |
| Funding-Long | Entry thresholds pre-registered; N=200 stopping rule | [docs/PRE_REGISTERED_HYPOTHESIS_funding_long.md](docs/PRE_REGISTERED_HYPOTHESIS_funding_long.md) |
| Pairs-Long | Z-score entry on 4h; allowlisted pairs; N=50 | [docs/PRE_REGISTERED_HYPOTHESIS_pairs_long.md](docs/PRE_REGISTERED_HYPOTHESIS_pairs_long.md) |

---

## Secrets and configuration

Never commit:

- API keys, passphrases, webhook HMAC secrets
- `exchange_credentials.dpapi`, `exchange_settings.json`, `telegram_settings.json`
- `appsettings.Paper.json`, `appsettings.Live.json`, `appsettings.runtime.json`, `.env*`

Prefer:

- Dashboard-written DPAPI blobs (paper path above), or
- [User secrets](https://learn.microsoft.com/dotnet/core/tools/dotnet-user-secrets) / environment variables for service-specific keys

`Listener.Api` validates webhooks with `TMQ_WEBHOOK_SECRET` (HMAC). Use the same secret when signing outbound test requests.

---

## Observability

Workers expose Prometheus text metrics and health endpoints where wired. Sample configs:

- [docs/observability/prometheus-scrape.yml](docs/observability/prometheus-scrape.yml)
- [docs/observability/grafana-dashboard.json](docs/observability/grafana-dashboard.json)

---

## Documentation

| Doc | Contents |
| --- | --- |
| [Paper launch checklist](docs/PAPER_TRADE_LAUNCH_CHECKLIST.md) | Credentials and first start |
| [Paper runbook](docs/PAPER_TRADE_RUNBOOK_2026_04_24.md) | Day-to-day operations |
| [Live readiness audit](docs/LIVE_READINESS_AUDIT_2026_04_24.md) | Pre-live checklist |
| [Postgres migration](docs/POSTGRES_MIGRATION.md) / [cutover](docs/POSTGRES_CUTOVER.md) | SQLite → Postgres |
| [Funding hypothesis](docs/PRE_REGISTERED_HYPOTHESIS_funding_long.md) | Pre-registered Funding-Long test |
| [Pairs hypothesis](docs/PRE_REGISTERED_HYPOTHESIS_pairs_long.md) | Pre-registered Pairs-Long test |

---

## License

Private repository. All rights reserved.
