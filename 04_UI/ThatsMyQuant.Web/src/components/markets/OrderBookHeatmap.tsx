import { useQuery } from "@tanstack/react-query";
import { marketsApi, type OrderBookLevel } from "@/api/markets";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";

interface Props {
  symbol: string;
  width?: number;
  height?: number;
  depth?: number;
  className?: string;
}

/**
 * Order-book depth heatmap. Bar width ∝ resting size; color intensity
 * encodes magnitude (bids: deep blue → cyan → yellow; asks: deep red →
 * orange → yellow). Largest sizes pop visually so liquidity walls are
 * obvious. Mirrors the WPF OrderBookHeatmapView.
 */
export function OrderBookHeatmap({
  symbol,
  width = 720,
  height = 420,
  depth = 25,
  className,
}: Props) {
  const q = useQuery({
    queryKey: ["markets", "orderbook-heatmap", symbol],
    queryFn: () => marketsApi.orderbook(symbol),
    refetchInterval: 2_000,
    enabled: !!symbol,
  });

  const ob = q.data;

  if (!ob) {
    return (
      <div
        className={className}
        style={{ width, height }}
      >
        <p className="p-6 text-center italic font-serif text-muted-foreground/60">
          — loading depth —
        </p>
      </div>
    );
  }

  const bids = (ob.bids ?? []).slice(0, depth);
  const asks = (ob.asks ?? []).slice(0, depth);

  const maxQty = Math.max(
    1,
    ...bids.map((l) => l.size),
    ...asks.map((l) => l.size)
  );
  const totalBid = bids.reduce((s, l) => s + l.size, 0);
  const totalAsk = asks.reduce((s, l) => s + l.size, 0);
  const ofi =
    totalBid + totalAsk > 0
      ? (totalBid - totalAsk) / (totalBid + totalAsk)
      : 0;

  const PAD = 12;
  const innerW = width - PAD * 2;
  const innerH = height - PAD * 2 - 28; // leave space for header
  const centerX = PAD + innerW / 2;
  const sideW = innerW / 2 - 8;
  const rowH = innerH / Math.max(depth, bids.length, asks.length, 1);

  const bestBid = bids[0]?.price ?? 0;
  const bestAsk = asks[0]?.price ?? 0;
  const spread = bestAsk - bestBid;
  const mid = (bestBid + bestAsk) / 2;
  const spreadBps = mid > 0 ? (spread / mid) * 10_000 : 0;

  const ofiColor =
    ofi > 0.1 ? "#37A95B" : ofi < -0.1 ? "#F05A5A" : "#F0883E";

  return (
    <div className={className}>
      {/* Header */}
      <div className="flex items-center justify-between px-3 py-2">
        <div className="flex items-center gap-2">
          <span className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            Depth Heatmap
          </span>
          <SyntheticBadge source={ob.isSynthetic === false ? "live" : "synthetic"} />
        </div>
        <div className="flex items-center gap-4 text-[10px] font-mono">
          <span className="text-emerald-400">
            BID <b>{fmt(bestBid)}</b>
          </span>
          <span className="text-rose-400">
            ASK <b>{fmt(bestAsk)}</b>
          </span>
          <span className="text-muted-foreground/70">
            SPR <b>{spreadBps.toFixed(2)} bp</b>
          </span>
          <span style={{ color: ofiColor }}>
            OFI <b>{ofi >= 0 ? "+" : ""}{ofi.toFixed(3)}</b>
          </span>
        </div>
      </div>

      <svg width={width} height={height - 28} style={{ display: "block" }}>
        {/* Center divider */}
        <line
          x1={centerX}
          x2={centerX}
          y1={PAD}
          y2={PAD + innerH}
          stroke="rgba(255,255,255,0.08)"
          strokeWidth={1}
        />

        {/* Bid bars (right-anchored to center, growing left) */}
        {bids.map((lvl, i) => {
          const t = lvl.size / maxQty;
          const barW = sideW * t;
          const y = PAD + i * rowH;
          const fill = bidColor(t);
          return (
            <g key={`b-${i}`}>
              <rect
                x={centerX - barW}
                y={y}
                width={barW}
                height={Math.max(1, rowH - 1)}
                fill={fill}
                opacity={0.85}
              />
              <text
                x={centerX - barW - 4}
                y={y + rowH / 2 + 3}
                fontSize={9}
                fontFamily="ui-monospace, monospace"
                textAnchor="end"
                fill="rgba(255,255,255,0.5)"
              >
                {fmt(lvl.price)}
              </text>
              {barW > 60 && (
                <text
                  x={centerX - 6}
                  y={y + rowH / 2 + 3}
                  fontSize={9}
                  fontFamily="ui-monospace, monospace"
                  textAnchor="end"
                  fill="rgba(0,0,0,0.85)"
                  fontWeight={700}
                >
                  {lvl.size.toFixed(3)}
                </text>
              )}
            </g>
          );
        })}

        {/* Ask bars (left-anchored to center, growing right) */}
        {asks.map((lvl, i) => {
          const t = lvl.size / maxQty;
          const barW = sideW * t;
          const y = PAD + i * rowH;
          const fill = askColor(t);
          return (
            <g key={`a-${i}`}>
              <rect
                x={centerX}
                y={y}
                width={barW}
                height={Math.max(1, rowH - 1)}
                fill={fill}
                opacity={0.85}
              />
              <text
                x={centerX + barW + 4}
                y={y + rowH / 2 + 3}
                fontSize={9}
                fontFamily="ui-monospace, monospace"
                fill="rgba(255,255,255,0.5)"
              >
                {fmt(lvl.price)}
              </text>
              {barW > 60 && (
                <text
                  x={centerX + 6}
                  y={y + rowH / 2 + 3}
                  fontSize={9}
                  fontFamily="ui-monospace, monospace"
                  fill="rgba(0,0,0,0.85)"
                  fontWeight={700}
                >
                  {lvl.size.toFixed(3)}
                </text>
              )}
            </g>
          );
        })}
      </svg>
    </div>
  );
}

