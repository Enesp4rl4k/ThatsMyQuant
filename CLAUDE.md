# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project: Thats My Quant

Agentic trading bot built as a multi-process .NET 9 microservices system. Ingests trading signals (TradingView webhooks, BIST data via Borsa-MCP, Binance WebSocket), runs them through a multi-tier agent swarm for risk/strategy/ML consensus, then dispatches to mock or live exchanges. Includes a WPF "Command Center" dashboard and a separate React/Vite web UI.

## Build, Test, Run

The solution is `ThatsMyQuant.sln` at the repo root. All projects target `net9.0` (the Dashboard targets `net9.0-windows`).

```bash
# Restore + build the whole solution
dotnet build ThatsMyQuant.sln

# Run all tests (xUnit + NSubstitute + FluentAssertions)
dotnet test ThatsMyQuant.sln

# Run one test project / one test
dotnet test 05_Tests/ThatsMyQuant.Tests/ThatsMyQuant.Tests.csproj
dotnet test --filter "FullyQualifiedName~SwarmOrchestratorTests"
dotnet test --filter "FullyQualifiedName~SwarmOrchestratorTests.MethodName"

# Run a service (each is a separate process; see "Service Topology" below)
dotnet run --project 01_Services/Listener.Api/Listener.Api.csproj
dotnet run --project 01_Services/QuantEngine.Worker/ThatsMyQuant.Bot.csproj
dotnet run --project 01_Services/Execution.Worker/Execution.Worker.csproj
dotnet run --project 01_Services/Gateway.Api/ThatsMyQuant.Gateway.Api.csproj
dotnet run --project 04_UI/ThatsMyQuant.Dashboard/ThatsMyQuant.Dashboard.csproj  # Windows only (WPF)

# Bring up the full ecosystem (PowerShell on Windows)
./start_hft_system.ps1

# Send a sample signed webhook (PowerShell)
./test_signal.ps1
```

The React web UI lives in `04_UI/ThatsMyQuant.Web` and is independent of the .NET solution:

```bash
cd 04_UI/ThatsMyQuant.Web
npm install
npm run dev      # vite dev server on :5173
npm run build    # tsc -b && vite build
npm run lint     # eslint .
```

### Database

Persistence is SQLite via EF Core (`QuantDbContext`). The connection string in both `Listener.Api/Program.cs` and `QuantEngine.Worker/Program.cs` is **hardcoded to a Windows absolute path** (`C:\Users\parlak\Downloads\ThatsMyQuant\quant.db`). On non-Windows or a different checkout location you must change this string before the services will start. `db.Database.EnsureCreatedAsync()` runs at startup — there are no EF migrations.

### Test stack

xUnit + NSubstitute + FluentAssertions, with `Microsoft.AspNetCore.Mvc.Testing` for Listener.Api integration tests. Three test projects:
- `05_Tests/ThatsMyQuant.Tests` — main unit/integration suite (Strategy, Risk, MachineLearning, Handlers, Middleware, Infrastructure, Performance)
- `05_Tests/ThatsMyQuant.SecurityTests` — HMAC, auth, risk-constraint security tests
- `05_Tests/ThatsMyQuant.Dashboard.Tests` — WPF ViewModel tests

## Architecture

### Numbered-folder layering

The repo uses a numbered folder convention that maps directly to a clean-architecture dependency graph. Lower numbers may not depend on higher numbers (with one historical exception, see "Quirks"):

| Folder | Role | Examples |
|---|---|---|
| `06_Contracts` | DTOs, enums, options, interfaces shared by all layers | `Signal`, `Trade`, `IExchangeService`, `RiskGovernorOptions` |
| `02_Core/Domain` | Pure domain primitives | `CircularBuffer` |
| `02_Core/Application` | Strategy logic, risk, ML, MediatR handlers, HFT primitives | `SwarmOrchestrator`, `RiskAgent`, `InferenceAgent`, `TickPipeline` |
| `03_Infrastructure/Data.Persistence` | EF Core, repositories, exchange/broker adapters, agent implementations (Tier 1–5) | `QuantDbContext`, `BinanceExecutionService`, `KellyPositionSizerAgent` |
| `03_Infrastructure/AI.DeepSeek` | LLM/AI adapters | `DeepSeekReasoningService`, `GeminiSentimentService` |
| `03_Infrastructure/Exchange.Binance` | Low-level Binance WS client | `ZeroAllocWebSocketClient` |
| `01_Services` | Composition roots (entry-point processes) | `Listener.Api`, `QuantEngine.Worker`, `Execution.Worker`, `Gateway.Api`, `CLI.Tool` |
| `04_UI` | Front-ends | `ThatsMyQuant.Dashboard` (WPF), `ThatsMyQuant.Web` (React) |
| `05_Tests` | Test projects |  |

