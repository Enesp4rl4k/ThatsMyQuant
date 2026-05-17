# 🏠 RETURN_RUNBOOK — 15 gün sonra döndüğünde ilk 30 dakika

**Yazıldı:** 2026-05-16, pre-vacation sprint Day 7
**Hedef:** Geri döndüğünde **30 saniyede** sistem durumunu anla, **5 dakikada** ne olduğunu öğren, **30 dakikada** karar ver.

---

## 📍 İlk 30 saniye — Sistem hayatta mı?

### 1. PowerShell aç, tek komut:
```powershell
Get-Process -Name 'ThatsMyQuant.Bot' -ErrorAction SilentlyContinue | Format-List Id, StartTime, Responding
```

**Beklenen çıktı:**
```
Id         : <pid>
StartTime  : <yakın tarih>
Responding : True
```

- ❌ **Boş çıktı** → bot ÖLDÜ. Watchdog devreye girmemiş demektir. Bkz. **Emergency #1** (alt taraf).
- ⚠️ **Responding=False** → bot HUNG. `Stop-Process` + relaunch. Bkz. **Emergency #2**.
- ✅ **Responding=True + StartTime yakın (son 24h)** → sağlıklı, devam.

### 2. Heartbeat dosyasının yaşı (≤ 5 dk olmalı):
```powershell
(Get-Item "$env:LOCALAPPDATA\ThatsMyQuant\worker_heartbeat.txt").LastWriteTime
```

- 5 dk'dan eski → watchdog tetiklemiş olmalıydı. Telegram alarmlarına bak.

