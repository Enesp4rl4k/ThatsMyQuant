// Lightweight indicator math — runs client-side on candle close arrays.

export interface OhlcvCandle {
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export interface VwapPoint {
  vwap: number;
  upper1: number;
  lower1: number;
  upper2: number;
  lower2: number;
}

/**
 * Session VWAP with ±1σ / ±2σ bands.
 * Computed as: cumulative(typical_price * volume) / cumulative(volume)
 * where typical_price = (high + low + close) / 3.
 * Bands use rolling stdev of the price-VWAP deviation.
 */
export function vwap(candles: OhlcvCandle[]): (VwapPoint | null)[] {
  const out: (VwapPoint | null)[] = [];
  let cumTpv = 0;
  let cumVol = 0;
  let cumTpvSq = 0;

  for (let i = 0; i < candles.length; i++) {
    const c = candles[i];
    const tp = (c.high + c.low + c.close) / 3;
    cumTpv += tp * c.volume;
    cumVol += c.volume;
    cumTpvSq += tp * tp * c.volume;

    if (cumVol <= 0) {
      out.push(null);
      continue;
    }
    const v = cumTpv / cumVol;
    // Variance: E[x²] - E[x]²
    const variance = Math.max(0, cumTpvSq / cumVol - v * v);
    const sd = Math.sqrt(variance);
    out.push({
      vwap: v,
      upper1: v + sd,
      lower1: v - sd,
      upper2: v + 2 * sd,
      lower2: v - 2 * sd,
    });
  }
  return out;
}

export interface VolumeProfileBucket {
  priceMin: number;
  priceMax: number;
  priceMid: number;
  volume: number;
  isPoc: boolean;  // Point of Control (highest volume)
  isVa: boolean;   // Inside Value Area (70% of volume)
  vaType?: "vah" | "val" | "mid";
}

/**
 * Volume Profile: distributes volume across price buckets.
 * Returns buckets sorted by price ascending.
 * POC = bucket with highest volume.
 * Value Area = 70% of total volume centered around POC.
 */
export function volumeProfile(
  candles: OhlcvCandle[],
  buckets = 24
): VolumeProfileBucket[] {
  if (candles.length === 0) return [];
  const allHighs = candles.map((c) => c.high);
  const allLows = candles.map((c) => c.low);
  const priceMin = Math.min(...allLows);
  const priceMax = Math.max(...allHighs);
  const range = priceMax - priceMin || 1;
  const bucketSize = range / buckets;

  const vols = new Float64Array(buckets);
  for (const c of candles) {
    // Distribute candle volume proportionally across price range it spans
    const lo = c.low;
    const hi = c.high;
    const spread = hi - lo || bucketSize;
    for (let b = 0; b < buckets; b++) {
      const bLo = priceMin + b * bucketSize;
      const bHi = bLo + bucketSize;
      const overlap = Math.max(0, Math.min(hi, bHi) - Math.max(lo, bLo));
      vols[b] += c.volume * (overlap / spread);
    }
  }

  const totalVol = vols.reduce((s, v) => s + v, 0) || 1;
  const pocIdx = vols.indexOf(Math.max(...vols));

  // Value Area: expand outward from POC until 70% of volume captured
  let vaVol = vols[pocIdx];
  let vaLo = pocIdx;
  let vaHi = pocIdx;
  while (vaVol / totalVol < 0.7 && (vaLo > 0 || vaHi < buckets - 1)) {
    const upVol = vaHi + 1 < buckets ? vols[vaHi + 1] : -Infinity;
    const downVol = vaLo - 1 >= 0 ? vols[vaLo - 1] : -Infinity;
    if (upVol >= downVol) { vaHi++; vaVol += vols[vaHi]; }
    else { vaLo--; vaVol += vols[vaLo]; }
  }

  return Array.from({ length: buckets }, (_, b) => ({
    priceMin: priceMin + b * bucketSize,
    priceMax: priceMin + (b + 1) * bucketSize,
    priceMid: priceMin + (b + 0.5) * bucketSize,
    volume: vols[b],
    isPoc: b === pocIdx,
    isVa: b >= vaLo && b <= vaHi,
    vaType: b === vaHi ? "vah" : b === vaLo ? "val" : undefined,
  }));
}

export function sma(values: number[], period: number): (number | null)[] {
  const out: (number | null)[] = [];
  let sum = 0;
  for (let i = 0; i < values.length; i++) {
    sum += values[i];
    if (i >= period) sum -= values[i - period];
    out.push(i >= period - 1 ? sum / period : null);
  }
  return out;
}

export function ema(values: number[], period: number): (number | null)[] {
  const out: (number | null)[] = [];
  const k = 2 / (period + 1);
  let prev: number | null = null;
  for (let i = 0; i < values.length; i++) {
    if (i < period - 1) {
      out.push(null);
      continue;
    }
    if (prev === null) {
      // seed with simple mean of first `period` values
      let sum = 0;
      for (let j = i - period + 1; j <= i; j++) sum += values[j];
      prev = sum / period;
    } else {
      prev = values[i] * k + prev * (1 - k);
    }
    out.push(prev);
  }
  return out;
}

export interface BollingerPoint {
  upper: number;
  mid: number;
  lower: number;
}

export function bollingerBands(
  values: number[],
  period = 20,
  stdevs = 2
): (BollingerPoint | null)[] {
  const sma20 = sma(values, period);
  const out: (BollingerPoint | null)[] = [];
  for (let i = 0; i < values.length; i++) {
    const m = sma20[i];
    if (m === null) {
      out.push(null);
      continue;
    }
    let sumSq = 0;
    for (let j = i - period + 1; j <= i; j++) {
      const d = values[j] - m;
      sumSq += d * d;
    }
    const sd = Math.sqrt(sumSq / period);
    out.push({
      mid: m,
      upper: m + stdevs * sd,
      lower: m - stdevs * sd,
    });
  }
  return out;
}

/** Wilder's RSI (period default 14). */
export function rsi(values: number[], period = 14): (number | null)[] {
  const out: (number | null)[] = [];
  if (values.length < period + 1) return values.map(() => null);

  let avgGain = 0;
  let avgLoss = 0;
  for (let i = 1; i <= period; i++) {
    const diff = values[i] - values[i - 1];
    if (diff >= 0) avgGain += diff;
    else avgLoss -= diff;
  }
  avgGain /= period;
  avgLoss /= period;

  for (let i = 0; i < values.length; i++) {
    if (i < period) {
      out.push(null);
      continue;
    }
    if (i > period) {
      const diff = values[i] - values[i - 1];
      const gain = diff > 0 ? diff : 0;
      const loss = diff < 0 ? -diff : 0;
      avgGain = (avgGain * (period - 1) + gain) / period;
      avgLoss = (avgLoss * (period - 1) + loss) / period;
    }
    if (avgLoss === 0) out.push(100);
    else {
      const rs = avgGain / avgLoss;
      out.push(100 - 100 / (1 + rs));
    }
  }
  return out;
}

export interface MacdPoint {
  macd: number;
  signal: number;
  histogram: number;
}

/** Standard MACD(12, 26, 9). */
export function macd(
  values: number[],
  fast = 12,
  slow = 26,
  signalPeriod = 9
): (MacdPoint | null)[] {
  const fastEma = ema(values, fast);
  const slowEma = ema(values, slow);
  const macdLine: (number | null)[] = values.map((_, i) => {
    const f = fastEma[i];
    const s = slowEma[i];
    return f != null && s != null ? f - s : null;
  });

  // Signal line = EMA(macd, 9). EMA helper expects raw numbers, so we treat
  // nulls by skipping until we have a clean run.
  const out: (MacdPoint | null)[] = values.map(() => null);
  let signalEma: number | null = null;
  const k = 2 / (signalPeriod + 1);
  let validSeenInARow = 0;
  let seedSum = 0;
  for (let i = 0; i < values.length; i++) {
    const m = macdLine[i];
    if (m == null) {
      validSeenInARow = 0;
      seedSum = 0;
      signalEma = null;
      out[i] = null;
      continue;
    }
    if (signalEma == null) {
      validSeenInARow++;
      seedSum += m;
      if (validSeenInARow === signalPeriod) {
        signalEma = seedSum / signalPeriod;
        out[i] = { macd: m, signal: signalEma, histogram: m - signalEma };
      } else {
        out[i] = null;
      }
    } else {
      signalEma = m * k + signalEma * (1 - k);
      out[i] = { macd: m, signal: signalEma, histogram: m - signalEma };
    }
  }
  return out;
}
