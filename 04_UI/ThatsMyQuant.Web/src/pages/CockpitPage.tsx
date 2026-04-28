import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { pairsApi, type ActivePairSnapshot } from "@/api/pairs";
import { proModeApi } from "@/api/proMode";
import { fundingApi } from "@/api/funding";
import { dashboardApi } from "@/api/dashboard";
import { useVenue } from "@/hooks/useVenue";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import { pushNotification } from "@/lib/notifications";
import { RiskExposureBar } from "@/components/cockpit/RiskExposureBar";
import { ThresholdMonitor } from "@/components/cockpit/ThresholdMonitor";
import { TradableNowScanner } from "@/components/cockpit/TradableNowScanner";
import { CorrelationMatrix } from "@/components/cockpit/CorrelationMatrix";
import { LiquidationWatchdog } from "@/components/cockpit/LiquidationWatchdog";
import { WhyNotPanel } from "@/components/cockpit/WhyNotPanel";
import { RingStat, type RingTone } from "@/components/ui/RingStat";
import { ZoomableTile } from "@/components/ui/ZoomableTile";

/**
 * Live Strategy Cockpit — single screen showing everything a trader needs
 * to assess current system state:
 *   • Bot overview: armed state, circuit breaker, regime, leverage
 *   • Pairs section: each pair's z-score gauge, state pill, half-life
 *   • Funding section: current rate, 8h sparkline, signal
 *   • Open positions summary
 *   • Recent signal feed
 */
