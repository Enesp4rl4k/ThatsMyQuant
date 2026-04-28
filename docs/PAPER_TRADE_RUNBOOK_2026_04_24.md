# Paper Trade Operational Runbook — 2026-04-24

**Purpose:** The canonical checklist to take the system from current state (backtested
+ Phase D validated) into 30-60 day paper trade validation before committing any real
capital.

---

## ✅ Day 0 — Pre-Flight Checklist (one-time)

Before first launch, confirm each:

### Configuration
- [ ] `OperatorAlerts:Telegram:BotToken` + `ChatId` set in `appsettings.json`
  *(or accept log-only alerts — NOT recommended for unattended runs)*
- [ ] `StablecoinAllocation` defaults acceptable (70/30 USDT/USDC) or override
- [ ] Risk profile chosen: **Moderate (3x)** recommended for paper trade
- [ ] Max per-position weight set (default 25%) — guards TRX/XLM dominance

### Infrastructure
- [ ] Desktop shortcut (`ThatsMyQuant Dashboard`) opens cleanly
- [ ] `dotnet build -c Release` zero errors
- [ ] SQLite DB at `%LOCALAPPDATA%\ThatsMyQuant\quant.db` writable
- [ ] OKX funding cache populated (run `fundingbacktest --symbol BTC-USDT --days 30`)
- [ ] Pro Mode tab loads without crash

### Safety Layers
- [ ] Circuit breaker trip threshold confirmed 5% (do not relax)
- [ ] Leverage hard cap 15x confirmed in runners
- [ ] Concentration guard registered in DI
- [ ] Correlation guard thresholds 0.30/0.50/0.70
- [ ] Kill switch visible in Pro Mode tab

### Drills
- [ ] **Kill switch drill** — press 🛑, verify "ARMED" → "DISARMED", scanner stops
- [ ] **Disconnect drill** — pull WiFi for 60s, verify reconnect without stale state
- [ ] **Manual circuit reset drill** — trigger via unit test, verify reset workflow

---

## 🏃 Day 1 — First Launch

1. Open Dashboard via desktop shortcut
2. Log in as Admin
3. Navigate to **🛡️ Pro Mode** tab
4. Set leverage slider to **3.0x** (Moderate)
5. Click **ARM**
6. Verify:
   - Status: "ARMED @ base leverage 3.0x"
   - Scanner cycle runs every 60s
   - Opportunity feed populates as funding crosses thresholds
7. Leave running for 24 hours, observe:
   - No crashes
   - Memory stays below 500 MB
   - CPU stays below 10% average
   - Candidate pool tracks signals

**If any check fails → DISARM + investigate before continuing.**

---

## 📈 Days 2-7 — Observation Phase (no live execution yet)

Daily review, 10 minutes each morning:

1. Check **Pro Mode** tab:
   - Circuit breaker state (should be ARMED green)
   - Regime history — does classification match your market view?
   - Correlation state (should be NORMAL)
2. Check **Funding Monitor** tab:
   - Sparklines render for all 20 symbols
   - Auto-refresh ticks at 60s
   - Signal alerts when funding crosses thresholds
3. Check **Alpha Portfolio** tab:
   - Run "RUN PORTFOLIO BACKTEST" — Sharpe/DD/PnL match expectations
