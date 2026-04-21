# Thats My Quant 

Advanced Agentic Trading Bot with Microservices Architecture, Borsa-MCP Integration, and AI Reasoning.

## Project Structure
- **01_Services**: Webhooks (Listener), Intelligence (QuantEngine), Execution (Hand).
- **02_Core**: Domain Entities and Application Logic.
- **03_Infrastructure**: Borsa Data, DeepSeek AI, Persistence.
- **04_UI**: Windows Dashboard (WPF Command Center).

---

## Quick Start: Running the App

**CRITICAL**: You must be in the project root directory to run these commands.
```powershell
cd C:\Users\parlak\Downloads\ThatsMyQuant
```

### 1. Run the Dashboard (UI)
The best way to see the bot in action is to start the Windows Dashboard.
```powershell
dotnet run --project 04_UI\ThatsMyQuant.Dashboard
```

### 2. Run the Microservices
Open separate terminals for each service to see the logs:

**Listen for Signals (Webhook):**
```powershell
dotnet run --project 01_Services\Listener.Api
```

**Analyze and Reason (The Brain):**
```powershell
dotnet run --project 01_Services\QuantEngine.Worker
```

**Execute Orders (The Hand):**
```powershell
dotnet run --project 01_Services\Execution.Worker
```

---

## Testing with a Sample Signal

Once the `Listener.Api` is running, you can send a mock signal using PowerShell:

```powershell
 $body = '{"Symbol":"THYAO","Side":"Buy","Price":285.50,"Source":"TradingView"}'
 $secret = $env:TMQ_WEBHOOK_SECRET   # Set this via user-secrets or environment variables
 $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
 $nonce = [Guid]::NewGuid().ToString()
 $idempotency = [Guid]::NewGuid().ToString()
 $hmac = New-Object System.Security.Cryptography.HMACSHA256
 $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
 $signatureBytes = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($body))
 $signature = "sha256=" + ([Convert]::ToHexString($signatureBytes).ToLower())

Invoke-RestMethod -Method Post -Uri https://localhost:7054/webhook `
  -ContentType "application/json" `
  -Headers @{
    "X-Signature-256" = $signature
    "X-Timestamp" = $timestamp
    "X-Nonce" = $nonce
    "X-Idempotency-Key" = $idempotency
  } `
  -Body $body
```

### What to Expect:
1. **Listener.Api**: Receives the signal and dispatches a command.
2. **QuantEngine.Worker**: 
   - Fetches live BIST data/news for THYAO via Borsa-MCP.
   - DeepSeek-R1 (Mock) reasons through the signal.
   - Approves the trade.
3. **Execution.Worker**: Executes the order via the Mock Broker.
4. **Dashboard**: Updates the logs and trade list.

---

## Dependencies
- .NET 9 SDK
- Borsa-MCP (Configured in Claude/Antigravity)
- Semantic Kernel (For AI Integration)

---

## Secrets and multi-process deployment

- **Do not commit** API keys, webhook HMAC secrets, or exchange credentials. Use per-machine [user-secrets](https://learn.microsoft.com/dotnet/core/tools/dotnet-user-secrets) (`dotnet user-secrets set "KEY" "value"`), environment variables, or a secret store in production.
- **Listener.Api** expects `TMQ_WEBHOOK_SECRET` (or equivalent in configuration) for HMAC validation; set the same value wherever you generate webhook signatures.
- **Typical layout**: run `Listener.Api`, `QuantEngine.Worker`, and `Execution.Worker` as **separate processes** (three terminals or three services). Each loads its own `appsettings.json` / `exchange_settings.json` and optional user-secrets; they share the database URL via configuration, not via the repo.
- **Observability**: workers expose `/metrics` (Prometheus text) and `/health` where `MapObservabilityEndpoints` is wired; use a single scrape config per service instance with distinct labels (e.g. service name). For product priorities (edge, execution quality, risk), see [docs/STRATEGIC_FOCUS.md](docs/STRATEGIC_FOCUS.md).

### Correlation IDs

- **Listener.Api** assigns a correlation id per HTTP request (`CorrelationIdMiddleware`) and may copy it into signal metadata for downstream tracing.
- **Execution.Worker** ties structured log scopes to each signal via `TradingLogScope` (signal id / symbol / correlation id from metadata when present).
