# Prop Firm Research — BEM Funding + alternatives
**Date:** 2026-05-10
**Context:** Operatör 13-strateji crypto stack'i prop firm'a taşıma fizibilitesini sordu.
**Status:** Research-only. **Aksiyon önerisi: ŞU AN ATMA.** Sebep aşağıda.

---

## TL;DR

| Soru | Cevap |
|---|---|
| BEM Funding güvenilir mi? | 🟡 Yellow flag: 2024 kuruluş, regülasyon yok, independent safety score 0/10. TrustPilot olumlu ama küçük sample. |
| Bot/EA izinli mi? | 🟡 Gri alan: terms-of-use'da explicit YOK; prohibited-practices "tick scalping/HFT/3rd-party EA" yasak. Kendi-kodlu non-HFT teorik izinli ama firma istediği zaman "abuse" yorumlayabilir. |
| Bizim 13-strateji stack uyumlu mu? | ❌ HAYIR — Long+Short aynı symbol concurrent yasak ("opposing correlated"); 13 strategy concurrent fire "bulk/mass trading abuse" riski; max single-symbol 1.2% bizim weight (7-11%) ile çakışıyor. |
| Crypto leverage rekabetçi mi? | ❌ BTC/ETH 1:2, alt 1:1.5 — OKX'te 7-10x kullanıyoruz; 4-7x kâr potansiyeli düşüşü |
| Şimdi BEM challenge alalım mı? | ❌ HAYIR — 3 sebep: (1) stack mimarisi BEM kurallarıyla çakışıyor, (2) live OKX demo henüz 0 fill ürettı (edge'lerin çalıştığını kanıtlamadık), (3) BEM henüz 1 yaşında firma — track record yetersiz |
| Daha iyi alternatif var mı? | ✅ FTMO veya FundedNext daha sağlam (regulated, EA explicit, lower fee) — ama hâlâ aynı stack-mimarisi çakışmaları geçerli |
| Prop firm yolu kapalı mı? | ❌ HAYIR — sadece **30 gün erken**. T+30 gün live OKX verisi sonrası **subset stack** (3-4 LONG-only 4h breakout edge) ile FTMO challenge denenebilir |

---

## 1. BEM Funding firma profili

**Kuruluş**: 2024 (1 yaşında)
**Trading platform**: cTrader, DXTrade
**Account size**: $25K - $200K
**Profit split**: 80% (Classic Normal terms-of-use), website "up to 90%" claim
**Payout frequency**: 1-2 working days, biweekly only (sınırlı)
**Funded account**: simulated (firmfunded.com claim — confirm gerek)
**Regulatory**: Lisans yok ([firmfunded.com safety score 0/10](https://firmfunded.com/prop-trading/bem-funding/))
**Crypto symbol count**: 18 ([symbols list](https://bemfunding.com/symbols?market=crypto))
**TrustPilot**: var ama URL 403 verdi; üçüncü-taraf review'larda genel olumlu, sample küçük

**Yellow flags:**
- 1 yaşında firma → market stress periodu görmedi
- Regulated broker arkalığı yok (firmfunded.com claim'i)
- Funded hesaplar simulated → "real money" iddiası şüpheli
- Withdrawals biweekly (operasyonel friksiyon)

---

## 2. BEM model detay tablosu

| Model | P1 Profit | P2 Profit | Daily Loss | Max DD | Trail/Fix | Min Days | Daily Profit Req | Crypto Lev | Profit Split |
|---|---|---|---|---|---|---|---|---|---|
| Classic Normal | 9% | 4.5% | 4.5% | 9% | Fixed | 3 | 0.5% | BTC/ETH 1:2, alt 1:1.5 | 80% |
| One (1-phase) | 9% | n/a | 3% | 5% | Trailing | 3 | 0.5% | 1:1.5 | 80% |
| Ascend (3-phase giveaway) | 6% × 3 | 6% × 3 | 3% | 6% | Fixed | 3 | 0.5% | BTC/ETH 1:1.5, alt 1:1 | n/a |

**Tüm modellerde ortak kural**: Min 3 trading days × 0.5% realized profit per day → bizim signal-driven stratejilerle problem (signal vermeyen günler).

**Max single symbol risk**: 1.2% of initial balance (post-evaluation).

---

## 3. EA / Bot Policy — BEM'de gri alan analizi

**Terms-of-use (Classic Normal/One/Extended)**: EA explicit izin YOK, explicit yasak da YOK → **belirsiz**.

**Prohibited Trading Practices** ([sayfa](https://bemfunding.com/prohibited-trading-practices)) açıkça yasak:
- "EAs designed for tick scalping, latency arbitrage, rollover-price exploitation, or excessive order activity"
- "Third-party EAs where trader does not own source code or where multiple traders execute identical trades"
- "Bulk/Mass Trading Activity – multiple trades opened simultaneously indicating automated execution"
- "Hedging & Opposing Positions on same/correlated symbols"
- "Copy Trading & Trade Coordination"

**Ascend giveaway accounts'ta**: "Any use of automated systems, scripts, bots, emulators, VPNs, proxy services, device masking" EXPLICITLY PROHIBITED.

**Yorum**: kendi-kodlu, non-HFT, tek-trader EA teorik olarak Classic Normal'da izinli görünüyor. Ama:
- Yazılı onay yok
- Terimler firma yorumuna açık
- "Bulk trading" kuralı bizim concurrent fire mimarisi ile çakışıyor

---

## 4. Bizim stack vs BEM rules — uyum matrisi

| Bizim stack özelliği | BEM kuralı | Çakışma |
|---|---|---|
| 13 strateji aynı bar'da concurrent fire edebilir | "Bulk/Mass Trading Activity" yasak | 🔴 KRİTİK — sistematik ban riski |
| eth_breakout_long + eth_breakout_short paralel | "Opposing positions on correlated symbols" yasak | 🔴 KRİTİK — core mimari çakışıyor |
| op_short + arb_short + near_short + ada_short concurrent | "Correlated symbols hedge" yorumlanabilir | 🟡 RİSK |
| Per-strategy weight 7-11% × $2K basis | Max single symbol 1.2% of initial balance | 🔴 KRİTİK — sistematik ihlal |
| OKX'te 7-10x leverage | BEM crypto 1:1.5 - 1:2 | 🟡 Sharpe ~%50 düşer |
| Signal-driven (bazı günler 0 fill) | Min 3 days × 0.5% realized profit | 🟡 Forced manual trading riski |
| 4h breakout, intra-day exit | Hold over weekends crypto'da OK | ✅ Uyumlu |
| Funding rate edge (perp-spesifik) | CFD broker — funding kavramı yok | ❌ Edge ölür |
| Fee panic / stablecoin / rv_breakout (1-4 fire/yr) | 30-day evaluation | ❌ Challenge süresince fire etmez |

**Symbol coverage** (BEM'in 18 crypto'su vs bizim 13 strateji):
- ✅ Var: BTC, ETH, DOGE, BNB, OP, NEAR, ADA (7 sembol → 11 strateji çalışabilir)
- ❌ Yok: ARB → arb_breakout_short BEM'de yapılamaz
- ❌ Funding edges: BEM CFD'de funding rate yok

**Minimum mimari yeniden tasarım** BEM için gerekli olur:
1. Long-only subset (eth_short / op_short / arb_short / near_short / ada_short / eth_breakout_short → YOK)
2. Max 3-4 concurrent strategy (bulk trading riskini düşür)
3. Per-strategy weight ≤1.2% / 13 strateji ≤ ~%0.1 each (Kelly'nin altında — anlamsız sizing)
4. Funding alfa tamamen kaybedilir
5. Event-driven edges (fee_panic/stablecoin/rv_breakout) 30-day window'da fire etmez
6. **Net realistik**: 4-5 LONG-only 4h breakout edge → çok küçük sample → low Sharpe

---

## 5. Alternatif prop firm karşılaştırması

| Firma | Kuruluş | Reg | EA Policy | Profit Target | Daily | Max DD | Profit Split | Crypto | Platform | Fee ($50K) |
|---|---|---|---|---|---|---|---|---|---|---|
| **BEM Funding** | 2024 | None | 🟡 Gri alan | P1 9% / P2 4.5% | 4.5% | 9% fix | 80% | 18 sym, 1:2 | cTrader, DXTrade | n/a |
| **FTMO** | 2014 | EU | ✅ Explicit allow | 10% / 5% | 5% | 10% fix | 90% (95% add-on) | Var (CFD) | MT4/5, cTrader, DXTrade | €345 |
| **FundedNext** | 2022 | UAE | ✅ Allow (cTrader hariç) | 10% / 5% | 3-5% | 6-10% | 95% | Var | MT4/5 | $32-230 |
| **The5%ers** | 2016 | EU | ✅ Allow | 6% (low) | 4% | 10% | 80%→100% | Var | MT5, cTrader | ~$200 |
| **Goat Funded** | 2023 | n/a | ✅ Allow | varies | varies | varies | up to 95% | Var | MT4/5 | varies |
| **E8 Markets** | 2021 | n/a | ✅ Allow | 8% | 4-5% | 8% | 80%→100% | Var | MT4/5, cTrader, MatchTrader | varies |

**Dürüst değerlendirme**:

1. **FTMO** = altın standart prop firm. 12 yıl, regulated EU, EA explicit allowed, en yüksek community trust. Ama profit target 10% (BEM 9%) ve fee %30-50 daha pahalı.

2. **FundedNext** = en ucuz fee, %95 split, 2022'den beri. cTrader'da EA banned (bu bizim için OK çünkü zaten MT5'i tercih edebiliriz).

3. **BEM Funding** = en yeni, en az kanıtlı, en gri EA policy. Tek avantajı: cTrader ile crypto'ya focus.

**Bizim stack için en uygun (BEM dışı)**: FTMO veya FundedNext. Ama yine de mimari çakışma sorunları (bulk trading, opposing correlated) HER prop firm'da var çünkü bunlar industry-standard kural.

---

## 6. Doğru sıralama ve karar matrisi

```
ŞİMDİ                           T+30 gün                    T+60-90 gün
│
├─ 13 edge OKX demo live (yapıldı 2026-05-10)
│   - Sample biriktir (N≥30 per strategy hedef)
│   - Realized Sharpe oku
│   - Maker fill ratio doğrula
│   - Kill switch davranışı gör
│
├──────────────────────────────► VERDICT NOKTASI
                                  │
                                  ├─ Vaka A: 3+ edge ≥0.5 net Sharpe
                                  │   → OPTION 1: OKX real money $500-1000
                                  │   → OPTION 2: FTMO challenge (subset stack)
                                  │       - Long-only 4h breakout subset (4-5 edge)
                                  │       - $25K-50K account ($110-345 fee)
                                  │       - 30-day P1 + 60-day P2 timeline
                                  │       - Pass olasılığı: ~%40-60 (dürüst)
                                  │
                                  └─ Vaka B: <3 edge geçer
                                      → 60 gün uzat, prop firm planı raftan
```

---

## 7. Kararım — şu an YAPMA, ama 3 sebep paralel iş yapılabilir

### Yapılmaması gerekenler (şimdi)

1. ❌ BEM Funding challenge satın alma — 3 critical sebep:
   - Stack mimarisi BEM kurallarıyla yapısal çakışıyor
   - 13 edge'in canlı çalıştığı kanıtlanmadı (0 fill bugün)
   - BEM henüz 1 yaşında firma; FTMO daha güvenli

2. ❌ Bot'u cTrader/MT5'e port etme işi — minimum 2-3 hafta dev work, OKX live data'sını contaminate eder

3. ❌ Quantlab tarafına "BEM için yeniden backtest" iş emri — Quantlab haftada 1 hipotez tempo'sundan sapmasın

### Yapılabilir (şimdi, paralel + zero risk)

1. ✅ **BEM TrustPilot ve Reddit'i derinlemesine takip** — 2 hafta sonra rating, payout report'ları daha güvenilir olabilir
2. ✅ **FTMO + FundedNext FAQ'larını oku** — kurallar netleştir
3. ✅ **Long-only subset stack hazırlama planı** — Quantlab ileride "4h breakout LONG only" subset için ayrı backtest çıktısı verirse hazır olalım
4. ✅ **Bu doc'u 30 gün sonra revisit** — OKX live verisi geldikten sonra karar matrisini yeniden değerlendir

---

## 8. Açık sorular (sonraki research turunda)

1. BEM cTrader symbol commission/spread (sayfa render olmadı)
2. BEM TrustPilot 5-yıldız sample size + son 30 gün trend
3. BEM payout history son 6 ay (TrustPilot manuel review)
4. FTMO crypto symbol listesi tam
5. FundedNext'in MT5'te crypto leverage exact
6. Goat Funded Trader vs Apex (futures route — futures'da bizim crypto stack hiç çalışmaz, ama alternatif yol)

---

## 9. Tek paragraf özet

BEM Funding teorik bir capital amplifier ama **şu an yanlış ve erken adım**: firma yeni (2024), regulated değil, EA policy gri, ve en kritik olarak bizim 13-strateji bidirectional crypto stack mimarisi BEM'in "no bulk trading / no opposing correlated positions / max 1.2% per symbol" kurallarıyla **yapısal olarak çakışıyor**. Long+Short aynı symbol mimarisi ölü, 13 concurrent fire ban riski yaratıyor, weight 7-11% sınırı aşıyor. Funding-rate edge'leri CFD'de zaten ölüyor. Doğru sıralama: **30 gün OKX demo verisi → 3+ edge live N≥30 ile Sharpe ≥0.5 net kanıtla → ondan sonra FTMO** (BEM değil) **challenge'a Long-only subset ile gir**. T-30 günü beklemek karar veren parametredir; firmaya değil.

---

## Sources

- [BEM Funding crypto symbols](https://bemfunding.com/symbols?market=crypto)
- [BEM Classic Normal terms](https://bemfunding.com/terms-of-use/bem-classic-normal)
- [BEM One terms](https://bemfunding.com/terms-of-use/bem-one)
- [BEM Ascend terms](https://bemfunding.com/terms-of-use/bem-ascend)
- [BEM Prohibited Trading Practices](https://bemfunding.com/prohibited-trading-practices)
- [TheTrustedProp BEM Review 2026](https://thetrustedprop.com/prop-firms/bem-funding)
- [FirmFunded BEM Review (safety 0/10)](https://firmfunded.com/prop-trading/bem-funding/)
- [PickMyTrade Algo-Friendly Prop Firms 2026](https://blog.pickmytrade.trade/best-prop-firms-algo-trading-bots-2026/)
- [FundedNext vs FTMO comparison](https://fundednext.com/blog/fundednext-vs-ftmo)
- [Atlas Funded - Prop Firms That Allow EA](https://www.atlasfunded.com/post/prop-firms-that-allow-ea)
