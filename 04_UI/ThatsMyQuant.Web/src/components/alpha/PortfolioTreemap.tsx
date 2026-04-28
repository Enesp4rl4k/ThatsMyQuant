import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { formatUsd, pnlColor } from "@/lib/format";

interface Props {
  width?: number;
  height?: number;
  className?: string;
}

interface TreemapInput {
  market: string;
  family: string;
  position: string;
  weight: number;
  realizedPnl: number;
}

interface Tile {
  x: number;
  y: number;
  w: number;
  h: number;
  fill: string;
  title: string;
  subtitle: string;
  weightLabel: string;
  pnlLabel: string;
  pnl: number;
}

/**
 * Hierarchical treemap of portfolio allocation: Market → Family → Position.
 * Tile area = capital weight; tile color = realized PnL gradient (green/red).
 * Mirrors the WPF PortfolioSunburstView. Falls back to validated-portfolio
 * seed if no live positions are open (so the panel always shows something).
 */
export function PortfolioTreemap({
  width = 720,
  height = 360,
  className,
}: Props) {
  const positionsQ = useQuery({
    queryKey: ["positions-for-treemap"],
    queryFn: () => dashboardApi.positions(),
    refetchInterval: 15_000,
    staleTime: 10_000,
  });
  const tradesQ = useQuery({
    queryKey: ["trades-for-treemap"],
    queryFn: () => dashboardApi.recentTrades(200),
    staleTime: 30_000,
  });

  const data: TreemapInput[] = useMemo(() => {
    const live = positionsQ.data ?? [];
    if (live.length > 0) {
      // Aggregate per (strategyTag, symbol). Weight = notional share.
      const totalNotional =
        live.reduce(
          (s, p) => s + Math.abs(p.markPrice * p.remainingQuantity),
          0
        ) || 1;
      // Realized PnL by strategy from closed trades
      const realizedByKey = new Map<string, number>();
      for (const t of tradesQ.data ?? []) {
        if (t.status?.toLowerCase() === "open") continue;
        const k = `${t.strategyTag}|${t.symbol}`;
        realizedByKey.set(k, (realizedByKey.get(k) ?? 0) + t.realizedPnL);
      }
      return live.map((p) => {
        const family = familyOf(p.strategyTag);
        const notional = Math.abs(p.markPrice * p.remainingQuantity);
        return {
          market: marketOf(p.symbol),
          family,
          position: `${p.symbol} (${p.strategyTag})`,
          weight: notional / totalNotional,
          realizedPnl:
            (realizedByKey.get(`${p.strategyTag}|${p.symbol}`) ?? 0) +
            p.unrealizedPnL,
        };
      });
    }
    // Fallback — validated portfolio seed (mirrors WPF default).
    return [
      { market: "Crypto", family: "Pairs", position: "TRX-XLM 4h", weight: 0.20, realizedPnl: 12 },
      { market: "Crypto", family: "Pairs", position: "SOL-AVAX 4h", weight: 0.20, realizedPnl: 3 },
      { market: "Crypto", family: "Pairs", position: "SOL-LINK 4h", weight: 0.20, realizedPnl: 5 },
      { market: "Crypto", family: "Funding", position: "LTC-Funding", weight: 0.15, realizedPnl: 2 },
      { market: "Crypto", family: "Funding", position: "DOGE-Funding", weight: 0.13, realizedPnl: -1 },
      { market: "Crypto", family: "Funding", position: "AAVE-Funding", weight: 0.12, realizedPnl: 1 },
    ];
  }, [positionsQ.data, tradesQ.data]);

  const tiles = useMemo(() => layout(data, width, height), [data, width, height]);
  const totalPnl = data.reduce((s, p) => s + p.realizedPnl, 0);
  const isFallback = !positionsQ.data || positionsQ.data.length === 0;

  return (
    <div className={className}>
      <div className="flex items-baseline justify-between px-3 py-2">
        <div className="flex items-center gap-2">
          <span className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
            Allocation Treemap
          </span>
          {isFallback && (
            <span className="px-1.5 py-0.5 rounded bg-amber-500/10 text-amber-400 text-[8px] uppercase tracking-widest">
              Seed
            </span>
          )}
        </div>
        <span className="text-[10px] font-mono text-muted-foreground/70">
          Positions <b className="text-foreground/80">{data.length}</b>
          {" · "}
          Net PnL{" "}
          <b className={pnlColor(totalPnl)}>{formatUsd(totalPnl)}</b>
        </span>
      </div>

      <svg width={width} height={height} style={{ display: "block" }}>
        {tiles.map((t, i) => (
          <g key={i}>
            <rect
              x={t.x}
              y={t.y}
              width={t.w}
              height={t.h}
              fill={t.fill}
              stroke="rgba(0,0,0,0.55)"
              strokeWidth={1}
              rx={3}
            />
            {t.w > 84 && t.h > 32 && (
              <>
                <text x={t.x + 6} y={t.y + 14} fontSize={10} fontWeight={700} fill="rgba(255,255,255,0.95)">
                  {t.title}
                </text>
                <text x={t.x + 6} y={t.y + 26} fontSize={8} fill="rgba(255,255,255,0.6)">
                  {t.subtitle}
                </text>
                {t.h > 60 && (
                  <>
                    <text x={t.x + 6} y={t.y + t.h - 18} fontSize={9} fontFamily="ui-monospace, monospace" fill="rgba(255,255,255,0.85)">
                      {t.weightLabel}
                    </text>
                    <text
                      x={t.x + 6}
                      y={t.y + t.h - 6}
                      fontSize={9}
                      fontFamily="ui-monospace, monospace"
                      fontWeight={700}
                      fill={t.pnl >= 0 ? "#a7f3d0" : "#fecaca"}
                    >
                      {t.pnlLabel}
                    </text>
                  </>
                )}
              </>
            )}
          </g>
        ))}
      </svg>
    </div>
  );
}

