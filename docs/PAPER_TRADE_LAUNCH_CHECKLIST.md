# Paper Trade Launch Checklist (2026-04-24)

Hızlı başlangıç — aşağıdaki adımları sırayla yap, hiçbirini atlama.

---

## 0. Files Created For You

- `01_Services/QuantEngine.Worker/appsettings.Paper.json` — paper-tuned config overlay
- `Launch-PaperTrade.bat` — launch script (builds Release + starts worker with `DOTNET_ENVIRONMENT=Paper`)
- `docs/observability/grafana-dashboard.json` — Grafana panel set
- `docs/observability/prometheus-scrape.yml` — Prometheus scrape config

---

## 1. Credentials (5 dk) — MUST DO

### 1a. OKX Demo credentials
1. Git: https://www.okx.com/account/my-api (hesap gerekirse aç).
2. Sağ üst "Demo Trading" modunu aktif et. "Create V5 API key" → Read + Trade izinlerini ver, IP kısıtlamasını ekle.
3. Aldığın 3 şeyi `appsettings.Paper.json`'a yaz:
   ```json
   "OKX": {
     "ApiKey": "XXXXXXXX",
     "SecretKey": "YYYYYYYY",
     "Passphrase": "ZZZZZZZZ",
     "IsDemoMode": true
   }
   ```

### 1b. Telegram bot
1. Telegram'da @BotFather ile konuş → `/newbot` → isim ver → token al.
2. @userinfobot ile kendi `chat_id`'ni öğren.
3. `appsettings.Paper.json`:
   ```json
   "Telegram": {
     "BotToken": "123456:ABCDEF...",
     "ChatId": "123456789",
     "EnableReadBot": true,
     "AllowedChatIds": ""
   }
   ```

---

## 2. Pre-flight Sanity (10 dk)

Dashboard'u aç (`Launch-Dashboard.bat`) → **Quant Lab** sekmesi:

### 2a. Each validated strategy için:
Studio dropdown'dan stratejiyi seç → `REFRESH CHARTS`:

| Kart | Geçme kriteri |
|---|---|
| **Performance-Stability Folds** | ⚠ DECAYING etiketi YOK, son 3 fold Sharpe pozitif |
| **Deflated Sharpe (N=50)** | **≥ %60** (altı = selection bias, pas geç) |
| **Probabilistic Sharpe (SR>0)** | ≥ %75 |
| **TCA · Fees leakage** | **< %15** of \|PnL\| (yoksa fees edge'i yer) |
| **TCA · Slippage P90** | < %0.15 (yoksa execution sorunu var) |
| **Correlation Heatmap** | Seçeceğin 2 stratejide ρ < 0.6 |

**Failed olan stratejiyi `appsettings.Paper.json`'dan çıkar** — `QuantEngine:LiveSignalScan:Symbols` / `Strategy:PairsTrading:AllowedPairs` / `Funding:Universe` listelerinden.

### 2b. Regime Timeline kontrolü
Quant Lab → **REGIME TIMELINE** kartı → her symbol için:
- `BTCUSDT 1h` · `SOLUSDT 4h` · `AVAXUSDT 4h` · `DOGEUSDT 1h` ayrı ayrı analiz et.
- Eğer son 200 window'un **%40+'ı Volatile** ise o symbol'ü bu hafta **pas geç** (rejimin oturmasını bekle).

---

## 3. Launch (2 dk)

```
Launch-PaperTrade.bat
```

Console'da şunlar olmalı:
- `✓ ExchangeMode=OKX | OKX_Creds=True (Demo=True)`
- `Paper] Initial balance: 10000 USDT`
- `Telegram read-bot started. Allowed chat IDs: ...`
- `ReconciliationHostedService started: interval=120s tolerance=0.50%`
- `PaperPortfolioPersistenceService loaded snapshot` (2. başlatmadan sonra)

Telegram'da `/status` yaz → bot cevap vermeli.

---

## 4. İlk 2 Saat Monitor (oturarak izle)

| Ne | Nereden | Beklenti |
|---|---|---|
| Sinyal üretimi | Console logs + Dashboard | En az 1 signal, 1 fill gözle |
| Telegram `/positions` | Telegram | 0-2 position, beklenmedik symbol yok |
| Prometheus `/metrics` | http://127.0.0.1:18082/metrics | `tmq_runtime_ready 1` |
| Reconciliation | Logs "Reconciliation OK" | Drift < %0.5 |
| Circuit breaker | Dashboard + Telegram `/status` | CLEAR |
| Log file | `%LOCALAPPDATA%\ThatsMyQuant\logs\quantengine-YYYYMMDD.clef` | Her 10-30s yeni satır |

Beklenmeyen bir şey görürsen **Ctrl+C** (graceful flush yapar, JSON portfolio snapshot alır).

---

## 5. Haftalık Kadans (30 gün boyunca)

**Her Pazar 21:00:**
1. Dashboard → Quant Lab → **P&L Attribution tile** screenshot al
2. **TCA card** kontrolü: fees leakage değişimi, slippage trend
3. **Performance-Stability Folds** güncel hali — decay var mı?
4. Paper PnL (toplam) vs Backtest Sharpe-implied PnL oranını hesapla:
   - Beklenen haftalık PnL = `backtest_sharpe × sqrt(1/52) × initial_balance`
   - Paper PnL bunun %50'sinden düşükse → edge illüzyondu, stratejiyi elle.

---

## 6. Kill Criteria (bu tetiklerse HEMEN DURDUR)

1. Portfolio DD > **%3** (circuit breaker otomatik yakalar ama yine de kontrol et)
2. TCA fees leakage > **%25** of \|PnL\|
3. Tek strateji **5 ardışık kayıp**
4. Reconciliation drift > **%0.5** (RiskAgent soft-pause edilir zaten)
5. Gün içinde **3+ unhandled exception** log'da
6. `/metrics` 5 dakikadan fazla cevap vermiyor (health sorunu)

Kill komutu: **Telegram `/status`** ile doğrula, `Ctrl+C` ile worker'ı durdur. Pozisyonları kapatmak için:
- Dashboard → Pro Mode → **🛑 KILL** butonu

---

## 7. Live Trade'e Geçiş (30 gün sonra)

Bu checklist TÜMÜYLE yeşilse:
- PSR (SR>0) her strateji için ≥ %80
- 30 gün paper'da realized Sharpe ≥ backtest Sharpe × 0.5
- Fees leakage < %15
- Hiç circuit breaker trip yok
- Reconciliation her gün <%0.3 drift

→ **Canary $100-500** gerçek para ile 1 ay test. Ancak ondan sonra full capital.

---

## Config Değişimi Gerekirse

`appsettings.Paper.json`'ı düzenle → `Ctrl+C` → tekrar `Launch-PaperTrade.bat`. Paper portfolio otomatik restore olur (JSON snapshot'tan).
