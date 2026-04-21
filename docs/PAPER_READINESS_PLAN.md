# ThatsMyQuant - Paper Readiness Plan

Bu dokumanin amaci, projeyi "feature ekleyen arastirma reposu" seviyesinden "paper oncesi kendini kanitlayan sistem" seviyesine tasimaktir.

## Current Stage

Su an dogru yerdeyiz:
- canliya degil, once backtestte kendini ispatlamak zorunda olan bir research engine
- execution, lifecycle, telemetry ve risk tarafinda ciddi ilerleme kaydedilmis durumda
- asil eksik "daha fazla feature" degil, acimasiz olcum ve gecis kapisi disiplini

## Core Thesis

Bir strateji veya bot, paper'a ancak su dort seyi ayni anda sagliyorsa cikabilmeli:
- net-of-cost ve net-of-slippage performans istikrari
- farkli sembol ve donemlerde benzer davranis
- out-of-sample ve walk-forward dayanikliligi
- zararli senaryolarda sermaye koruma ve exit disiplini

## Phase 1 - Backtest Engine Integrity

Amac: backtest motorunun ambient state ve test-yaniltici davranislardan arinmasi.

- `BacktestEngineApp` icindeki kalan legacy fallback state tamamen kaldirilacak.
- Tum run configuration sadece scoped execution settings uzerinden akacak.
- HTF context, leverage, HFT mode, timeframe, risk profile ve cost model ambient field degil run-scoped state olacak.
- Backtest sonucunu etkileyen tum gate ve overlays deterministic hale getirilecek.

Done definition:
- ayni engine instance'i ile ard arda ve paralel kosular birbirini etkilemeyecek
- state leakage regression testleri kalici olacak
- ambient mutable fallback alanlari kaldirilmis veya sadece constructor snapshot seviyesine indirgenmis olacak

## Phase 2 - Acceptance Gate

Amac: "iyi gorunen" degil, paper'a cikmaya hak kazanan sonucu tanimlamak.

Minimum acceptance gate:
- minimum trade count
- maximum drawdown ceiling
- Sharpe floor
- Profit Factor floor
- PBO ceiling
- out-of-sample stability floor
- net expectancy positive
- average RR floor

Ilk onerilen gate taslagi:
- `Trades >= 80`
- `MaxDrawdownPercent <= 12`
- `SharpeRatio >= 1.10`
- `ProfitFactor >= 1.25`
- `AverageRR >= 1.10`
- `PBO <= 0.35`
- `OutOfSample expectancy > 0`

Not:
- Bunlar son rakam degil; ilk institutional guardrail taslagi.
- Stratejiye gore ayarlanabilir ama gevsetilmesi icin yazili gerekce gerekmeli.

## Phase 3 - Portfolio-Style Validation

Amac: tek bir hero sonucu degil, tasinabilir edge aramak.

Bir strateji paper'a su kosullar olmadan cikmamali:
- en az 4-6 sembolde calisabilmeli
- en az 3 farkli tarih penceresinde test edilmeli
- tek bir market fazina bagimli olmamali
- sembol bazli sonuclar toplu sonucu tasiyor olmali; sadece tek sembol sonucu toplam PnL'yi surtuklememeli

Beklenen ciktilar:
- symbol-wise PnL
- regime-wise PnL
- period-wise PnL
- pooled Sharpe
- weakest bucket summary

## Phase 4 - Cost Realism Hardening

Amac: backtestte gorulen edge'in execution gercegine dayanmayan sahte bir edge olmamasini saglamak.

Sertlestirilecek alanlar:
- venue spread realism
- slippage realism
- maker fill discipline
- latency stress
- partial fill assumptions
- exit-side cost leakage

Olculmesi gereken metrikler:
- gross vs net PnL
- cost leakage
- signal price vs fill price drift
- entry slip bucketleri
- exit slip bucketleri
- maker no-fill rejection rate

## Phase 5 - Position Management Research Track

Amac: alpha ile position management'i ayri ayri olcmek.

Ayrica arastirilacak:
- partial exits
- breakeven logic
- trailing stop behavior
- stress exits
- time stop behavior

Beklenen arastirma formati:
- A/B test tablosu
- her policy icin trade count
- expectancy
- PF
- max drawdown
- average winner / average loser
- cost leakage contribution

Kural:
- "daha iyi hissettiriyor" yeterli degil
- her exit policy degisikligi net olarak olumlu bir metrik izi birakmali

## Phase 6 - Paper Readiness Dashboard

Amac: paper'a cikis kararini dashboard uzerinden okunabilir hale getirmek.

Dashboard'da gorunmesi gereken metrikler:
- win rate
- avg RR
- expectancy
- Profit Factor
- Sharpe
- Sortino
- max DD
- trade count
- regime-wise PnL
- symbol-wise PnL
- cost leakage
- weakest bucket
- live-vs-backtest drift placeholders

## Phase 7 - Promotion Enforcement

Amac: paper'a gecisin manuel hisle degil sistem kuraliyla olmasi.

Kurallar:
- acceptance gate gecmeyen strateji paper'a cikamaz
- paper sonucu yetersizse LiveProbe'a cikamaz
- promotion logic sadece PnL degil risk ve execution metriklerini de dikkate almali

Sistem ciktisi:
- pass/fail
- hangi gate'in kaldigi
- hangi bucket'in zayif oldugu
- bir sonraki iyilestirme alani

## Phase 8 - Paper Phase Objective

Paper fazinin amaci "kar aramak" degil, execution truth discovery olmalidir.

Paper'da cevaplanacak sorular:
- signal-to-fill drift ne kadar?
- live slippage drift ne kadar?
- exit behavior backtestten ne kadar sapti?
- infra stability yeterli mi?
- lifecycle ve sync logic gercek akista tutarli mi?

Paper basari olcutu:
- backtestte beklenen davranis paper'da ciddi sapma gostermiyor
- execution kaynakli edge erimesi kabul edilebilir seviyede
- risk ve lifecycle akisi sessizce bozulmuyor

## Immediate Next Moves

1. `BacktestEngineApp` kalan fallback state temizligi
2. acceptance gate modelini kod seviyesinde tanimlama
3. symbol/regime/period bazli validation raporu uretme
4. cost leakage dashboard alanlarini genisletme
5. position-management A/B matrix taslagi

## Honest Project View

Bu repo oyuncak seviyesinde degil.

Guclu taraflar:
- risk ve lifecycle tarafinda giderek kurumsal bir iskelet olusuyor
- telemetry ve audit izi ciddi sekilde gucleniyor
- event-driven ve backtest hardening dogru yone gidiyor

Zayif taraflar:
- bizi ikna edecek kadar guclu, tekrarli, net-of-cost sonuc henuz yok
- strategy edge ile execution gercekligi arasindaki kopru henuz tam kanitlanmadi
- paper'a cikis kurali henuz sistemde enforce edilmiyor

Sonuc:
- basarili pozisyon yoneten bir bot olma ihtimali gercek
- karlı bot olma ihtimali de var
- ama bu ancak bundan sonra ozellikle olcum, disiplin ve promotion kapilariyla mumkun olacak