Note: assembly names don't always match folder names. `02_Core/Application` ships as `ThatsMyQuant.Application`, `03_Infrastructure/Data.Persistence` ships as `ThatsMyQuant.Infrastructure`, and `01_Services/QuantEngine.Worker` ships as `ThatsMyQuant.Bot`.

### Service topology

Four .NET processes cooperate. They share state through the SQLite DB and HTTP/WebSocket; they are **not** linked by an in-process MediatR bus across processes.

```
TradingView / BIST → [Listener.Api]  ──HMAC──►  ProcessSignalCommand
                          │                        │
                          │ SignalR /market-hub    │ writes Signal rows
                          ▼                        ▼
                   [Web/Dashboard]            [SQLite quant.db]
                                                   ▲
[QuantEngine.Worker] ── runs TradeBotWorker, periodic reconciliation, in-process pipeline
                                                   │
[Execution.Worker]  ── polls Status=Queued ────────┘  → IExchangeService → broker
[Gateway.Api]       ── gRPC bidi stream → Dashboard telemetry/control
```

- **Listener.Api**: ASP.NET Core webhook (`POST /webhook`), HMAC + ApiKey middlewares, fixed-window rate limiter ("webhook" policy, 30/min), MediatR-dispatches `ProcessSignalCommand`. Also hosts `MarketDataHub` (SignalR) for the React UI.
- **QuantEngine.Worker**: BackgroundService that runs the strategy loop; same DI graph as Listener (it can also handle `ProcessSignalCommand` end-to-end). Wraps `IExchangeService` in `ChaosExchangeDecorator` for fault-injection testing.
- **Execution.Worker**: Polls `Signal` rows where `Status=Queued` every ~1.5s and dispatches `ExecuteTradeCommand`. This is the consumer side of the worker-handoff pattern controlled by `Execution:UseWorkerHandoff` config.
- **Gateway.Api**: gRPC bidi-streaming (`TradingGateway.ConnectStream`, see `01_Services/Gateway.Api/Protos/TradeEngine.proto`). The WPF Dashboard is the gRPC client; messages carry ARM/DISARM/EMERGENCY_STOP signals one way and live PnL telemetry the other.
- **CLI.Tool**: Standalone command-line interface project (`01_Services/CLI.Tool/ThatsMyQuant.CLI`).

### Signal lifecycle (the mental model to keep)

The whole product is built around a single pipeline. Read `02_Core/Application/Handlers/ProcessSignalHandler.cs` first when modifying any stage:

1. **Persist** — Signal saved immediately for audit.
2. **Validate** — chain of `ISignalValidator` (MarketHours, MinVolume, PriceSanity), ordered by `Order` property. Fail-fast on first rejection.
3. **Market data** — `IMarketDataService.GetCandlesAsync` wrapped in Polly retry pipeline (`ResiliencePolicies.CreateRetryPipeline`).
4. **Regime detection** — `MarketRegimeDetector` + `StructuralPatternDetector` adjust `RiskParameters` (Volatile shrinks size, Trending grows it).
5. **5-Tier agent snapshots** — `IAgentCoordinator` builds Foundation (Tier 1) and Pattern (Tier 2) snapshots from registered agents.
6. **Technical analysis** — `ITechnicalAnalysisService` (`QuantIndicatorService`) produces a `TradeDecision` with confidence/SL/TP.
7. **Swarm consensus** — MediatR-dispatched `SwarmNegotiationRequest` → `SwarmOrchestrator` runs Risk/Sentiment/ML inference + Tier 3 strategy votes + Tier 4 risk gauntlet + Tier 5 meta-adaptation, computes confidence, and either approves with adjusted Kelly/TP or vetoes with `SignalReasonFlags`.
8. **Dispatch** — if `Execution:UseWorkerHandoff=true` (default in Listener), Status flips to `Queued` and `Execution.Worker` picks it up later; otherwise `ExecuteTradeCommand` runs synchronously through `IExchangeService`.

