import { useMemo, useRef } from "react";
import { vwap as calcVwap, volumeProfile as calcVolumeProfile } from "@/lib/indicators";
import { useChartSync } from "@/lib/chartSync";

interface Candle {
  time: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

interface OverlayLine {
  price: number;
  color: string;
  label: string;
  dash?: boolean;
}

/** A line series traced through the candles (e.g. moving average). */
export interface IndicatorLine {
  values: (number | null)[];
  color: string;
  width?: number;
  dash?: boolean;
  label?: string;
}

/** Filled band between two series (e.g. Bollinger). */
export interface IndicatorBand {
  upper: (number | null)[];
  lower: (number | null)[];
  fill: string;
  strokeColor?: string;
  label?: string;
}

/** Risk/reward zone for an open position — translucent rectangle between two prices.
 *  Use for entry→SL (loss zone) and entry→TP (profit zone) so the operator sees the
 *  asymmetric payoff visually overlaid on the chart. */
export interface RiskRewardZone {
  /** Price boundary 1. Order doesn't matter — component sorts. */
  priceA: number;
  /** Price boundary 2. */
  priceB: number;
  /** Translucent fill (e.g. "rgba(34,197,94,0.10)"). */
  fill: string;
  /** Optional inline label rendered on the right edge. */
  label?: string;
  /** Optional border colour (defaults to fill). */
  stroke?: string;
}

/** Two-anchor trendline. Anchors are ISO timestamps; nearest candle is used. */
export interface TrendlineSegment {
  fromTime: string;
  fromPrice: number;
  toTime: string;
  toPrice: number;
  color: string;
  label?: string;
}

/**
 * Trade-mode marker — arrow / dot drawn on top of the candle nearest to `time`.
 *
 *   long  entry  → filled ▲ below the candle (green / glow)
 *   short entry  → filled ▼ above the candle (red / glow)
 *   long  pending → hollow △ (dashed outline)
 *   short pending → hollow ▽
 *   exit  win/loss → small dot at exit price (PnL color)
 */
export interface ChartAnnotation {
  time: string;        // ISO; matched to the closest candle by timestamp
  price: number;       // vertical anchor for exits / horizontal lines
  side: "long" | "short";
  kind: "entry" | "exit" | "pending" | "mfe" | "mae";
  /** For exits: paints the dot green (win) / rose (loss) / amber (open). */
  status?: "win" | "loss" | "open";
  /** Tooltip text shown via SVG <title>. */
  label?: string;
  /** When true, the marker pulses (used for entries opened in the last few minutes). */
  fresh?: boolean;
}

interface Props {
  candles: Candle[];
  overlays?: OverlayLine[];
  /** Indicator line series — drawn after candles, before horizontal overlays. */
  lines?: IndicatorLine[];
  /** Indicator bands (Bollinger etc) — drawn under everything else. */
  bands?: IndicatorBand[];
  /** User-drawn trendlines (two anchors). */
  trendlines?: TrendlineSegment[];
  /** Trade-mode markers (entry / exit / pending arrows). */
  annotations?: ChartAnnotation[];
  /** Live price marker — pulsing dot at right edge + dashed horizontal line. */
  livePrice?: number;
  /** Subtle background tint for current market regime. */
  regimeTint?: "trending-up" | "trending-down" | "ranging" | "volatile" | null;
  /** Order-book liquidity bands — horizontal coloured stripes at major bid/ask price levels. */
  liquidityBands?: LiquidityBand[];
  /** Risk/reward zones — translucent rectangles between two prices (entry→SL, entry→TP).
   *  Drawn under candles so wicks remain crisp on top. */
  riskRewardZones?: RiskRewardZone[];
  /** Click handler — receives nearest-candle time + the price under the cursor. */
  onChartClick?: (time: string, price: number) => void;
  /** Visual cursor mode — affects mouse style + hint colour. */
  drawingMode?: "none" | "horizontal" | "trendline" | "alert";
  /** Pending trendline anchor (live preview while user picks the second point). */
  pendingAnchor?: { time: string; price: number } | null;
  width?: number;
  height?: number;
  entryTime?: string;
  /** Compact mode strips paddings/labels for mini popouts. */
  compact?: boolean;
  /** Show volume sub-pane below price chart. */
  showVolume?: boolean;
  /** Show VWAP line + ±1σ/2σ bands. */
  showVwap?: boolean;
  /** Show Volume Profile histogram on the right edge. */
  showVolumeProfile?: boolean;
  /** Show price/time gridlines (high-contrast). When false, only the axis labels render. */
  showGrid?: boolean;
  /** External crosshair bar index (from ChartSyncContext). */
  syncedBarIndex?: number | null;
}

export interface LiquidityBand {
  price: number;
  /** Normalised intensity (0-1) — drives opacity. */
  intensity: number;
  side: "bid" | "ask";
}

export function CandleChart({
  candles,
  overlays = [],
  lines = [],
  bands = [],
  trendlines = [],
  annotations = [],
  livePrice,
  regimeTint = null,
  liquidityBands = [],
  riskRewardZones = [],
  onChartClick,
  drawingMode = "none",
  pendingAnchor = null,
  width = 680,
  height = 300,
  entryTime,
  compact = false,
  showVolume = false,
  showVwap = false,
  showVolumeProfile = false,
  showGrid = true,
  syncedBarIndex,
}: Props) {
  const sync = useChartSync();
  const svgRef = useRef<SVGSVGElement>(null);
  const PAD = compact
    ? { top: 4, right: 4, bottom: 4, left: 4 }
    : { top: 12, right: 8, bottom: 28, left: 60 };

  const vpW = showVolumeProfile ? 40 : 0; // width reserved for volume profile sidebar
  const volPaneH = showVolume ? Math.round(height * 0.18) : 0;
  const volGap = showVolume ? 6 : 0;

  if (candles.length === 0) {
    return (
      <div
        style={{ width, height }}
        className="flex items-center justify-center text-xs italic font-serif text-muted-foreground"
      >
        — no candle data —
      </div>
    );
  }

  const innerW = width - PAD.left - PAD.right - vpW;
  const priceH = height - PAD.top - PAD.bottom - volPaneH - volGap;

  const allPrices = candles.flatMap((c) => [c.high, c.low]);
  const overlayPrices = overlays.map((o) => o.price).filter((p) => p > 0);
  const indicatorPrices: number[] = [];
  for (const ln of lines) for (const v of ln.values) if (v != null) indicatorPrices.push(v);
  for (const b of bands) {
    for (const v of b.upper) if (v != null) indicatorPrices.push(v);
    for (const v of b.lower) if (v != null) indicatorPrices.push(v);
  }
  const priceMin = Math.min(...allPrices, ...overlayPrices, ...indicatorPrices) * 0.9995;
  const priceMax = Math.max(...allPrices, ...overlayPrices, ...indicatorPrices) * 1.0005;
  const priceRange = priceMax - priceMin || 1;

  const n = candles.length;
  const candleW = Math.max(2, (innerW / n) * 0.7);
  const step = innerW / n;

  const scaleY = (p: number) => PAD.top + priceH * (1 - (p - priceMin) / priceRange);
  const scaleX = (i: number) => PAD.left + i * step + step / 2;

  // Volume scaling
  const volMax = showVolume ? Math.max(...candles.map((c) => c.volume), 1) : 1;
  const volTop = PAD.top + priceH + volGap;
  const volScaleY = (v: number) => volTop + volPaneH * (1 - v / volMax);

  const entryX = entryTime
    ? (() => {
        const idx = candles.findIndex((c) => c.time >= entryTime);
        return idx >= 0 ? scaleX(idx) : null;
      })()
    : null;

  const yTickCount = compact ? 0 : 5;
  const yTicks = Array.from({ length: yTickCount }, (_, i) => {
    const price = priceMin + (priceRange * i) / Math.max(1, yTickCount - 1);
    return { price, y: scaleY(price) };
  });

  // VWAP series
  // eslint-disable-next-line react-hooks/rules-of-hooks
  const vwapSeries = useMemo(
    () => (showVwap ? calcVwap(candles) : []),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [showVwap, candles]
  );

  // Volume Profile buckets
  // eslint-disable-next-line react-hooks/rules-of-hooks
  const vpBuckets = useMemo(
    () => (showVolumeProfile ? calcVolumeProfile(candles, 24) : []),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [showVolumeProfile, candles]
  );
  const vpMaxVol = vpBuckets.length > 0 ? Math.max(...vpBuckets.map((b) => b.volume), 1) : 1;

  const greenFill = "#3B8C68";
  const redFill = "#C9485F";
  const greenStroke = "#2D6A4F";
  const redStroke = "#B5364B";

  // Crosshair bar index — prefer synced context, then local hover
  const crosshairIdx = syncedBarIndex ?? sync?.hoveredIndex ?? null;

  // X-axis labels — first/mid/last
  const fmtTime = (iso: string) => {
    try {
      const d = new Date(iso);
      const now = new Date();
      const sameDay =
        d.getUTCFullYear() === now.getUTCFullYear() &&
        d.getUTCMonth() === now.getUTCMonth() &&
        d.getUTCDate() === now.getUTCDate();
      return sameDay
        ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
        : d.toLocaleDateString([], { month: "short", day: "numeric" });
    } catch {
      return "";
    }
  };

  return (
    <svg
      ref={svgRef}
      width={width}
      height={height}
      style={{ display: "block", overflow: "visible" }}
      className="font-mono"
      onMouseMove={(e) => {
        if (!sync) return;
        const rect = svgRef.current?.getBoundingClientRect();
        if (!rect) return;
        const mx = e.clientX - rect.left - PAD.left;
        const idx = Math.max(0, Math.min(candles.length - 1, Math.floor(mx / step)));
        sync.setHoveredIndex(idx);
      }}
      onMouseLeave={() => sync?.setHoveredIndex(null)}
    >
      {/* Risk/reward zones — render UNDER liquidity bands so the open-position
          payoff geometry is visible without obscuring depth or candles. */}
      {riskRewardZones.length > 0 && riskRewardZones.map((z, i) => {
        const top    = Math.max(z.priceA, z.priceB);
        const bottom = Math.min(z.priceA, z.priceB);
        if (top <= priceMin || bottom >= priceMax) return null;
        const yTop = scaleY(Math.min(top, priceMax));
        const yBot = scaleY(Math.max(bottom, priceMin));
        return (
          <g key={`rrz-${i}`} pointerEvents="none">
            <rect
              x={PAD.left}
              y={yTop}
              width={innerW}
              height={Math.max(1, yBot - yTop)}
              fill={z.fill}
              stroke={z.stroke ?? "transparent"}
              strokeWidth={1}
              strokeDasharray="3 3"
            />
            {z.label && (
              <text
                x={PAD.left + innerW - 6}
                y={yTop + 11}
                textAnchor="end"
                fontSize={9}
                fontFamily="ui-monospace, monospace"
                fill={z.stroke ?? "rgba(255,255,255,0.65)"}
                opacity={0.85}
              >
                {z.label}
              </text>
            )}
          </g>
        );
      })}

      {/* Liquidity heatmap — bid/ask depth bands from live order book.
          Each level renders as a translucent rectangle whose height is proportional
          to the price-tick spacing of the level above/below it (so deeper books
          show as thicker bands). Right-edge gets a size label for the top-N levels
          so the operator can read absolute depth without the order book panel open. */}
      {liquidityBands.length > 0 && (() => {
        const visible = liquidityBands
          .filter(b => b.price > priceMin && b.price < priceMax)
          .sort((a, b) => b.price - a.price); // top → bottom

        // Compute per-level band thickness from neighbour spacing so dense areas
        // get tighter bands and sparse areas get fatter ones (depth-of-book feel).
        const heights = visible.map((b, i) => {
          const prev = visible[i - 1]?.price ?? b.price + (priceRange * 0.005);
          const next = visible[i + 1]?.price ?? b.price - (priceRange * 0.005);
          const above = scaleY(b.price) - scaleY(prev);
          const below = scaleY(next) - scaleY(b.price);
          return Math.max(2, Math.min(10, (Math.abs(above) + Math.abs(below)) / 2));
        });

        // Top-3 by intensity per side get a depth label on the right edge
        const bidsRanked = [...visible].filter(b => b.side === "bid").sort((a, b) => b.intensity - a.intensity).slice(0, 3);
        const asksRanked = [...visible].filter(b => b.side === "ask").sort((a, b) => b.intensity - a.intensity).slice(0, 3);
        const labelSet = new Set([...bidsRanked, ...asksRanked]);

        return (
          <g pointerEvents="none">
            {visible.map((b, i) => {
              const y = scaleY(b.price);
              const h = heights[i];
              const opacity = Math.max(0.10, Math.min(0.55, b.intensity * 0.6 + 0.05));
              const fill = b.side === "bid" ? "34,197,94" : "240,90,90";
              return (
                <rect
                  key={`liq-${i}`}
                  x={PAD.left}
                  y={y - h / 2}
                  width={innerW}
                  height={h}
                  fill={`rgba(${fill},${opacity.toFixed(3)})`}
                />
              );
            })}
            {/* Right-edge depth labels for the top-N levels */}
            {visible.map((b, i) => {
              if (!labelSet.has(b)) return null;
              const y = scaleY(b.price);
              const fill = b.side === "bid" ? "rgb(74,222,128)" : "rgb(252,165,165)";
              const sizeText = b.intensity >= 0.5 ? `${(b.intensity * 100).toFixed(0)}%` : `${(b.intensity * 100).toFixed(1)}%`;
              return (
                <text
                  key={`liq-lbl-${i}`}
                  x={PAD.left + innerW - 4}
                  y={y + 3}
                  textAnchor="end"
                  fontSize={9}
                  fontFamily="ui-monospace, monospace"
                  fill={fill}
                  opacity={0.85}
                >
                  {sizeText}
                </text>
              );
            })}
          </g>
        );
      })()}

      {/* Subtle regime background tint — applied to the whole price pane */}
      {regimeTint && (() => {
        const tintColor =
          regimeTint === "trending-up"   ? "rgba(34,197,94,0.04)"  :
          regimeTint === "trending-down" ? "rgba(240,90,90,0.04)"  :
          regimeTint === "volatile"      ? "rgba(245,158,11,0.05)" :
                                           "rgba(14,165,233,0.03)"; // ranging
        return (
          <rect
            x={PAD.left} y={PAD.top}
            width={innerW + vpW} height={priceH}
            fill={tintColor}
            pointerEvents="none"
          />
        );
      })()}

      {/* Y-axis grid + labels (price). Grid lines only render when showGrid=true; axis ticks
          + price labels always render so the operator can read price levels. */}
      {yTicks.map((t, i) => (
        <g key={i}>
          {showGrid && (
            <line
              x1={PAD.left}
              x2={PAD.left + innerW}
              y1={t.y}
              y2={t.y}
              stroke="rgba(31,30,27,0.10)"
              strokeWidth={1}
              strokeDasharray={i === 0 || i === yTicks.length - 1 ? "" : "2 4"}
              className="dark:stroke-[rgba(255,250,235,0.14)]"
            />
          )}
          {/* Tick mark on the price axis */}
          <line
            x1={PAD.left - 4}
            x2={PAD.left}
            y1={t.y}
            y2={t.y}
            stroke="rgba(255,250,235,0.55)"
            className="[html:not(.dark)_&]:stroke-[rgba(31,30,27,0.55)]"
          />
          <text
            x={PAD.left - 8}
            y={t.y + 3}
            textAnchor="end"
            fill="rgba(255,250,235,0.92)"
            fontSize={10}
            fontFamily="ui-monospace, monospace"
            className="[html:not(.dark)_&]:fill-[rgba(31,30,27,0.92)]"
          >
            {t.price.toFixed(t.price > 1000 ? 0 : t.price > 1 ? 2 : 4)}
          </text>
        </g>
      ))}
      {/* Y-axis title (price) — vertical text on the left */}
      {!compact && yTicks.length > 0 && (
        <text
          x={10}
          y={PAD.top + priceH / 2}
          textAnchor="middle"
          fontSize={7}
          fill="rgba(255,250,235,0.3)"
          className="dark:fill-[rgba(255,250,235,0.3)] [html:not(.dark)_&]:fill-[rgba(31,30,27,0.4)] uppercase tracking-widest"
          transform={`rotate(-90, 10, ${PAD.top + priceH / 2})`}
        >
          price (USD)
        </text>
      )}

      {/* Entry-time vertical bar */}
      {entryX !== null && (
        <line
          x1={entryX}
          x2={entryX}
          y1={PAD.top}
          y2={PAD.top + priceH}
          stroke="#1E3A8A"
          strokeWidth={1}
          strokeDasharray="3 3"
          opacity={0.5}
        />
      )}

      {/* Indicator bands (under candles — Bollinger, Keltner, etc) */}
      {bands.map((band, bi) => {
        const upperPath = pathFromValues(band.upper, scaleX, scaleY);
        const lowerPath = pathFromValues(band.lower, scaleX, scaleY);
        // Build a closed area path from upper (forward) → lower (reversed)
        const upperPts = band.upper
          .map((v, i) => (v == null ? null : `${scaleX(i).toFixed(2)},${scaleY(v).toFixed(2)}`))
          .filter((p): p is string => p !== null);
        const lowerPts = band.lower
          .map((v, i) => (v == null ? null : `${scaleX(i).toFixed(2)},${scaleY(v).toFixed(2)}`))
          .filter((p): p is string => p !== null)
          .reverse();
        const areaPath =
          upperPts.length > 0 && lowerPts.length > 0
            ? `M ${upperPts.join(" L ")} L ${lowerPts.join(" L ")} Z`
            : null;
        return (
          <g key={`band-${bi}`}>
            {areaPath && <path d={areaPath} fill={band.fill} opacity={0.18} />}
            {band.strokeColor && upperPath && (
              <path
                d={upperPath}
                fill="none"
                stroke={band.strokeColor}
                strokeWidth={1}
                strokeOpacity={0.55}
              />
            )}
            {band.strokeColor && lowerPath && (
              <path
                d={lowerPath}
                fill="none"
                stroke={band.strokeColor}
                strokeWidth={1}
                strokeOpacity={0.55}
              />
            )}
          </g>
        );
      })}

      {/* Candles */}
      {candles.map((c, i) => {
        const x = scaleX(i);
        const isGreen = c.close >= c.open;
        const fill = isGreen ? greenFill : redFill;
        const stroke = isGreen ? greenStroke : redStroke;
        const bodyTop = scaleY(Math.max(c.open, c.close));
        const bodyBot = scaleY(Math.min(c.open, c.close));
        const bodyH = Math.max(1, bodyBot - bodyTop);

        return (
          <g key={i}>
            {/* Wick */}
            <line
              x1={x}
              x2={x}
              y1={scaleY(c.high)}
              y2={scaleY(c.low)}
              stroke={stroke}
              strokeWidth={1}
            />
            {/* Body */}
            <rect
              x={x - candleW / 2}
              y={bodyTop}
              width={candleW}
              height={bodyH}
              fill={fill}
              opacity={0.92}
            />

            {/* Volume bar — color by VWAP bias when available */}
            {showVolume && (
              <rect
                x={x - candleW / 2}
                y={volScaleY(c.volume)}
                width={candleW}
                height={volTop + volPaneH - volScaleY(c.volume)}
                fill={
                  showVwap && vwapSeries[i] != null
                    ? c.close >= vwapSeries[i]!.vwap ? "#3B8C68" : "#C9485F"
                    : fill
                }
                opacity={0.45}
              />
            )}
          </g>
        );
      })}

      {/* Volume pane separator */}
      {showVolume && (
        <line
          x1={PAD.left}
          x2={PAD.left + innerW}
          y1={volTop}
          y2={volTop}
          stroke="rgba(31,30,27,0.08)"
          strokeWidth={0.5}
          className="dark:stroke-[rgba(255,250,235,0.08)]"
        />
      )}

      {/* Indicator line series (MA20, MA50, EMA21, etc) */}
      {lines.map((ln, li) => {
        const d = pathFromValues(ln.values, scaleX, scaleY);
        if (!d) return null;
        return (
          <path
            key={`ind-${li}`}
            d={d}
            fill="none"
            stroke={ln.color}
            strokeWidth={ln.width ?? 1.4}
            strokeDasharray={ln.dash ? "4 3" : undefined}
            strokeLinejoin="round"
            strokeLinecap="round"
            opacity={0.9}
          />
        );
      })}

      {/* Trendlines (user-drawn) */}
      {trendlines.map((tl, i) => {
        const fromIdx = candles.findIndex((c) => c.time >= tl.fromTime);
        const toIdx = candles.findIndex((c) => c.time >= tl.toTime);
        if (fromIdx < 0 || toIdx < 0) return null;
        const x1 = scaleX(fromIdx);
        const y1 = scaleY(tl.fromPrice);
        const x2 = scaleX(toIdx);
        const y2 = scaleY(tl.toPrice);
        return (
          <g key={`tl-${i}`}>
            <line
              x1={x1}
              y1={y1}
              x2={x2}
              y2={y2}
              stroke={tl.color}
              strokeWidth={1.6}
              strokeOpacity={0.85}
              strokeLinecap="round"
            />
            <circle cx={x1} cy={y1} r={3} fill="white" stroke={tl.color} strokeWidth={1.5} />
            <circle cx={x2} cy={y2} r={3} fill="white" stroke={tl.color} strokeWidth={1.5} />
          </g>
        );
      })}

      {/* Pending trendline preview (first anchor placed) */}
      {pendingAnchor && drawingMode === "trendline" && (() => {
        const idx = candles.findIndex((c) => c.time >= pendingAnchor.time);
        if (idx < 0) return null;
        const x = scaleX(idx);
        const y = scaleY(pendingAnchor.price);
        return (
          <g>
            <circle cx={x} cy={y} r={5} fill="rgba(30,58,138,0.2)" stroke="#1E3A8A" strokeWidth={2} />
            <text x={x + 8} y={y - 6} fontSize={9} fill="#1E3A8A" fontFamily="Newsreader, serif" fontStyle="italic">
              start anchor
            </text>
          </g>
        );
      })()}

      {/* Trade-mode annotations: entry arrows, exit dots, pending hollow arrows */}
      {annotations.map((ann, i) => {
        // Find nearest candle by timestamp (uses sorted-time invariant)
        let idx = candles.findIndex((c) => c.time >= ann.time);
        if (idx < 0) idx = candles.length - 1;
        if (idx >= n) idx = n - 1;
        const c = candles[idx];
        if (!c) return null;
        const cx = scaleX(idx);
        const isLong = ann.side === "long";

        // Color logic
        const longColor  = "#22C55E"; // brighter green for visibility on candles
        const shortColor = "#EF4444";
        const winColor   = "#22C55E";
        const lossColor  = "#F05A5A";
        const openColor  = "#F59E0B";

        if (ann.kind === "exit") {
          // Small filled circle at exit price; color by win/loss/open status
          const cy    = scaleY(ann.price);
          const color = ann.status === "win"  ? winColor
                      : ann.status === "loss" ? lossColor
                                              : openColor;
          return (
            <g key={`ann-${i}`}>
              <circle cx={cx} cy={cy} r={3.5} fill={color} stroke="#000" strokeWidth={0.7} />
              {ann.label && <title>{ann.label}</title>}
            </g>
          );
        }

        if (ann.kind === "mfe" || ann.kind === "mae") {
          // Diamond marker at the running max-favorable / max-adverse price.
          const cy    = scaleY(ann.price);
          const color = ann.kind === "mfe" ? "#FBBF24" /* amber-400 */ : "#A78BFA" /* violet-400 */;
          const s = 5; // half-side
          return (
            <g key={`ann-${i}`}>
              <polygon
                points={`${cx},${cy - s} ${cx + s},${cy} ${cx},${cy + s} ${cx - s},${cy}`}
                fill="transparent"
                stroke={color}
                strokeWidth={1.2}
                opacity={0.9}
              />
              {/* Inner dot */}
              <circle cx={cx} cy={cy} r={1.5} fill={color} />
              {ann.label && <title>{ann.label}</title>}
            </g>
          );
        }

        // Entry / pending → triangle arrow
        const isPending = ann.kind === "pending";
        const color = isLong ? longColor : shortColor;
        const arrowH = 9;
        const arrowW = 7;
        // Long entry below candle low; short entry above candle high
        const anchorY = isLong ? scaleY(c.low) + 6 : scaleY(c.high) - 6;
        const tip     = isLong ? anchorY + arrowH : anchorY - arrowH;
        const points  = isLong
          ? `${cx},${anchorY} ${cx - arrowW},${tip} ${cx + arrowW},${tip}`
          : `${cx},${anchorY} ${cx - arrowW},${tip} ${cx + arrowW},${tip}`;

        return (
          <g key={`ann-${i}`}>
            <polygon
              points={points}
              fill={isPending ? "transparent" : color}
              stroke={color}
              strokeWidth={isPending ? 1.3 : 1}
              strokeDasharray={isPending ? "2 2" : undefined}
              opacity={isPending ? 0.85 : 0.95}
              style={!isPending ? { filter: `drop-shadow(0 0 3px ${color}66)` } : undefined}
            />
            {/* Fresh entry pulse — outer ring that scales + fades */}
            {ann.fresh && !isPending && (
              <circle
                cx={cx}
                cy={(anchorY + tip) / 2}
                r={6}
                fill="none"
                stroke={color}
                strokeWidth={1.2}
                opacity={0.7}
              >
                <animate attributeName="r"       values="6;14;6" dur="1.8s" repeatCount="indefinite" />
                <animate attributeName="opacity" values="0.7;0;0.7" dur="1.8s" repeatCount="indefinite" />
              </circle>
            )}
            {ann.label && <title>{ann.label}</title>}
          </g>
        );
      })}

      {/* Overlay lines (Entry / SL / TP) */}
      {overlays
        .filter((o) => o.price > 0)
        .map((o, i) => {
          const y = scaleY(o.price);
          return (
            <g key={i}>
              <line
                x1={PAD.left}
                x2={PAD.left + innerW}
                y1={y}
                y2={y}
                stroke={o.color}
                strokeWidth={1.4}
                strokeDasharray={o.dash ? "5 3" : undefined}
                opacity={0.85}
              />
              <rect
                x={PAD.left + innerW - 38}
                y={y - 8}
                width={38}
                height={14}
                fill={o.color}
                opacity={0.18}
                rx={2}
              />
              <text
                x={PAD.left + innerW - 3}
                y={y + 4}
                textAnchor="end"
                fill={o.color}
                fontSize={9}
                fontWeight={600}
              >
                {o.label}
              </text>
            </g>
          );
        })}

      {/* Live price ticker — pulsing dot + dashed line at right edge */}
      {livePrice != null && livePrice > 0 && (() => {
        const y = scaleY(livePrice);
        if (y < PAD.top || y > PAD.top + priceH) return null;
        const x2 = PAD.left + innerW;
        return (
          <g pointerEvents="none">
            <line
              x1={PAD.left} x2={x2}
              y1={y} y2={y}
              stroke="rgba(255,255,255,0.6)"
              strokeWidth={1}
              strokeDasharray="2 3"
              opacity={0.7}
            />
            {/* Pulsing dot at right edge */}
            <circle cx={x2} cy={y} r={4} fill="#FFFFFF">
              <animate attributeName="r"      values="4;8;4"     dur="1.6s" repeatCount="indefinite" />
              <animate attributeName="opacity" values="1;0.2;1"  dur="1.6s" repeatCount="indefinite" />
            </circle>
            <circle cx={x2} cy={y} r={2.5} fill="#FFFFFF" />
            {/* Price label */}
            <rect x={x2 + 6} y={y - 8} width={56} height={16} rx={3} fill="rgba(255,255,255,0.92)" />
            <text x={x2 + 34} y={y + 4} textAnchor="middle" fontSize={10} fontWeight={700} fill="#000" fontFamily="ui-monospace">
              {livePrice >= 1000 ? livePrice.toLocaleString(undefined, { maximumFractionDigits: 2 })
                : livePrice >= 1 ? livePrice.toFixed(2) : livePrice.toFixed(5)}
            </text>
          </g>
        );
      })()}

      {/* X-axis baseline */}
      {!compact && (
        <line
          x1={PAD.left}
          x2={PAD.left + innerW}
          y1={PAD.top + priceH + volPaneH + (showVolume ? volGap : 0)}
          y2={PAD.top + priceH + volPaneH + (showVolume ? volGap : 0)}
          stroke="rgba(31,30,27,0.12)"
          strokeWidth={1}
          className="dark:stroke-[rgba(255,250,235,0.08)]"
        />
      )}

      {/* X-axis time labels + grid ticks — evenly-spaced (auto-density by width) */}
      {!compact && candles.length >= 3 && (() => {
        // Pick tick count by chart width — roughly 1 tick per 90px
        const desiredTicks = Math.max(3, Math.min(9, Math.floor(innerW / 90)));
        const baselineY = PAD.top + priceH + volPaneH + (showVolume ? volGap : 0);
        const labels: { idx: number; x: number; t: string }[] = [];
        for (let i = 0; i < desiredTicks; i++) {
          const idx = Math.floor((i * (candles.length - 1)) / Math.max(1, desiredTicks - 1));
          labels.push({ idx, x: scaleX(idx), t: candles[idx]?.time ?? "" });
        }
        return (
          <g fontSize={9} fontFamily="ui-monospace, monospace">
            {labels.map((lbl, i) => {
              const isEdge = i === 0 || i === labels.length - 1;
              return (
                <g key={i}>
                  {/* Vertical grid — visible when showGrid=true */}
                  {showGrid && !isEdge && (
                    <line
                      x1={lbl.x} x2={lbl.x}
                      y1={PAD.top} y2={baselineY}
                      stroke="rgba(255,255,255,0.10)"
                      className="[html:not(.dark)_&]:stroke-[rgba(31,30,27,0.10)]"
                      strokeDasharray="2 4"
                    />
                  )}
                  {/* Tick mark */}
                  <line
                    x1={lbl.x} x2={lbl.x}
                    y1={baselineY} y2={baselineY + 4}
                    stroke="rgba(255,255,255,0.45)"
                    className="[html:not(.dark)_&]:stroke-[rgba(31,30,27,0.45)]"
                  />
                  {/* Label */}
                  <text
                    x={lbl.x}
                    y={height - 8}
                    textAnchor={i === 0 ? "start" : i === labels.length - 1 ? "end" : "middle"}
                    fill="rgba(255,250,235,0.85)"
                    className="[html:not(.dark)_&]:fill-[rgba(31,30,27,0.85)]"
                    fontSize={10}
                  >
                    {fmtTime(lbl.t)}
                  </text>
                </g>
              );
            })}
            {/* X-axis title */}
            <text
              x={PAD.left + innerW / 2}
              y={height - 1}
              textAnchor="middle"
              fontSize={7}
              fill="rgba(255,250,235,0.3)"
              className="[html:not(.dark)_&]:fill-[rgba(31,30,27,0.4)] uppercase tracking-widest"
            >
              time
            </text>
          </g>
        );
      })()}

      {/* ── VWAP overlay ──────────────────────────────────────────── */}
      {showVwap && vwapSeries.length > 0 && (() => {
        const vwapLine = vwapSeries.map((v) => v?.vwap ?? null);
        const u1 = vwapSeries.map((v) => v?.upper1 ?? null);
        const l1 = vwapSeries.map((v) => v?.lower1 ?? null);
        const u2 = vwapSeries.map((v) => v?.upper2 ?? null);
        const l2 = vwapSeries.map((v) => v?.lower2 ?? null);

        // Build filled area paths for ±1σ and ±2σ
        const mkArea = (top: (number | null)[], bot: (number | null)[]) => {
          const tPts = top.map((v, i) => v != null ? `${scaleX(i).toFixed(1)},${scaleY(v).toFixed(1)}` : null).filter(Boolean) as string[];
          const bPts = bot.map((v, i) => v != null ? `${scaleX(i).toFixed(1)},${scaleY(v).toFixed(1)}` : null).filter(Boolean).reverse() as string[];
          return tPts.length && bPts.length ? `M ${tPts.join(" L ")} L ${bPts.join(" L ")} Z` : null;
        };

        const area1 = mkArea(u1, l1);
        const area2 = mkArea(u2, l2);
        const vwapPath = pathFromValues(vwapLine, scaleX, scaleY);
        const u1Path = pathFromValues(u1, scaleX, scaleY);
        const l1Path = pathFromValues(l1, scaleX, scaleY);

        return (
          <g>
            {area2 && <path d={area2} fill="rgba(14,165,233,0.04)" />}
            {area1 && <path d={area1} fill="rgba(14,165,233,0.09)" />}
            {u1Path && <path d={u1Path} fill="none" stroke="rgba(14,165,233,0.35)" strokeWidth={0.8} strokeDasharray="3 3" />}
            {l1Path && <path d={l1Path} fill="none" stroke="rgba(14,165,233,0.35)" strokeWidth={0.8} strokeDasharray="3 3" />}
            {vwapPath && <path d={vwapPath} fill="none" stroke="#0ea5e9" strokeWidth={1.5} strokeLinejoin="round" />}
            {/* VWAP label at last point */}
            {vwapSeries[n - 1] && (
              <text
                x={PAD.left + innerW - 2}
                y={scaleY(vwapSeries[n - 1]!.vwap) - 4}
                fontSize={8}
                fontFamily="ui-monospace, monospace"
                textAnchor="end"
                fill="#0ea5e9"
                fontWeight={700}
              >
                VWAP
              </text>
            )}
          </g>
        );
      })()}

      {/* ── Volume Profile (right sidebar) ─────────────────────── */}
      {showVolumeProfile && vpBuckets.length > 0 && (
        <g>
          {vpBuckets.map((b, i) => {
            const y = scaleY(b.priceMax);
            const h = Math.max(1, scaleY(b.priceMin) - y);
            const barW = Math.max(2, (b.volume / vpMaxVol) * (vpW - 4));
            const x = PAD.left + innerW + 2;
            const fill = b.isPoc ? "#f59e0b" : b.isVa ? "rgba(14,165,233,0.5)" : "rgba(255,255,255,0.15)";
            return (
              <g key={i}>
                <rect x={x} y={y} width={barW} height={h} fill={fill} opacity={0.85} />
                {b.vaType && (
                  <text x={x + vpW - 2} y={y + h / 2 + 3} fontSize={7} textAnchor="end" fill="#0ea5e9" fontWeight={700}>
                    {b.vaType.toUpperCase()}
                  </text>
                )}
                {b.isPoc && (
                  <text x={x + vpW - 2} y={y + h / 2 + 3} fontSize={7} textAnchor="end" fill="#f59e0b" fontWeight={700}>
                    POC
                  </text>
                )}
              </g>
            );
          })}
          {/* VP column divider */}
          <line
            x1={PAD.left + innerW}
            x2={PAD.left + innerW}
            y1={PAD.top}
            y2={PAD.top + priceH}
            stroke="rgba(255,255,255,0.06)"
            strokeWidth={0.5}
          />
        </g>
      )}

      {/* ── Crosshair ──────────────────────────────────────────────── */}
      {crosshairIdx !== null && crosshairIdx >= 0 && crosshairIdx < candles.length && (() => {
        const cx = scaleX(crosshairIdx);
        const c = candles[crosshairIdx];
        const cy = scaleY(c.close);
        return (
          <g pointerEvents="none">
            {/* Vertical line */}
            <line
              x1={cx} x2={cx}
              y1={PAD.top} y2={PAD.top + priceH}
              stroke="rgba(255,255,255,0.25)"
              strokeWidth={1}
              strokeDasharray="2 3"
            />
            {/* Horizontal line at close price */}
            <line
              x1={PAD.left} x2={PAD.left + innerW}
              y1={cy} y2={cy}
              stroke="rgba(255,255,255,0.18)"
              strokeWidth={0.8}
              strokeDasharray="2 3"
            />
            {/* Price label on Y axis */}
            <rect x={0} y={cy - 9} width={PAD.left - 2} height={16} fill="#0ea5e9" rx={2} opacity={0.9} />
            <text x={PAD.left - 6} y={cy + 4} fontSize={8} textAnchor="end" fill="white" fontWeight={700} fontFamily="ui-monospace,monospace">
              {c.close.toFixed(c.close >= 100 ? 2 : c.close >= 1 ? 3 : 5)}
            </text>
            {/* OHLCV tooltip box */}
            {!compact && (
              <g>
                <rect
                  x={cx > PAD.left + innerW / 2 ? cx - 104 : cx + 6}
                  y={PAD.top + 2}
                  width={98}
                  height={64}
                  fill="rgba(7,10,15,0.92)"
                  stroke="rgba(255,255,255,0.12)"
                  rx={4}
                />
                {[
                  { label: "O", val: c.open, color: c.open >= c.close ? "#C9485F" : "#3B8C68" },
                  { label: "H", val: c.high, color: "#3B8C68" },
                  { label: "L", val: c.low, color: "#C9485F" },
                  { label: "C", val: c.close, color: c.close >= c.open ? "#3B8C68" : "#C9485F" },
                ].map(({ label, val, color }, row) => (
                  <g key={label}>
                    <text x={cx > PAD.left + innerW / 2 ? cx - 98 : cx + 12} y={PAD.top + 14 + row * 14} fontSize={9} fill="rgba(255,255,255,0.5)" fontFamily="ui-monospace,monospace">{label}</text>
                    <text x={cx > PAD.left + innerW / 2 ? cx - 10 : cx + 100} y={PAD.top + 14 + row * 14} fontSize={9} textAnchor="end" fill={color} fontWeight={700} fontFamily="ui-monospace,monospace">
                      {val.toFixed(val >= 100 ? 2 : val >= 1 ? 3 : 5)}
                    </text>
                  </g>
                ))}
              </g>
            )}
          </g>
        );
      })()}

      {/* Click capture overlay — only mounted when in a drawing mode.
          Sits on top so clicks aren't intercepted by other elements. */}
      {onChartClick && drawingMode !== "none" && (
        <rect
          x={PAD.left}
          y={PAD.top}
          width={innerW}
          height={priceH}
          fill="transparent"
          style={{ cursor: "crosshair" }}
          onClick={(e) => {
            const rect = e.currentTarget.getBoundingClientRect();
            const sx = e.clientX - rect.left;
            const sy = e.clientY - rect.top;
            // Convert to candle index (round to nearest)
            const idx = Math.max(
              0,
              Math.min(candles.length - 1, Math.floor(sx / step))
            );
            const time = candles[idx].time;
            // Reverse y → price
            const price =
              priceMin + ((priceH - sy) / priceH) * priceRange;
            onChartClick(time, price);
          }}
        />
      )}
    </svg>
  );
}

/** Build an SVG path from a sparse value array (null gaps break the line). */
function pathFromValues(
  values: (number | null)[],
  scaleX: (i: number) => number,
  scaleY: (v: number) => number
): string | null {
  let out = "";
  let pen = false;
  for (let i = 0; i < values.length; i++) {
    const v = values[i];
    if (v == null) {
      pen = false;
      continue;
    }
    const x = scaleX(i).toFixed(2);
    const y = scaleY(v).toFixed(2);
    out += pen ? `L ${x},${y} ` : `M ${x},${y} `;
    pen = true;
  }
  return out.trim() || null;
}