4. Check logs (`%LOCALAPPDATA%\ThatsMyQuant\logs\`):
   - No unhandled exceptions
   - No "circuit breaker TRIPPED" (unless genuine market crash)
   - WebSocket reconnect events if any disconnect happened

---

## 🎯 Days 8-30 — Active Paper Trade

Enable paper trading service to consume signals from Pro Mode candidate pool.
*(This is the STEP where candidate promotions feed live execution on paper.)*

### Daily tasks
1. Morning review (10 min) — state summary
2. Evening review (10 min) — P&L + open positions
3. **Weekly drawdown check** — DD breaches >3% require investigation

### Weekly tasks
1. Backtest comparison — does realised paper match backtest within 20%?
2. Correlation matrix review — any positions drifting toward each other?
3. Stress test replay — run `behaviorsim --days 30` to replay the past month's
   decisions as if from scratch (detect if past behaviour was regime-lagged)

### Biweekly tasks
1. Export trade log (CSV) for archiving
2. Review candidate pool — any rejected candidates re-promote?
3. Clean old data — prune candidates > 30 days old

---

## 🚨 Incident Response

### If circuit breaker trips
1. **Do NOT immediately reset.** Investigate first.
2. Check Pro Mode tab → Circuit breaker reason
3. Review last 24h trade log — which positions bled?
4. If cause understood (e.g. clear market event):
   - Wait for market stabilisation (1-24 hrs)
   - Manually reset via Pro Mode tab (Phase B will wire this button)
5. If cause unclear → **keep tripped**, investigate further before reset.

### If correlation guard panics
1. Verify via external data (TradingView, Glassnode)
2. If real crisis: do nothing — system already flatted pairs to 70% cash
3. If false positive (e.g. your own scanner bug): restart Pro Mode

### If kill switch gets pressed (by you or automation)
1. All new entries blocked + disarmed
2. Review WHY you pressed it — diary entry
3. Do NOT re-arm within 24 hrs without reviewing trade history

### If WebSocket disconnects
1. System should auto-reconnect within ~15 seconds
2. Verify via logs
3. Check for stale positions — mark-to-market should update on reconnect

---

## 🎓 Success Criteria (End of 30 Days)

Only proceed to canary-live if ALL of these hold:

- [ ] **Paper P&L positive** — any magnitude fine; direction is what matters
- [ ] **Paper Sharpe ≥ 1.0** — absolute minimum for live (backtest showed 2.15; 50% degradation is realistic)
- [ ] **Max paper DD ≤ 3%** — circuit breaker should have kept us here
- [ ] **Zero unhandled exceptions** for 7+ consecutive days
- [ ] **WebSocket reconnect verified** successfully at least once (ISP issue, OKX maintenance)
- [ ] **Regime classification matches intuition** — system doesn't call crashes "Trending"
- [ ] **Correlation matrix stayed under panic threshold** (unless genuine crisis hit)
- [ ] **Circuit breaker behaviour validated** — either didn't trip (clean run) or tripped appropriately

---

## 🟢 Canary Live Plan (After Successful Paper Phase)

**Day 1-7:** $100 capital, Moderate profile
**Day 8-21:** If consistent, $500
**Day 22-60:** If consistent, $2,000
**Day 61+:** If consistent, scale to target

**NEVER:**
- Skip steps (going from $500 to $20,000 directly)
- Raise leverage until each tier confirms
- Touch Aggressive profile until 6 months live track record at Moderate
- Disable safety layers (circuit breaker, concentration guard, correlation guard)

---

## 📞 Abort Conditions — Return to Paper if ANY

- Live Sharpe < 0.5 after 30 days
- Live Max DD > 5% (should never happen with circuit breaker)
- Exchange issue (funding freeze, withdrawal delay) affecting >5% capital
- Kill switch pressed twice in same week
- Any unhandled exception in production

---

## 🔧 Open Gaps to Close During Paper Phase

Still outstanding (acceptable for paper, close before live):
- [ ] Binance as secondary exchange (OKX-only risk)
- [ ] USDT→USDC auto-rebalance on depeg detection
- [ ] VPS deployment (home-PC single-point-of-failure)
- [ ] Exchange-side stop-loss orders (belt + suspenders)
- [ ] External health endpoint (Telegram bot to query status)

---

## Signatures
Operator: _______________________  Date: _______________
Reviewed: _______________________  Date: _______________

*Store this runbook physically printed near your trading workstation. If the system
is down, this is the manual playbook to keep you safe.*