Failure handling: `SwarmOrchestrator` tracks consecutive failures and arms a kill-switch on the `RiskAgent` after 3 strikes (`TriggerEmergencyShutdown`).

### 5-Tier Agent Hierarchy

Wired in `03_Infrastructure/Data.Persistence/DependencyInjection.cs::AddThatsMyQuantInfrastructure`. Multiple agents are registered per tier; `AgentCoordinator` runs them in parallel and merges results:

- **Tier 1 — Foundation** (`IFoundationAgent`): `OrderBookDepthAnalyzer`, `VolumeProfileAgent`, `MicrostructureAgent`
- **Tier 2 — Patterns** (`IPatternAgent`): `VwapDeviationAgent`, `FibonacciAgent`, `IchimokuAgent`, `BollingerBandAgent`
- **Tier 3 — Strategy** (`IStrategyAgent`): `MomentumStrategyAgent`, `BreakoutStrategyAgent`, `StatisticalArbitrageAgent`, `ScalpingStrategyAgent`
- **Tier 4 — Risk** (`IRiskGuardAgent`, ordered by `Order`): `KellyPositionSizerAgent`, `PortfolioOptimiserAgent`, `CorrelationGuardAgent`
- **Tier 5 — Meta-Learning** (`IMetaLearningAgent`, **singletons** to keep rolling state): `RegimeSwitchingModelAgent`, `OnlineLearningAdaptorAgent`, `ReinforcementLearningAgent`

To add a new agent: implement the tier interface (most also implement `IFocusedAgent` for `ResetAsync`), register it in `AddThatsMyQuantInfrastructure`. Don't construct it manually — `AgentCoordinator` discovers all registered implementations of the interface via `IEnumerable<T>`.

### Application services DI conventions

`02_Core/Application/Common/DependencyInjection.cs::AddThatsMyQuantApplication` is the single registration point for application-layer services. The split is intentional:

- **Singletons** — stateful/heavy: `RiskAgent`, `InferenceAgent`, `FeatureEngineeringAgent`, `MemoryPortfolioManager`, `TickPipeline`, `CircuitBreakerService`, `FeatureDriftMonitor`, ML training pipeline.
- **Scoped** — per-request logic and LLM-dependent: `SwarmOrchestrator`, `SentimentAgent`, expectancy/edge services, `HftExecutionService`, all `IStrategyRouter`s.

Match this lifetime split when adding services. Never inject `Scoped` into `Singleton`.

### Cross-cutting infrastructure to know about

- **MediatR** is the in-process bus. Commands/events live in `06_Contracts/Commands` and `06_Contracts/Models` (e.g., `Signal : INotification`). Handlers live in `02_Core/Application/Handlers`. Registration: `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessSignalCommand).Assembly))`.
- **Resilience**: `ResiliencePolicies` (Polly v8 pipelines) wraps external I/O. Use it instead of bare `try/catch` for retryable operations.
- **HFT primitives** in `02_Core/Application/HftInfrastructure/` and `MachineLearning/`: `HftRingBuffer`, `TickRingBuffer`, `AllocationFreeSignalPool`, `CircuitBreakers`, `OnlineMathHelper`. These are designed for zero-allocation hot paths — preserve that when editing (no LINQ in inner loops, no boxing, reuse pooled instances).
- **`PerformanceBootstrapper.OptimizeForHft()`** runs at the top of every service `Program.cs`. It pre-warms the ThreadPool, sets `GCLatencyMode.SustainedLowLatency`, and bumps process priority to `High`. Don't remove it.
- **Multi-exchange routing**: `ExchangeServiceResolver.Resolve(exchangeName)` switches between `BinanceExecutionService`, `OkxExecutionService`, and the default (mock). The default `IExchangeService` registration in QuantEngine is `MockBrokerService` wrapped in `ChaosExchangeDecorator` — keep that decorator pattern when adding new exchange wrappers.
- **AI providers**: `IReasoningProvider` (DeepSeek), `ISentimentService` (Gemini), Semantic Kernel `IChatCompletionService`. If `Ai:GeminiApiKey` config is empty, the system falls back to `MockChatCompletionService` — keep both code paths working.