function layout(input: TreemapInput[], W: number, H: number): Tile[] {
  if (input.length === 0) return [];
  const sumW = input.reduce((s, p) => s + p.weight, 0) || 1;
  const absMax =
    Math.max(0.000001, ...input.map((p) => Math.abs(p.realizedPnl))) || 1;

  // Group: Market → Family → list
  const byMarket = new Map<string, TreemapInput[]>();
  for (const p of input) {
    if (!byMarket.has(p.market)) byMarket.set(p.market, []);
    byMarket.get(p.market)!.push(p);
  }

  const tiles: Tile[] = [];
  let xCursor = 0;
  for (const [, list] of byMarket) {
    const mWeight = list.reduce((s, p) => s + p.weight, 0) || 1;
    const mWidth = W * (mWeight / sumW);

    const byFamily = new Map<string, TreemapInput[]>();
    for (const p of list) {
      if (!byFamily.has(p.family)) byFamily.set(p.family, []);
      byFamily.get(p.family)!.push(p);
    }
    let mTop = 0;
    for (const [, fam] of byFamily) {
      const fWeight = fam.reduce((s, p) => s + p.weight, 0) || 1;
      const fHeight = H * (fWeight / mWeight);
      let pCursor = xCursor;
      for (const pos of fam) {
        const pWidth = mWidth * (pos.weight / fWeight);
        tiles.push({
          x: pCursor + 2,
          y: mTop + 2,
          w: Math.max(2, pWidth - 4),
          h: Math.max(2, fHeight - 4),
          fill: pnlFill(pos.realizedPnl, absMax),
          title: pos.position,
          subtitle: `${pos.family} · ${pos.market}`,
          weightLabel: `${(pos.weight * 100).toFixed(1)}%`,
          pnlLabel:
            pos.realizedPnl >= 0
              ? `+${pos.realizedPnl.toFixed(0)}`
              : pos.realizedPnl.toFixed(0),
          pnl: pos.realizedPnl,
        });
        pCursor += pWidth;
      }
      mTop += fHeight;
    }
    xCursor += mWidth;
  }
  return tiles;
}

function pnlFill(pnl: number, absMax: number): string {
  const t = Math.max(0, Math.min(1, Math.abs(pnl) / absMax));
  if (pnl >= 0) {
    const r = Math.round(0x1f + (0x37 - 0x1f) * (1 - t));
    const g = Math.round(0x60 + (0xe0 - 0x60) * t);
    const b = Math.round(0x35 + (0x5b - 0x35) * (1 - t));
    return `rgb(${r},${g},${b})`;
  }
  const r = Math.round(0x90 + (0xf0 - 0x90) * t);
  const g = Math.round(0x35 + (0x5a - 0x35) * (1 - t));
  const b = Math.round(0x35 + (0x5a - 0x35) * (1 - t));
  return `rgb(${r},${g},${b})`;
}

function familyOf(strategyTag: string): string {
  const t = strategyTag?.toLowerCase() ?? "";
  if (t.includes("pair")) return "Pairs";
  if (t.includes("fund")) return "Funding";
  if (t.includes("ml") || t.includes("ai")) return "ML";
  return "Directional";
}

function marketOf(symbol: string): string {
  if (/usd[t|c]?$/i.test(symbol) || /-usd/i.test(symbol)) return "Crypto";
  if (/\.is$|\.ist$/i.test(symbol)) return "BIST";
  return "Crypto";
}