export function CockpitPage() {
  const proQ = useQuery({
    queryKey: ["cockpit", "pro"],
    queryFn: proModeApi.snapshot,
    refetchInterval: 5_000,
  });
  const pairsQ = useQuery({
    queryKey: ["cockpit", "pairs"],
    queryFn: pairsApi.active,
    refetchInterval: 5_000,
  });
  const fundingQ = useQuery({
    queryKey: ["cockpit", "funding"],
    queryFn: () => fundingApi.rates(),
    refetchInterval: 10_000,
  });
  const { executionModeParam } = useVenue();
  const posQ = useQuery({
    queryKey: ["cockpit", "positions", executionModeParam ?? "all"],
    queryFn: () => dashboardApi.positions(executionModeParam),
    refetchInterval: 5_000,
  });
  const signalsQ = useQuery({
    queryKey: ["cockpit", "signals"],
    queryFn: () => dashboardApi.recentSignals(15),
    refetchInterval: 5_000,
  });

  const snap = proQ.data;
  const pairs = pairsQ.data?.pairs ?? [];
  const funding = fundingQ.data ?? [];
  const positions = posQ.data ?? [];
  const signals = signalsQ.data ?? [];

  const qc = useQueryClient();
  const [closingId, setClosingId] = useState<string | null>(null);
  const closeM = useMutation({
    mutationFn: dashboardApi.closePosition,
    onMutate: (vars) => setClosingId(`${vars.symbol}:${vars.side ?? ""}`),
    onSettled: () => setClosingId(null),
    onSuccess: (res) => {
      pushNotification({
        type: "success",
        title: `Close requested · ${res.symbol}`,
        body: `Queued #${res.id}. Worker processes within ~3s.`,
      });
      // Optimistic refresh after a tick so the queue-driven change shows up
      setTimeout(() => {
        qc.invalidateQueries({ queryKey: ["cockpit", "positions"] });
        qc.invalidateQueries({ queryKey: ["positions"] });
        qc.invalidateQueries({ queryKey: ["portfolio"] });
      }, 4_000);
    },
    onError: (err: unknown) => {
      pushNotification({
        type: "error",
        title: "Close request failed",
        body: err instanceof Error ? err.message : String(err),
      });
    },
  });

  const cbState = snap?.circuitBreaker?.state ?? "—";
  const cbColor =
    cbState === "Tripped" ? "text-rose-400" :
    cbState === "Warning" ? "text-amber-400" :
    "text-emerald-400";

  return (
    <div className="space-y-5">
      {/* ── Page header ──────────────────────────────────────────── */}
      <div className="flex items-baseline justify-between">
        <div>
          <h2 className="font-serif text-2xl tracking-tight">Live Cockpit</h2>
          <p className="text-xs text-muted-foreground/60 mt-0.5">
            Real-time strategy state · refreshes every 5s
          </p>
        </div>
        <div className="flex items-center gap-2 text-[10px] font-mono uppercase tracking-widest">
          <span className={cn(
            "px-2 py-1 rounded border",
            snap?.isArmed
              ? "bg-emerald-400/10 border-emerald-400/30 text-emerald-400"
              : "bg-white/[0.03] border-white/10 text-muted-foreground/50"
          )}>
            {snap?.isArmed ? "● Armed" : "○ Disarmed"}
          </span>
          <span className={cn("px-2 py-1 rounded border border-white/10", cbColor)}>
            CB: {cbState}
          </span>
        </div>
      </div>

      {/* ── System Row ───────────────────────────────────────────── */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <StatTile label="Regime" value={snap?.currentRegime ?? "—"} color={regimeColor(snap?.currentRegime)} />
        <StatTile label="Base Leverage" value={snap ? `${snap.baseLeverage}×` : "—"} />
        <StatTile label="Effective Lev" value={snap ? `${snap.effectiveLeverage}×` : "—"}
          sub={snap?.leverageExplanation} />
        <StatTile label="Drawdown"
          value={snap ? `${(snap.circuitBreaker?.realizedDrawdown * 100).toFixed(2)}%` : "—"}
          color={snap && snap.circuitBreaker?.realizedDrawdown > 0.04 ? "text-rose-400" : "text-emerald-400"}
        />
      </div>

      {/* ── Risk Exposure Bar ────────────────────────────────────── */}
      <RiskExposureBar positions={positions} />

      {/* ── Tradable-Now scanner (universe view) ──────────────────── */}
      <TradableNowScanner />

      {/* ── Why-Not Panel (live diagnostic of bot's quietness) ────── */}
      <WhyNotPanel />

      {/* ── Threshold Monitor (per-strategy proximity gauges) ─────── */}
      <ThresholdMonitor />

      {/* ── Correlation Matrix + Liquidation Watchdog (side by side) */}
      <div className="grid grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-4">
        <CorrelationMatrix positions={positions} />
        <LiquidationWatchdog positions={positions} />
      </div>

      {/* ── Pairs ────────────────────────────────────────────────── */}
      <Section title="Pairs Trading" count={pairs.length}>
        {pairs.length === 0 ? (
          <EmptyRow msg="No active pairs loaded — check pairs engine status" />
        ) : (
          <div className="space-y-2">
            {pairs.map((p) => <PairRow key={`${p.symbolY}-${p.symbolX}`} pair={p} />)}
          </div>
        )}
      </Section>

      {/* ── Funding Alpha ────────────────────────────────────────── */}
      <Section title="Funding Alpha" count={funding.length}>
        {funding.length === 0 ? (
          <EmptyRow msg="No funding rates available" />
        ) : (
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-2">
            {funding.map((r) => <FundingRow key={r.symbol} row={r} />)}
          </div>
        )}
      </Section>

      {/* ── Open Positions ───────────────────────────────────────── */}
      <Section title="Open Positions" count={positions.length}>
        {positions.length === 0 ? (
          <EmptyRow msg="No open positions" />
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-xs font-mono">
              <thead>
                <tr className="border-b border-white/5 text-[9px] uppercase tracking-widest text-muted-foreground/50">
                  <th className="text-left py-2 pr-4">Symbol</th>
                  <th className="text-left pr-4">Side</th>
                  <th className="text-left pr-4">Strategy</th>
                  <th className="text-right pr-4">Entry</th>
                  <th className="text-right pr-4">Mark</th>
                  <th className="text-right pr-4">uPnL</th>
                  <th className="text-right pr-4">ROE</th>
                  <th className="text-right">Action</th>
                </tr>
              </thead>
              <tbody>
                {positions.map((p) => {
                  const sideKey = p.side === "LONG" ? "buy" : "sell";
                  const isBusy = closingId === `${p.symbol}:${sideKey}`;
                  return (
                  <tr key={p.id} className="border-b border-white/[0.03] hover:bg-white/[0.02]">
                    <td className="py-1.5 pr-4 font-bold">{p.symbol}</td>
                    <td className={cn("pr-4 font-bold", p.side === "LONG" ? "text-emerald-400" : "text-rose-400")}>
                      {p.side}
                    </td>
                    <td className="pr-4 text-muted-foreground/60 text-[10px]">{p.strategyTag}</td>
                    <td className="pr-4 text-right tabular-nums">{fmt(p.entryPrice)}</td>
                    <td className="pr-4 text-right tabular-nums">{fmt(p.markPrice)}</td>
                    <td className={cn("pr-4 text-right tabular-nums font-bold",
                      p.unrealizedPnL >= 0 ? "text-emerald-400" : "text-rose-400")}>
                      {formatUsd(p.unrealizedPnL)}
                    </td>
                    <td className={cn("pr-4 text-right tabular-nums",
                      p.roe >= 0 ? "text-emerald-400" : "text-rose-400")}>
                      {(p.roe * 100).toFixed(2)}%
                    </td>
                    <td className="text-right">
                      <button
                        disabled={isBusy}
                        onClick={() => {
                          if (!confirm(`Close ${p.symbol} ${p.side}?`)) return;
                          closeM.mutate({ symbol: p.symbol, side: sideKey, reason: "CockpitManual" });
                        }}
                        className={cn(
                          "px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-wider border transition-colors",
                          isBusy
                            ? "border-white/10 text-muted-foreground/40"
                            : "border-rose-400/30 text-rose-300 hover:bg-rose-500/15"
                        )}
                      >
                        {isBusy ? "…" : "Close"}
                      </button>
                    </td>
                  </tr>
                );})}
              </tbody>
            </table>
          </div>
        )}
      </Section>

      {/* ── Signal Feed ──────────────────────────────────────────── */}
      <Section title="Recent Signals" count={signals.length}>
        {signals.length === 0 ? (
          <EmptyRow msg="No recent signals" />
        ) : (
          <div className="space-y-1.5">
            {signals.map((s, i) => (
              <div
                key={s.id ?? i}
                className="flex items-start gap-3 px-3 py-2 rounded-lg bg-white/[0.02] border border-white/[0.04] text-xs"
              >
                <span className={cn(
                  "shrink-0 px-1.5 py-0.5 rounded text-[9px] font-black uppercase tracking-wider mt-0.5",
                  s.side?.toUpperCase() === "LONG" ? "bg-emerald-400/10 text-emerald-400" : "bg-rose-400/10 text-rose-400"
                )}>
                  {s.side}
                </span>
                <div className="flex-1 min-w-0">
                  <div className="flex items-baseline gap-2 flex-wrap">
                    <span className="font-bold">{s.symbol}</span>
                    <span className="text-muted-foreground/50 text-[10px]">{s.strategyTag}</span>
                    <span className="font-mono text-[10px] text-muted-foreground/60">{fmt(s.price)}</span>
                    {s.confidence > 0 && (
                      <ConfidenceBar value={s.confidence} />
                    )}
                  </div>
                  {s.reasoning && (
                    <p className="text-[10px] text-muted-foreground/60 mt-0.5 leading-relaxed line-clamp-2">
                      {s.reasoning}
                    </p>
                  )}
                </div>
                <span className="shrink-0 text-[9px] text-muted-foreground/40 font-mono">
                  {relTime(s.timestamp)}
                </span>
              </div>
            ))}
          </div>
        )}
      </Section>
    </div>
  );
}

// ── Sub-components ─────────────────────────────────────────────────────────

function PairRow({ pair: p }: { pair: ActivePairSnapshot }) {
  const stateColor =
    p.state === "LongSpread" ? "text-emerald-400 border-emerald-400/30 bg-emerald-400/10" :
    p.state === "ShortSpread" ? "text-rose-400 border-rose-400/30 bg-rose-400/10" :
    p.state === "Stop" ? "text-amber-400 border-amber-400/30 bg-amber-400/10" :
    "text-muted-foreground/60 border-white/10 bg-white/[0.02]";

  const zAbs = Math.abs(p.currentZ);
  const zFill = zAbs > 3 ? "#f05a5a" : zAbs > 2 ? "#f59e0b" : zAbs > 1 ? "#0ea5e9" : "#8b949e";
  const zPct = Math.min(100, (zAbs / 4) * 100);

  // Ring tone for |z| — same thresholds as the gauge bar colour
  const zTone: RingTone =
    zAbs > 3 ? "rose"  :
    zAbs > 2 ? "amber" :
    zAbs > 1 ? "cyan"  :
               "gray";

  // Hurst ring: lower = more mean-reverting (good for pairs). Map (1 - hurst) × 100, capped 0-100.
  const hurstFill = Math.max(0, Math.min(100, (1 - p.hurstExponent) * 100));
  const hurstTone: RingTone =
    p.hurstExponent < 0.4 ? "green" :
    p.hurstExponent < 0.5 ? "cyan"  :
                            "amber";

  // Correlation ring: |ρ| × 100, sign indicates direction. We show |ρ| as fill.
  const corrFill = Math.min(100, Math.abs(p.correlation) * 100);
  const corrTone: RingTone =
    Math.abs(p.correlation) >= 0.6 ? "green" :
    Math.abs(p.correlation) >= 0.3 ? "cyan"  :
                                     "amber";

  const cardInner = (compact: boolean) => (
    <div className={cn("rounded-xl border border-white/5 bg-white/[0.02]", compact ? "p-3" : "p-4")}>
      <div className="flex items-center justify-between mb-2">
        <div className="flex items-center gap-2">
          <span className={cn("font-bold", compact ? "text-sm" : "text-base")}>{p.symbolY}</span>
          <span className="text-muted-foreground/40 text-xs">/</span>
          <span className={cn("font-bold", compact ? "text-sm" : "text-base")}>{p.symbolX}</span>
          {!p.isCointegrated && (
            <span className="px-1.5 py-0.5 rounded bg-amber-400/10 text-amber-400 text-[8px] uppercase font-black">
              ⚠ decointegrated
            </span>
          )}
        </div>
        <span className={cn("px-2 py-0.5 rounded border text-[9px] font-black uppercase tracking-wider", stateColor)}>
          {p.state}
        </span>
      </div>

      {/* Notion-style ring summary — single-glance state */}
      <div className="flex items-start justify-around gap-2 mb-3 pt-1 pb-2 border-y border-white/[0.04]">
        <RingStat
          value={zPct}
          display={`${p.currentZ >= 0 ? "+" : ""}${p.currentZ.toFixed(2)}`}
          label="Z-Score"
          tone={zTone}
          size={compact ? "sm" : "md"}
        />
        <RingStat
          value={hurstFill}
          display={p.hurstExponent.toFixed(2)}
          label="Hurst"
          sub={p.hurstExponent < 0.5 ? "mean-rev" : "trending"}
          tone={hurstTone}
          size={compact ? "sm" : "md"}
        />
        <RingStat
          value={corrFill}
          display={`${p.correlation >= 0 ? "+" : ""}${p.correlation.toFixed(2)}`}
          label="ρ corr"
          tone={corrTone}
          size={compact ? "sm" : "md"}
        />
      </div>

      {/* Z-score gauge */}
      <div className="mb-2">
        <div className="flex items-baseline justify-between text-[10px] font-mono mb-1">
          <span className="text-muted-foreground/50">Z-Score</span>
          <span className="font-bold" style={{ color: zFill }}>
            {p.currentZ >= 0 ? "+" : ""}{p.currentZ.toFixed(3)}
          </span>
        </div>
        <div className="relative h-3 rounded-full bg-white/[0.06] overflow-hidden">
          <div className="absolute left-1/2 top-0 bottom-0 w-px bg-white/20" />
          <div
            className="absolute top-0 bottom-0 rounded-full transition-all duration-500"
            style={{
              width: `${zPct / 2}%`,
              left: p.currentZ >= 0 ? "50%" : undefined,
              right: p.currentZ < 0 ? "50%" : undefined,
              backgroundColor: zFill,
              opacity: 0.7,
            }}
          />
        </div>
        <div className="flex justify-between text-[8px] text-muted-foreground/30 mt-0.5 font-mono">
          <span>-4</span><span>-2</span><span>0</span><span>+2</span><span>+4</span>
        </div>
      </div>

      <div className="grid grid-cols-4 gap-2 text-[10px]">
        <MicroStat label="β" value={p.hedgeRatio.toFixed(3)} />
        <MicroStat label="HL" value={`${p.halfLife.toFixed(1)}h`} />
        <MicroStat label="Hurst" value={p.hurstExponent.toFixed(2)} />
        <MicroStat label="ρ" value={p.correlation.toFixed(2)} />
      </div>

      {!compact && (
        <div className="mt-4 pt-3 border-t border-white/5 grid grid-cols-2 gap-3 text-[11px] font-mono">
          <DetailRow label="Symbol Y"        value={p.symbolY} />
          <DetailRow label="Symbol X"        value={p.symbolX} />
          <DetailRow label="State"           value={p.state} />
          <DetailRow label="Cointegrated"    value={p.isCointegrated ? "✓ Yes" : "⚠ No"}
                     valueColor={p.isCointegrated ? "text-emerald-400" : "text-amber-400"} />
          <DetailRow label="Hedge ratio (β)" value={p.hedgeRatio.toFixed(4)} />
          <DetailRow label="Correlation (ρ)" value={p.correlation.toFixed(4)} />
          <DetailRow label="Hurst exponent"  value={p.hurstExponent.toFixed(3)} />
          <DetailRow label="Half-life"       value={`${p.halfLife.toFixed(1)} h`} />
          <DetailRow label="Current Z-score" value={p.currentZ.toFixed(4)} />
        </div>
      )}
      {!compact && (
        <p className="mt-4 text-[10px] font-serif italic text-muted-foreground/55 leading-relaxed">
          Pair is tradeable when |Z| ≥ 2 with Hurst &lt; 0.5 (mean-reverting),
          |ρ| ≥ 0.30, and the ADF cointegration test holds. Half-life under
          24 h is preferred so positions don't sit too long.
        </p>
      )}
    </div>
  );

  return (
    <ZoomableTile
      title={`${p.symbolY} / ${p.symbolX}`}
      subtitle={`State: ${p.state} · Cointegrated: ${p.isCointegrated ? "yes" : "no"}`}
      modalSize="max-w-3xl"
      preview={cardInner(true)}
      expanded={() => cardInner(false)}
    />
  );
}

function DetailRow({ label, value, valueColor = "text-foreground/90" }: { label: string; value: string; valueColor?: string }) {
  return (
    <div className="flex items-baseline justify-between border-b border-white/[0.04] py-1.5">
      <span className="text-muted-foreground/60 text-[10px] uppercase tracking-widest">{label}</span>
      <span className={cn("font-bold tabular-nums", valueColor)}>{value}</span>
    </div>
  );
}

function FundingRow({ row: r }: { row: ReturnType<typeof fundingApi.rates> extends Promise<infer T> ? T extends Array<infer U> ? U : never : never }) {
  const positive = r.rateBps >= 0;
  const signal = r.signal?.toLowerCase() ?? "";
  const sigColor = signal.includes("strong") ? (positive ? "text-emerald-400" : "text-rose-400") :
    signal.includes("mod") ? "text-amber-400" : "text-muted-foreground/50";
  const maxSpk = Math.max(...(r.sparklineBps ?? [1]).map(Math.abs), 1);

  return (
    <div className="rounded-xl border border-white/5 bg-white/[0.02] p-3">
      <div className="flex items-baseline justify-between mb-1.5">
        <span className="font-bold text-sm">{r.symbol}</span>
        <span className={cn("font-mono text-[11px] font-bold tabular-nums", positive ? "text-emerald-400" : "text-rose-400")}>
          {positive ? "+" : ""}{r.rateBps.toFixed(2)} bps
        </span>
      </div>
      {/* Sparkline */}
      {r.sparklineBps && r.sparklineBps.length > 1 && (
        <svg width="100%" height={28} viewBox={`0 0 ${r.sparklineBps.length} 28`} preserveAspectRatio="none" className="mb-1.5">
          {r.sparklineBps.map((v, i) => {
            const h = Math.max(1, (Math.abs(v) / maxSpk) * 24);
            const y = v >= 0 ? 26 - h : 26;
            return (
              <rect
                key={i}
                x={i}
                y={y}
                width={0.8}
                height={h}
                fill={v >= 0 ? "#3B8C68" : "#C9485F"}
                opacity={0.7}
              />
            );
          })}
          <line x1={0} x2={r.sparklineBps.length} y1={26} y2={26} stroke="rgba(255,255,255,0.1)" strokeWidth={0.3} />
        </svg>
      )}
      <span className={cn("text-[9px] uppercase font-black tracking-wider", sigColor)}>
        {r.signal ?? "—"}
      </span>
    </div>
  );
}

function ConfidenceBar({ value }: { value: number }) {
  const pct = Math.max(0, Math.min(1, value)) * 100;
  const color = pct >= 75 ? "#37A95B" : pct >= 50 ? "#F0883E" : pct >= 30 ? "#C9A13B" : "#F05A5A";
  return (
    <span className="inline-flex items-center gap-1">
      <span className="w-16 h-1.5 rounded-full bg-white/[0.08] overflow-hidden inline-block">
        <span className="h-full rounded-full block" style={{ width: `${pct}%`, backgroundColor: color }} />
      </span>
      <span className="text-[9px] font-mono" style={{ color }}>{pct.toFixed(0)}%</span>
    </span>
  );
}

function StatTile({ label, value, sub, color = "" }: { label: string; value: string; sub?: string; color?: string }) {
  return (
    <div className="rounded-xl border border-white/5 bg-white/[0.02] px-4 py-3">
      <p className="text-[9px] uppercase tracking-[0.2em] text-muted-foreground/50 mb-1">{label}</p>
      <p className={cn("font-mono text-lg font-bold leading-none", color || "text-foreground/90")}>{value}</p>
      {sub && <p className="text-[9px] text-muted-foreground/50 mt-1 leading-tight line-clamp-2">{sub}</p>}
    </div>
  );
}

function MicroStat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <span className="text-[9px] text-muted-foreground/40 uppercase tracking-widest">{label}</span>
      <div className="font-mono text-[11px] font-bold text-foreground/80">{value}</div>
    </div>
  );
}

function Section({ title, count, children }: { title: string; count: number; children: React.ReactNode }) {
  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-center gap-2 mb-3">
        <h3 className="text-[10px] font-black uppercase tracking-[0.25em] text-accent/80">{title}</h3>
        <span className="px-1.5 py-0.5 rounded bg-white/[0.06] text-[9px] font-mono text-muted-foreground/60">
          {count}
        </span>
      </div>
      {children}
    </div>
  );
}

function EmptyRow({ msg }: { msg: string }) {
  return (
    <p className="text-[11px] italic font-serif text-muted-foreground/50 text-center py-4">
      — {msg} —
    </p>
  );
}

function regimeColor(r: string | undefined): string {
  if (!r) return "";
  const l = r.toLowerCase();
  if (l.includes("trend")) return "text-emerald-400";
  if (l.includes("volatile")) return "text-rose-400";
  if (l.includes("rang")) return "text-amber-400";
  return "text-muted-foreground/80";
}

function fmt(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}

function relTime(iso: string): string {
  try {
    const diff = (Date.now() - new Date(iso).getTime()) / 1000;
    if (diff < 60) return `${Math.floor(diff)}s ago`;
    if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
    return `${Math.floor(diff / 3600)}h ago`;
  } catch { return ""; }
}