### Security model for the webhook

`Listener.Api` enforces a four-header HMAC scheme via `HmacValidationMiddleware`:

- `X-Signature-256`: `sha256=<hex(HMAC-SHA256(WebhookSecret, body))>`
- `X-Timestamp`: unix seconds, must be within `Security:WebhookTimestampWindowMinutes` (default 5)
- `X-Nonce`: GUID, single-use (tracked in `WebhookRequestLog` + an in-memory in-flight set)
- `X-Idempotency-Key`: GUID, deduplicates retries (unique index in DB)

`WebhookSecret` defaults to `"change-me-in-production-use-user-secrets"` in `appsettings.json` — real deployments must override via user-secrets/env. `ApiKeyValidationMiddleware` runs after HMAC for an additional internal-API-key check. `WebhookCleanupService` is a hosted service that purges expired `WebhookRequestLog` rows.

`HardwareSecurityModule` + `ApiVault` (in `02_Core/Application/Security`) abstract credential storage for exchange API keys.

## Conventions

- **Namespaces** follow the assembly name, not the folder. The `Application` layer is `ThatsMyQuant.Application.*`, infrastructure is `ThatsMyQuant.Infrastructure.*`, contracts are `ThatsMyQuant.Contracts.*`. Don't introduce a `ThatsMyQuant.Core.*` or folder-prefix namespace.
- **`ImplicitUsings` and `Nullable`** are both enabled solution-wide. Honor non-null contracts; don't sprinkle `?` to silence warnings.
- **EF Core**: schema is defined fluently in `QuantDbContext.OnModelCreating`. `Signal.Metadata` is a `Dictionary<string,string>` JSON-serialized into a TEXT column with a custom `ValueComparer` — preserve that pattern for new dictionary properties or change tracking will break.
- **Logging**: Serilog (`Serilog.Sinks.Console`) is the default. Use structured logging templates (`{Symbol}`, `{Side}`) — never string-concatenate values into the message.
- **Config**: per-service `appsettings.json` + `appsettings.Development.json`. Keys of note: `WebhookSecret`, `Security:InternalApiKey`, `Security:WebhookTimestampWindowMinutes`, `Execution:UseWorkerHandoff`, `Ai:GeminiApiKey`.

## Quirks worth flagging before you start editing

- The SQLite path is hardcoded to a Windows absolute path in two `Program.cs` files. Linux/macOS sessions will fail at `EnsureCreatedAsync` until this is changed.
- `src/ThatsMyQuant.Bot/` and `src/ThatsMyQuant.Infrastructure/` are leftover/legacy folders (not in the `.sln`). The active code lives under `01_Services` / `02_Core` / `03_Infrastructure`. Don't add new code under `src/`.
- The `04_UI/ThatsMyQuant.Dashboard` project targets `net9.0-windows` and uses `<UseWPF>true</UseWPF>` — it will not build on Linux/macOS. The non-WPF projects build fine cross-platform.
- A second `.csproj` named `ThatsMyQuant.Dashboard_5sdu050h_wpftmp.csproj` sits in the Dashboard folder — that's a WPF designer-tool tempfile; don't reference or edit it.
- `MemoryPortfolioManager` exists in **both** `02_Core/Application/Strategy/` and `02_Core/Application/Risk/` and `03_Infrastructure/Data.Persistence/Services/`. The infrastructure copy is the singleton instance (`MemoryPortfolioManager.Instance`) wired via `ISharedPortfolioStateManager`. Confirm which one you need before extending.
- `future_architecture_plan.md` describes work that is **not yet done** (real Binance routing, ring-buffer feature engineering, L2 market replay, in-memory portfolio cache, hard circuit breakers). Treat it as roadmap, not current state.
