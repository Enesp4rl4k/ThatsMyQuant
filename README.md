# ThatsMyQuant (TMQ) 🚀



ThatsMyQuant (TMQ) is a high-performance, microservices-based quantitative trading platform designed for professional traders and quant funds. It bridges the gap between retail automation and institutional-level execution through a unique combination of **High-Frequency Trading (HFT)** data structures and **Multi-Agent AI Orchestration**.

---

## 🏗️ Core Pillars

### 1. High-Frequency Executive (HFT)
Optimized for sub-millisecond market data processing and order execution:
- **Lock-Free Architecture**: Utilizes custom `HftRingBuffer` for zero-allocation tick processing.
- **Memory Optimization**: `AllocationFreeSignalPool` minimizes GC pressure, preventing "Stop-the-World" pauses during high volatility.
- **Microsecond Prediction**: Dedicated engines for ultra-low latency price action analysis.

### 2. Agentic Intelligence & Reasoning
Move beyond simple IF/ELSE logic with a distributed "Brain":
- **Multi-Agent Orchestration**: Separate roles for **Risk Management**, **Alpha Generation**, and **Sentiment Analysis**.
- **Visual Reasoning**: The "Logic Tree" system captures and audits the AI's step-by-step decision process.
- **Adaptive Sizing**: Real-time Kelly Criterion and exposure analysis via the `RiskAgent`.

### 3. Advanced Strategy Validation (Backtesting)
Before deploying capital, TMQ provides a high-fidelity simulation environment:
- **Precision Backtesting**: The `BacktestEngine` models slippage, spread, and commissions with ticker-specific precision.
- **Institutional Metrics**: Automated calculation of Sharpe Ratio, Sortino Ratio, Profit Factor, and Max Drawdown.
- **Visual Equity Curves**: Real-time rendering of performance history within the dashboard.
- **Risk Attribution**: Analysis of win rates across different market regimes (e.g., Trending vs. Ranging).

### 4. Institutional Observability (WPF Command Center)
A high-fidelity Windows dashboard providing deep transparency into the bot's operation:
- **Execution Telemetry**: Live Z-scores, latency profiles, and slippage estimates.
- **AI Intelligence Panel**: Direct visibility into the "Thinking" mode of the Reasoning Engine.
- **Strategy Leaderboard**: Performance tracking across different market regimes and strategy buckets.

### 4. Industrial Resilience
Built to survive real-world market chaos:
- **Chaos Mode Recovery**: Designed to resume operations gracefully after network or exchange failures.
- **Dead-Letter Handling**: Robust retry policies and fail-safe queues for execution certainty.
- **Reconciliation Audit**: "Dead-Man-Switch" integrity checks that verify internal state against exchange balance Every 60s.

---

## 📂 Project Architecture

The project follows **Clean Architecture** and **Domain-Driven Design (DDD)** principles:

- **`01_Services`**: Microservices layer.
  - `Listener.Api`: High-availability webhook gateway for external signals.
  - `QuantEngine.Worker`: The "Brain"—multi-agent analysis and strategy orchestration.
  - `Execution.Worker`: The "Hand"—low-latency order routing and management.
- **`02_Core`**: The Heart.
  - `Domain`: Pure business logic, entities, and HFT interfaces.
  - `Application`: Handlers, strategy implementations (Grid, Arbitrage, Trend), and Risk Agents.
- **`03_Infrastructure`**: Adapters for External Worlds.
  - Data persistence (PostgreSQL/SQLite), Broker Adapters, and AI Service (Gemini/DeepSeek) integrations.
- **`04_UI`**: The Command Center.
  - WPF-based Dashboard with real-time charting and AI reasoning overlays.

---

## 🚀 Quick Start

### Prerequisites
- .NET 9.0 SDK
- PostgreSQL (or SQLite for development)

### One-Command Launch (Dev Mode)
To see the full suite in action, run the Dashboard:
```powershell
dotnet run --project 04_UI\ThatsMyQuant.Dashboard
```

### Manual Service Deployment
For high-performance deployments, run the workers independently:
```powershell
# Start the Gateway
dotnet run --project 01_Services\Listener.Api

# Start the Reasoning Engine
dotnet run --project 01_Services\QuantEngine.Worker

# Start the Order Executioner
dotnet run --project 01_Services\Execution.Worker
```

---

## 🛠️ Tech Stack
- **Framework**: .NET 9 (C#)
- **UI**: WPF (CommunityToolkit.Mvvm)
- **Messaging**: MediatR (In-process) / RabbitMQ (Optional Ext)
- **AI**: Gemini 1.5 Pro / DeepSeek-R1 via Semantic Kernel
- **Persistence**: Entity Framework Core

---

## ⚠️ Disclaimer
Trading involves significant risk. This framework is a professional tool and should be used responsibly. Always test strategies in **Paper Mode** (supported natively in TMQ) before deploying real capital.