/** Deep blue → cyan → yellow ramp. */
function bidColor(t: number): string {
  t = Math.max(0, Math.min(1, t));
  if (t < 0.5) {
    const k = t / 0.5;
    const r = Math.round(0x14 + (0x1f - 0x14) * k);
    const g = Math.round(0x2a + (0x88 - 0x2a) * k);
    const b = Math.round(0x6e + (0xc4 - 0x6e) * k);
    return `rgb(${r},${g},${b})`;
  }
  const k = (t - 0.5) / 0.5;
  const r = Math.round(0x1f + (0xf0 - 0x1f) * k);
  const g = Math.round(0x88 + (0xe2 - 0x88) * k);
  const b = Math.round(0xc4 + (0x3d - 0xc4) * k);
  return `rgb(${r},${g},${b})`;
}

/** Deep red → orange → yellow ramp. */
function askColor(t: number): string {
  t = Math.max(0, Math.min(1, t));
  if (t < 0.5) {
    const k = t / 0.5;
    const r = Math.round(0x6e + (0xc4 - 0x6e) * k);
    const g = Math.round(0x14 + (0x35 - 0x14) * k);
    const b = Math.round(0x14 + (0x1f - 0x14) * k);
    return `rgb(${r},${g},${b})`;
  }
  const k = (t - 0.5) / 0.5;
  const r = Math.round(0xc4 + (0xf0 - 0xc4) * k);
  const g = Math.round(0x35 + (0xe2 - 0x35) * k);
  const b = Math.round(0x1f + (0x3d - 0x1f) * k);
  return `rgb(${r},${g},${b})`;
}

function fmt(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}

// `OrderBookLevel` re-used as-is from API
export type { OrderBookLevel };
