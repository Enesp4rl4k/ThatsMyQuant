import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { pairsApi, type ActivePairSnapshot } from "@/api/pairs";
import { cn } from "@/lib/utils";

export function LivePairMonitor() {
  const q = useQuery({
    queryKey: ["pairs", "active"],
    queryFn: pairsApi.active,
    refetchInterval: 15_000,
    staleTime: 10_000,
  });

  const data = q.data;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Live · Pairs Trading
          </p>
          <p className="text-sm font-bold">
            Active Pair Monitor{" "}
            {data && (
              <span className="text-muted-foreground/40 font-light text-xs">
                — {data.timeframe} · entry ±{data.entryZ}σ · exit ±{data.exitZ}σ · stop ±{data.stopZ}σ
              </span>
            )}
          </p>
        </div>
        {q.isFetching && (
          <span className="text-[9px] uppercase tracking-widest text-accent/60 animate-pulse">live</span>
        )}
      </div>

      {q.isLoading ? (
        <p className="text-xs text-muted-foreground/60">Loading active pairs…</p>
      ) : q.isError ? (
        <p className="text-xs text-rose-400">Failed to load active pairs.</p>
      ) : !data || data.pairs.length === 0 ? (
        <p className="text-xs text-muted-foreground/50">
          No pairs configured under <code className="text-accent/70">Strategy:PairsTrading:AllowedPairs</code>.
        </p>
      ) : (
        <div className="space-y-2">
          {data.pairs.map((p) => (
            <PairRow key={`${p.symbolY}/${p.symbolX}`} pair={p} entryZ={data.entryZ} stopZ={data.stopZ} />
          ))}
        </div>
      )}
    </div>
  );
}

function PairRow({ pair, entryZ, stopZ }: { pair: ActivePairSnapshot; entryZ: number; stopZ: number }) {
  if (pair.error || pair.state === "Error") {
    return (
      <div className="rounded-md border border-rose-500/20 bg-rose-500/5 px-3 py-2 text-xs">
        <span className="font-mono font-bold">{pair.symbolY} / {pair.symbolX}</span>{" "}
        <span className="text-rose-400">{pair.error ?? "Error"}</span>
      </div>
    );
  }

  const stateColor =
    pair.state === "Stop"
      ? "text-rose-400 bg-rose-500/10 border-rose-500/30"
      : pair.state === "ShortSpread" || pair.state === "LongSpread"
      ? "text-amber-400 bg-amber-500/10 border-amber-500/30"
      : pair.state === "AtMean"
      ? "text-emerald-400 bg-emerald-500/10 border-emerald-500/30"
      : "text-muted-foreground bg-white/5 border-white/10";

  return (
    <div className="rounded-md border border-white/5 bg-black/20 p-3 grid grid-cols-12 gap-3 items-center">
      <div className="col-span-3">
        <p className="font-mono font-bold text-xs">{pair.symbolY} / {pair.symbolX}</p>
        <p className="text-[9px] text-muted-foreground/40 mt-0.5">
          β={pair.hedgeRatio.toFixed(3)} · HL={pair.halfLife.toFixed(1)} · H={pair.hurstExponent.toFixed(2)}
          {!pair.isCointegrated && <span className="text-rose-400 ml-1">· not cointegrated</span>}
        </p>
      </div>

      <div className="col-span-5">
        <ZScoreBar z={pair.currentZ} entryZ={entryZ} stopZ={stopZ} />
        <ZTailSpark zTail={pair.zTail} entryZ={entryZ} stopZ={stopZ} />
      </div>

      <div className="col-span-2 text-right">
        <p className={cn("font-mono font-black tabular-nums", Math.abs(pair.currentZ) >= entryZ ? "text-amber-400" : "text-foreground")}>
          z = {pair.currentZ.toFixed(2)}
        </p>
        <p className="text-[9px] text-muted-foreground/40">ρ={pair.correlation.toFixed(2)}</p>
      </div>

      <div className="col-span-2 text-right">
        <span
          className={cn(
            "inline-block px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest border",
            stateColor
          )}
        >
          {pair.state}
        </span>
      </div>
    </div>
  );
}

function ZScoreBar({ z, entryZ, stopZ }: { z: number; entryZ: number; stopZ: number }) {
  // Visual scale: -stopZ..+stopZ mapped to 0..100%
  const pct = useMemo(() => {
    const clamped = Math.max(-stopZ, Math.min(stopZ, z));
    return ((clamped + stopZ) / (2 * stopZ)) * 100;
  }, [z, stopZ]);

  const entryPct = ((stopZ - entryZ) / (2 * stopZ)) * 100;
  const entryPctR = ((stopZ + entryZ) / (2 * stopZ)) * 100;

  return (
    <div className="relative h-2 bg-white/5 rounded-full overflow-hidden">
      {/* Entry band */}
      <div
        className="absolute inset-y-0 bg-amber-500/10"
        style={{ left: `${entryPct}%`, right: 0 }}
      />
      <div
        className="absolute inset-y-0 bg-amber-500/10"
        style={{ left: 0, right: `${100 - entryPctR}%` }}
      />
      {/* Center mark (zero) */}
      <div className="absolute inset-y-0 w-px bg-emerald-400/50" style={{ left: "50%" }} />
      {/* Marker */}
      <div
        className="absolute top-0 bottom-0 w-1 bg-accent shadow-[0_0_8px_rgba(0,191,255,0.6)] rounded-full"
        style={{ left: `calc(${pct}% - 2px)` }}
      />
    </div>
  );
}

function ZTailSpark({ zTail, entryZ, stopZ }: { zTail: number[]; entryZ: number; stopZ: number }) {
  if (zTail.length < 2) return null;
  const w = 240;
  const h = 28;
  const max = Math.max(stopZ, ...zTail.map((v) => Math.abs(v)));
  const xStep = w / (zTail.length - 1);
  const yFor = (v: number) => h / 2 - (v / max) * (h / 2 - 2);
  const path = zTail.map((v, i) => `${i === 0 ? "M" : "L"} ${(i * xStep).toFixed(1)} ${yFor(v).toFixed(1)}`).join(" ");

  return (
    <svg width={w} height={h} className="mt-1 block">
      {/* Entry bands */}
      <line x1={0} x2={w} y1={yFor(entryZ)} y2={yFor(entryZ)} stroke="rgba(245,158,11,0.3)" strokeDasharray="2 3" />
      <line x1={0} x2={w} y1={yFor(-entryZ)} y2={yFor(-entryZ)} stroke="rgba(245,158,11,0.3)" strokeDasharray="2 3" />
      {/* Zero */}
      <line x1={0} x2={w} y1={h / 2} y2={h / 2} stroke="rgba(255,255,255,0.1)" />
      <path d={path} stroke="rgba(0,191,255,0.85)" strokeWidth="1.2" fill="none" />
    </svg>
  );
}
