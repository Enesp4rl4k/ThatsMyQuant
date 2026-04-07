# Phase 4 Alpha Generation Plan

## Scope

Phase 4 builds on the now-stable execution and ops foundation. This slice focuses on generating better opportunities rather than hardening the transport layer.

## Goals

1. Add a repeatable ML model experiment framework so model candidates can be compared without guesswork.
2. Implement dynamic symbol-universe ranking so the system can focus attention on the best current opportunities.
3. Add multi-timeframe confluence scoring to improve signal quality before execution.
4. Introduce portfolio rebalancing planning based on target weights and live drift.
5. Formalize BIST market integration behind an interface so the current simulated implementation can be upgraded to a live provider cleanly.

## Delivered In This Slice

1. `ModelExperimentService` for deterministic cohort assignment and post-trade experiment evaluation.
2. `DynamicSymbolUniverseService` for ranking market symbols using liquidity, volatility, momentum, and recent expectancy.
3. `MultiTimeframeConfluenceService` for scoring alignment across multiple candle timeframes.
4. `PortfolioRebalancingScheduler` for scheduling and planning drift-aware rebalance actions.
5. `IBistMarketGateway` + `SimulatedBistMarketGateway` as the BIST integration seam, wired into `BorsaMarketDataService`.

## Backlog After This Slice

1. Promote experiment winners automatically into active ML routing.
2. Feed dynamic universe outputs directly into the live scan/watchlist pipeline.
3. Attach confluence scoring to strategy promotion and dashboard analytics.
4. Execute rebalance plans through the live order pipeline with approvals.
5. Swap the simulated BIST gateway for a real Borsa-MCP or broker-backed implementation.