### 3. OKX UI hızlı bakış
- **Open positions** kaç tane? Doğal seviye: 0-5
- **Floating PnL** toplamı: pozitif mi negatif mi?
- **Maintenance margin ratio**: tüm pozisyonlarda %1000+ olmalı (3-5x leverage'da). %200 altı = liq yakın → acil müdahale.

---

## 📊 İlk 5 dakika — Ne oldu?

### Telegram'ı aç, son 24h'ın özetini gör
- Digest mesajları (4×/gün) → her birinin son satırı (`Aggregate`) net PnL
- KillSwitch alarmları (🚨 emoji) → varsa **kritik**
- Watchdog alarmları → bot kaç kez restart olmuş
- Cleanup alarmları (`✅ Paused-edge cleanup`) → kaç paused edge otomatik kapatılmış

### En son digest'i bul, şu rakamlara bak:
```
Paper Portfolio:
  Cash    : $X,XXX
  Equity  : $X,XXX        ← bu sayı önemli
  Open    : N            ← OKX UI ile eşleşmeli
  Closed  : 54 + N       ← 54 baseline + sonraki canlı fill'ler

Per-Strategy Attribution:
  ...                    ← per-strategy PnL (artık doğru attribution var)

Aggregate:
  Trades  : 54 + N
  WinRate : X%
  PF      : X.XX        ← > 1.0 ise para kazanmış, < 1.0 zarar
```

### Hızlı PnL hesabı
- **Başlangıç**: ~$2,000 basis
- **Bugün**: Equity / 2000 - 1 = net %
- **+%2 üstü iyi**, **-%5 altı** acil bakım (KillSwitch zaten -%8'de tetiklenmeli)

---

## 🔍 İlk 30 dakika — Detaylı doğrulama

### A — Pozisyon attestasyonu
PaperPortfolio'daki Open OKX UI ile eşleşmeli:

```powershell
$port = Get-Content "$env:LOCALAPPDATA\ThatsMyQuant\paper_portfolio.json" | ConvertFrom-Json
$liveOkx = $port.OpenPositions | Where-Object { $_.ExecutionMode -eq 'LIVE_OKX' }
Write-Host "Mirror count: $($liveOkx.Count)"
$liveOkx | Select-Object Symbol, Side, Quantity, EntryPrice, CurrentPrice
```

Mismatch varsa:
- Bot < OKX UI: DbToPortfolioSync sync yapamamış → bot restart
- Bot > OKX UI: orphan mirror → 60s'de düzelir, beklemek yeterli

### B — KillSwitch durumu
```powershell
Get-Content "$env:LOCALAPPDATA\ThatsMyQuant\kill_switch_state.json" | ConvertFrom-Json
```

- `Killed: false` → normal
- `Killed: true` → tetiklenmiş, **YENİ ENTRY YOK**. Sebep `Reason` field'da. Reset için:
  ```
  Telegram: /kill-reset   (eğer TelegramCommandHostedService aktifse)
  Veya: dosyayı sil → restart
  ```

### C — Drought / aktivite kontrolü
```powershell
Get-Content "$env:LOCALAPPDATA\ThatsMyQuant\drought_alert_state.json" | ConvertFrom-Json
```

`LastDroughtAlertUtc` 24h+ eski ise drought yok, bot fill ediyor.

### D — Paused-edge cleanup logları
Telegram'da ara: `✅ Paused-edge cleanup` veya `⚠️ Paused-edge cleanup`
- Her cleanup attempt log'da. Başarılı = pozisyon kapandı. Fail = OKX UI'dan elle kapat.

### E — Per-strategy performans
Son digest'in `Per-Strategy Attribution` bölümü. Her strateji için:
- **N≥5** olanlar anlamlı; N<5 sample noise
- **PF>1.0 + WR>50%** iyi; ikisi de düşük → strategy attention gerek
- **Negative cumulative PnL > -%5** → strategy review

### F — Reconciliation drift
Son 15 günde her gün 23:50 UTC'de fire eden alarm:
- `RECON_24H ... OK` → DB ile OKX uyumlu
- `RECON_24H ... DRIFT-DETECTED` → 15 günden N tane DRIFT varsa **dikkat** (DB Trades hâlâ boş normal, ama volume drift > %5 ise sorun)

---

## 🚨 Top 10 Common Issues + Quick Fixes

### 1. Bot yok / öldü
```powershell
.\Launch-PaperTrade-Clean.ps1 -SkipBuild
```
Logs: `%LOCALAPPDATA%\ThatsMyQuant\worker_logs\worker_<latest>.out.log`

### 2. Bot var ama Pozisyonlar görünmüyor (Open=0)
DbToPortfolioSync down olabilir. Logda ara:
```powershell
Select-String "DbToPortfolioSync" $env:LOCALAPPDATA\ThatsMyQuant\worker_logs\worker_*.out.log | Select-Object -Last 3
```
Yoksa → restart. Varsa → OKX API rate-limit, beklemek yeterli.

### 3. Telegram digest gelmiyor
- Bot çalışıyor mu? (Issue #1)
- 4×/gün cadence (00:00, 06:00, 12:00, 18:00 local). Slot atlanmış mı?
- Telegram BotToken expired olabilir → Launch-PaperTrade-Clean.ps1 env vars'ı kontrol et

### 4. KillSwitch fire etmiş, sistem freeze
1. OKX UI: tüm pozisyon kapalı olmalı (FLATTEN_ALL)
2. Kapanmadıysa elle "Close all"
3. Sebep analizi: kill_switch_state.json `Reason` field
4. Reset: `kill_switch_state.json` sil → worker restart → ARMED state'e döner

### 5. Paused-edge pozisyonu hâlâ açık
Quantlab pause attı ama cleanup fail oldu. Cycle'da `[CLEANUP-FAIL]` log var:
```powershell
Select-String "CLEANUP-FAIL" $env:LOCALAPPDATA\ThatsMyQuant\worker_logs\worker_*.out.log
```
Manuel kapat OKX UI'dan + bir sonraki cycle (1-4h) retry edecek.

### 6. Drawdown -%5'i geçti ama KillSwitch fire etmedi
- threshold -%8, normal. -%5 endişe ama otomatik fire değil.
- Manuel müdahale istiyorsan OKX UI "Close all" + 1-2 gün izle.

### 7. ETHUSDT short hâlâ açık (15 gün önceki)
Bir paused edge. Cleanup deniyor, fail ediyorsa elle kapat (#5).

### 8. Disk dolu
SqliteMaintenance < 2GB free'de Critical alert atar.
```powershell
# WAL dosyası şişmiş olabilir
& sqlite3 quant.db "PRAGMA wal_checkpoint(TRUNCATE);"
# veya bot restart (sqlite maintenance ilk tick'te halleder)
```

### 9. Worker log devasa
```powershell
Get-ChildItem $env:LOCALAPPDATA\ThatsMyQuant\worker_logs\ | Sort-Object Length -Desc | Select -First 5
```
500MB+ → eski log'ları sil. Worker yeniden başlattığında yeni dosya açar.

### 10. "Bot bir şey yapmıyor" hissi
13-gün drought edge'lerin sinyali olmadığı için normal. Aktif edge'ler 4h cycle'da çalışır. Hourly cycle (eth_breakout_1h_long, sol_breakout_1h_long) daha aktif. 24h+ aktivite yoksa:
- Quantlab signal'leri stale mi? `signals/<edge>/latest.json` modification time
- Eğer signal'ler taze ise bot bekliyordur, edge tetiklenmedi. **Normal.**

---

## 🆘 Emergency Procedures

### Emergency #1: Bot ÖLDÜ
```powershell
# Önce kill orphan instance varsa
Stop-Process -Name 'ThatsMyQuant.Bot' -Force -ErrorAction SilentlyContinue
Start-Sleep 3
# Relaunch
cd C:\Users\parlak\Downloads\ThatsMyQuant
.\Launch-PaperTrade-Clean.ps1 -SkipBuild
# 30s bekle, sonra:
Get-Process -Name 'ThatsMyQuant.Bot' | Format-List Id, StartTime, Responding
```

### Emergency #2: Bot HUNG (Responding=False)
```powershell
Stop-Process -Name 'ThatsMyQuant.Bot' -Force
Start-Sleep 5
.\Launch-PaperTrade-Clean.ps1 -SkipBuild
```

### Emergency #3: Tüm OKX pozisyonlarını ACİL KAPAT
**En hızlı**: OKX UI → Positions → "Close all" butonu (sağ üst)

**Bot üzerinden** (eğer çalışıyorsa):
```powershell
# DB'ye __FLATTEN_ALL__ ManualCloseRequest ekle
# (KillSwitch zaten bunu yapar — manuel için aynı yol)
```

### Emergency #4: Build kırıldı, bot başlatılamıyor
```powershell
# Önce eski build'i sil
Remove-Item .\01_Services\QuantEngine.Worker\bin\Release -Recurse -Force
Remove-Item .\01_Services\QuantEngine.Worker\obj -Recurse -Force
# Temiz build
dotnet build .\01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj -c Release
# Sonuç: 0 hata → relaunch (Issue #1)
```

### Emergency #5: SQLite corrupt
```powershell
# Backup al önce
Copy-Item $env:LOCALAPPDATA\ThatsMyQuant\quant.db $env:LOCALAPPDATA\ThatsMyQuant\quant.db.bak_$(Get-Date -F yyyyMMdd)
# Integrity check
& sqlite3 $env:LOCALAPPDATA\ThatsMyQuant\quant.db "PRAGMA integrity_check;"
# Eğer "ok" değilse → restore latest backup:
# Get-ChildItem $env:LOCALAPPDATA\ThatsMyQuant\backups\
```

---

## 📂 Quick Reference

### Önemli dosyalar
| Dosya | İçerik |
|---|---|
| `%LOCALAPPDATA%\ThatsMyQuant\paper_portfolio.json` | Cash, OpenPositions, ClosedPositions snapshot |
| `%LOCALAPPDATA%\ThatsMyQuant\worker_heartbeat.txt` | Watchdog source-of-truth (her 60s update) |
| `%LOCALAPPDATA%\ThatsMyQuant\kill_switch_state.json` | Killed=true/false, Reason, PeakEquity |
| `%LOCALAPPDATA%\ThatsMyQuant\drought_alert_state.json` | Son drought alert timestamp |
| `%LOCALAPPDATA%\ThatsMyQuant\quant.db` | SQLite — Trade table, alerts, telemetry |
| `%LOCALAPPDATA%\ThatsMyQuant\worker_logs\worker_*.out.log` | Detaylı log (~MB/gün) |
| `%LOCALAPPDATA%\ThatsMyQuant\config_snapshots\` | Boot-time config snapshots + index |
| `%LOCALAPPDATA%\ThatsMyQuant\watchdog.log` | Watchdog tick log (her dk) |
| `%LOCALAPPDATA%\ThatsMyQuant\edge_lab_fill_attribution.jsonl` | Per-strategy fill ledger |
| `C:\Users\parlak\Downloads\ThatsMyQuant\Launch-PaperTrade-Clean.ps1` | Worker launcher |
| `C:\Users\parlak\Downloads\ThatsMyQuant\Watchdog-PaperTrade.ps1` | Watchdog (Task Scheduler tetikler) |

### Önemli komutlar
| İş | Komut |
|---|---|
| Bot durumu | `Get-Process -Name 'ThatsMyQuant.Bot'` |
| Son log lines | `Get-Content (gci $env:LOCALAPPDATA\ThatsMyQuant\worker_logs\worker_*.out.log \| sort LastWriteTime \| select -Last 1).FullName -Tail 50` |
| Telegram digest manuel test | (Worker zaten 4×/gün otomatik atıyor) |
| Health snapshot | `cat $env:LOCALAPPDATA\ThatsMyQuant\paper_portfolio.json \| ConvertFrom-Json \| select Cash, @{N='Equity';E={$_.Cash + 0}}, @{N='OpenCount';E={$_.OpenPositions.Count}}` |
| Build | `dotnet build .\01_Services\QuantEngine.Worker\ThatsMyQuant.Bot.csproj -c Release` |
| Stop + restart | `Stop-Process ThatsMyQuant.Bot -Force; .\Launch-PaperTrade-Clean.ps1 -SkipBuild` |

### Alert tag decoder (Telegram'da göreceklerin)
| Tag | Anlam | Aksiyon |
|---|---|---|
| `🚨 KILL-SWITCH FIRED` | -%8 DD veya -%3 daily PnL → flatten all | Critical, manuel check + reset |
| `⚠️ Watchdog: bot restarted` | Bot crash/hang sonrası watchdog restart | Log'a bak, sebep bul |
| `🟢 OPENED Buy/Sell` | Yeni pozisyon açıldı | Normal |
| `✅ Paused-edge cleanup` | Paused edge orphan otomatik kapatıldı | İyi |
| `⚠️ Paused-edge cleanup FAIL` | Cleanup denedi, fill olmadı | Elle kapat |
| `⚠️ Maker fill ratio below threshold` | Post-only kuyruk yarışını kaybediyor | Normal (informational) |
| `⚠️ Daily reconciliation: ledger drift` | DB-OKX uyumsuzluk | İncele, sample bias olabilir |
| `[HB DROUGHT]` | 48h+ yeni trade yok | Normal, sinyal yok demek |
| `OKX_ORPHAN_SUSPECT` | Cancel-verify FSM: order belki orphan | OKX UI Open Orders kontrol |

### Konfig knob'ları (en sık değiştirilecekler)
`01_Services\QuantEngine.Worker\appsettings.Paper.json`:
- `Risk:KillSwitch:DrawdownPct` (default 0.08 = -%8)
- `Risk:KillSwitch:DailyPnlPct` (default 0.03 = -%3)
- `OrderExecution:PostOnlyMode` (true/false)
- `OrderExecution:MakerEdgeBps` (default 0.1 — küçük tut, deep queue olmasın)
- `OrderExecution:SmartRoutingIocEnabled` (true)
- `Telegram:DigestTimes` (default "00:00,06:00,12:00,18:00")

### Quantlab spec dosyaları (referans için)
- `C:\Users\parlak\Downloads\Quantlab\integration\TMQ_PAUSED_EDGES_SPEC.md`
- `C:\Users\parlak\Downloads\Quantlab\signals\_PAUSED_EDGES.json`
- `C:\Users\parlak\Downloads\Quantlab\signals\<edge>\latest.json`

---

## 🧠 Decision tree: ne yapmalıyım?

```
Bot çalışıyor mu?
├─ Hayır → Emergency #1 (Bot öldü)
└─ Evet
    ├─ Equity > $1900? (yani -%5 üstü)
    │   ├─ Evet → Sağlıklı, izle
    │   └─ Hayır
    │       ├─ KillSwitch fire etmiş mi?
    │       │   ├─ Evet → ARMED'e geri al (#4)
    │       │   └─ Hayır → manuel müdahale değerlendir
    │       └─ Per-strategy hangileri zarar?
    │           └─ Quantlab paused listesine eklemeyi düşün
    └─ Açık pozisyonlar OKX UI ile uyumlu mu?
        ├─ Evet → İyi
        └─ Hayır → DbToPortfolioSync log kontrol (Issue #2)
```

---

## ✅ Son kontrol (5 saniye)

Geri döndüğünde Telegram'da en üstte bunu gör → her şey normal:
```
📊 Paper Trade 6h Digest (slot N/4 @ HH:MM)
...
Aggregate:
  Trades  : 54 + X (X yeni trade)
  WinRate : %50+ ise iyi
  PF      : 1.0+ ise iyi
```

Bu üç sayı yeşilse → **30 dakika içinde başka şey yapma**, biraz çay iç. Sonra yukarıdaki detaylı doğrulamayı yap.

---

**Bu runbook 2026-05-16 itibariyle güncel.** Sprint sonrası gelişmeler olursa update et.

**Son not (PhD quant moduyla):** Bot 15 gün boyunca **gözlem** modunda olacak, **kazanç** garantisi değil. N=30 sample bile zor birikecek. Geri döndüğünde "bot para kazanmış mı" sorusundan önce "bot **hayatta** mı, **doğru** mu çalışıyor" sorusunu sor. PnL ikincil.

İyi tatiller. 🌴
