import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { proModeApi } from "@/api/proMode";
import type { ProModeSnapshot } from "@/api/proMode";
import { cn } from "@/lib/utils";
import { formatPercentLoose, formatUsd, formatDateTime } from "@/lib/format";

const REGIMES = ["Trending", "Ranging", "Volatile", "Breakout", "MeanReverting", "Unknown"];

export function ProModePage() {
  const qc = useQueryClient();
  const [leverage, setLeverage] = useState(3);
  const [note, setNote] = useState("operator-web");

  const snapQ = useQuery({
    queryKey: ["pro-mode-snapshot"],
    queryFn: proModeApi.snapshot,
    refetchInterval: 5_000,
  });

  const armM = useMutation({
    mutationFn: () => proModeApi.arm(leverage),
    onSuccess: (data) => qc.setQueryData(["pro-mode-snapshot"], data),
  });
  const disarmM = useMutation({
    mutationFn: () => proModeApi.disarm(note),
    onSuccess: (data) => qc.setQueryData(["pro-mode-snapshot"], data),
  });
  const killM = useMutation({
    mutationFn: () => proModeApi.kill(note),
    onSuccess: (data) => qc.setQueryData(["pro-mode-snapshot"], data),
  });
  const scanM = useMutation({
    mutationFn: () => proModeApi.scan(),
    onSuccess: (data) => qc.setQueryData(["pro-mode-snapshot"], data.snapshot),
  });
  const regimeM = useMutation({
    mutationFn: (r: string) => proModeApi.setRegime(r),
    onSuccess: (data) => qc.setQueryData(["pro-mode-snapshot"], data),
  });

  const s = snapQ.data;

  return (
    <div className="space-y-8">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Pro Mode / Orchestration
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Risk Console{" "}
          <span className="text-muted-foreground/30 font-light">
            — Circuit breaker, regime throttle, opportunity scan
          </span>
        </p>
      </div>

      <StateBanner snapshot={s} />

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        <CircuitBreakerPanel snapshot={s} />
        <LeveragePanel snapshot={s} />
        <ControlsPanel
          leverage={leverage}
          setLeverage={setLeverage}
          note={note}
          setNote={setNote}
          onArm={() => armM.mutate()}
          onDisarm={() => disarmM.mutate()}
          onKill={() => killM.mutate()}
          onScan={() => scanM.mutate()}
          isArmed={s?.isArmed ?? false}
          loading={armM.isPending || disarmM.isPending || killM.isPending || scanM.isPending}
        />
      </div>

      <RegimeSelector
        currentRegime={s?.currentRegime ?? "Unknown"}
        onChange={(r) => regimeM.mutate(r)}
        isPending={regimeM.isPending}
      />

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <OpportunitiesPanel snapshot={s} />
        <CandidatePoolPanel snapshot={s} />
      </div>
    </div>
  );
}

function StateBanner({ snapshot }: { snapshot: ProModeSnapshot | undefined }) {
  if (!snapshot) {
    return (
      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl px-6 py-4 text-sm text-muted-foreground">
        Loading orchestrator snapshot…
      </div>
    );
  }
  const { isArmed, circuitBreaker } = snapshot;
  const tripped = circuitBreaker.state === "Tripped";
  const warn = circuitBreaker.state === "Warning";

  let label: string;
  let color: string;
  if (tripped) {
    label = "🛑 KILL — Circuit breaker tripped";
    color = "border-rose-500/40 bg-rose-500/10 text-rose-400";
  } else if (warn) {
    label = "⚠ WARNING — drawdown approaching trip threshold";
    color = "border-amber-500/40 bg-amber-500/10 text-amber-400";
  } else if (isArmed) {
    label = "✓ ARMED — orchestrator active";
    color = "border-emerald-500/40 bg-emerald-500/10 text-emerald-400";
  } else {
    label = "○ DISARMED — entries blocked";
    color = "border-zinc-500/30 bg-zinc-500/10 text-zinc-400";
  }
  return (
    <div className={cn("rounded-xl border backdrop-blur-xl px-6 py-4 flex items-center justify-between", color)}>
      <span className="text-sm font-black uppercase tracking-wider">{label}</span>
      <span className="text-[10px] font-mono text-muted-foreground">
        Regime: {snapshot.currentRegime} · Lev {snapshot.effectiveLeverage.toFixed(1)}x
      </span>
    </div>
  );
}

function Card({ title, eyebrow, children }: { title: string; eyebrow: string; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-muted-foreground/50 mb-1">
        {eyebrow}
      </p>
      <p className="text-base font-black tracking-tight mb-4">{title}</p>
      {children}
    </div>
  );
}

function CircuitBreakerPanel({ snapshot }: { snapshot: ProModeSnapshot | undefined }) {
  const cb = snapshot?.circuitBreaker;
  return (
    <Card eyebrow="Risk" title="Circuit Breaker">
      <Row label="State" value={cb?.state ?? "—"} valueClass={cbColor(cb?.state)} />
      <Row label="Drawdown" value={cb ? formatPercentLoose(Number(cb.realizedDrawdown)) : "—"} />
      <Row label="Peak Equity" value={cb ? formatUsd(cb.peakEquity) : "—"} />
      <Row label="Current Equity" value={cb ? formatUsd(cb.currentEquity) : "—"} />
      <Row label="Last Trip" value={cb?.lastTripUtc ? formatDateTime(cb.lastTripUtc) : "—"} />
      {cb?.lastTripReason && (
        <p className="mt-3 text-[10px] text-muted-foreground/70 leading-relaxed">{cb.lastTripReason}</p>
      )}
    </Card>
  );
}

function LeveragePanel({ snapshot }: { snapshot: ProModeSnapshot | undefined }) {
  return (
    <Card eyebrow="Leverage" title="Effective Sizing">
      <Row label="Base" value={snapshot ? `${snapshot.baseLeverage.toFixed(1)}x` : "—"} />
      <Row label="Effective" value={snapshot ? `${snapshot.effectiveLeverage.toFixed(1)}x` : "—"} valueClass="text-accent" />
      <Row label="Regime" value={snapshot?.currentRegime ?? "—"} />
      {snapshot?.leverageExplanation && (
        <p className="mt-3 text-[10px] text-muted-foreground/70 leading-relaxed">
          {snapshot.leverageExplanation}
        </p>
      )}
    </Card>
  );
}

function ControlsPanel({
  leverage,
  setLeverage,
  note,
  setNote,
  onArm,
  onDisarm,
  onKill,
  onScan,
  isArmed,
  loading,
}: {
  leverage: number;
  setLeverage: (n: number) => void;
  note: string;
  setNote: (s: string) => void;
  onArm: () => void;
  onDisarm: () => void;
  onKill: () => void;
  onScan: () => void;
  isArmed: boolean;
  loading: boolean;
}) {
  return (
    <Card eyebrow="Operator" title="Controls">
      <label className="block">
        <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/60">
          Base Leverage
        </span>
        <div className="flex items-center gap-3 mt-1">
          <input
            type="range"
            min={1}
            max={15}
            step={0.5}
            value={leverage}
            onChange={(e) => setLeverage(Number(e.target.value))}
            className="flex-1 accent-[#1E3A8A]"
          />
          <span className="font-mono text-sm w-10 text-right">{leverage.toFixed(1)}x</span>
        </div>
      </label>
      <label className="block mt-3">
        <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/60">
          Operator Note
        </span>
        <input
          type="text"
          value={note}
          onChange={(e) => setNote(e.target.value)}
          className="w-full mt-1 px-2 py-1.5 rounded bg-black/40 border border-white/10 text-xs font-mono"
        />
      </label>
      <div className="grid grid-cols-2 gap-2 mt-4">
        <button
          onClick={onArm}
          disabled={loading || isArmed}
          className="px-3 py-2 rounded text-[11px] font-black uppercase tracking-wider bg-emerald-500/15 text-emerald-400 border border-emerald-500/30 hover:bg-emerald-500/25 disabled:opacity-30 disabled:cursor-not-allowed"
        >
          ARM
        </button>
        <button
          onClick={onDisarm}
          disabled={loading || !isArmed}
          className="px-3 py-2 rounded text-[11px] font-black uppercase tracking-wider bg-zinc-500/15 text-zinc-300 border border-zinc-500/30 hover:bg-zinc-500/25 disabled:opacity-30 disabled:cursor-not-allowed"
        >
          DISARM
        </button>
        <button
          onClick={onScan}
          disabled={loading}
          className="px-3 py-2 rounded text-[11px] font-black uppercase tracking-wider bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 disabled:opacity-30"
        >
          FORCE SCAN
        </button>
        <button
          onClick={onKill}
          disabled={loading}
          className="px-3 py-2 rounded text-[11px] font-black uppercase tracking-wider bg-rose-500/15 text-rose-400 border border-rose-500/40 hover:bg-rose-500/25 disabled:opacity-30"
        >
          🛑 KILL
        </button>
      </div>
    </Card>
  );
}

function RegimeSelector({
  currentRegime,
  onChange,
  isPending,
}: {
  currentRegime: string;
  onChange: (r: string) => void;
  isPending: boolean;
}) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-muted-foreground/50 mb-3">
        Manual Regime Override
      </p>
      <div className="flex flex-wrap gap-2">
        {REGIMES.map((r) => (
          <button
            key={r}
            onClick={() => onChange(r)}
            disabled={isPending}
            className={cn(
              "px-3 py-1.5 rounded text-[11px] font-bold uppercase tracking-wider border transition-colors",
              r === currentRegime
                ? "bg-accent/15 text-accent border-accent/40"
                : "bg-white/[0.02] text-muted-foreground border-white/5 hover:text-foreground"
            )}
          >
            {r}
          </button>
        ))}
      </div>
    </div>
  );
}

function OpportunitiesPanel({ snapshot }: { snapshot: ProModeSnapshot | undefined }) {
  const ops = snapshot?.recentOpportunities ?? [];
  return (
    <Card eyebrow="Scanner" title="Recent Funding Opportunities">
      {ops.length === 0 ? (
        <p className="text-xs text-muted-foreground py-6 text-center">No opportunities in the last scan.</p>
      ) : (
        <table className="w-full text-xs">
          <thead className="text-[9px] uppercase tracking-widest text-muted-foreground/60">
            <tr>
              <th className="text-left py-2">Symbol</th>
              <th className="text-right py-2">Bps</th>
              <th className="text-left py-2">Direction</th>
              <th className="text-left py-2">Conviction</th>
              <th className="text-left py-2">Observed</th>
            </tr>
          </thead>
          <tbody>
            {ops.map((o, i) => (
              <tr key={i} className="border-t border-white/[0.04]">
                <td className="py-1.5 font-bold">{o.symbol}</td>
                <td className="py-1.5 text-right font-mono">{o.fundingBps.toFixed(2)}</td>
                <td className="py-1.5 text-muted-foreground">{o.direction}</td>
                <td className="py-1.5 text-muted-foreground">{o.conviction}</td>
                <td className="py-1.5 text-[10px] font-mono text-muted-foreground">
                  {formatDateTime(o.observedUtc)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

function CandidatePoolPanel({ snapshot }: { snapshot: ProModeSnapshot | undefined }) {
  const pool = snapshot?.candidatePool ?? [];
  return (
    <Card eyebrow="Promotion" title="Candidate Pool (7-day OOS Gate)">
      {pool.length === 0 ? (
        <p className="text-xs text-muted-foreground py-6 text-center">No candidates being tracked.</p>
      ) : (
        <table className="w-full text-xs">
          <thead className="text-[9px] uppercase tracking-widest text-muted-foreground/60">
            <tr>
              <th className="text-left py-2">Symbol</th>
              <th className="text-left py-2">Strategy</th>
              <th className="text-left py-2">State</th>
              <th className="text-right py-2">OOS Sharpe</th>
              <th className="text-right py-2">PF</th>
              <th className="text-right py-2">Trades</th>
            </tr>
          </thead>
          <tbody>
            {pool.map((c, i) => (
              <tr key={i} className="border-t border-white/[0.04]">
                <td className="py-1.5 font-bold">{c.symbol}</td>
                <td className="py-1.5 text-muted-foreground">{c.strategyTag}</td>
                <td className="py-1.5 text-accent">{c.state}</td>
                <td className="py-1.5 text-right font-mono">{c.oosSharpe.toFixed(2)}</td>
                <td className="py-1.5 text-right font-mono">{c.oosProfitFactor.toFixed(2)}</td>
                <td className="py-1.5 text-right font-mono">{c.oosTrades}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Card>
  );
}

function Row({ label, value, valueClass = "" }: { label: string; value: string; valueClass?: string }) {
  return (
    <div className="flex items-center justify-between py-1 text-xs">
      <span className="text-muted-foreground/70">{label}</span>
      <span className={cn("font-mono font-bold", valueClass)}>{value}</span>
    </div>
  );
}

function cbColor(state: string | undefined): string {
  switch (state) {
    case "Armed":
      return "text-emerald-400";
    case "Warning":
      return "text-amber-400";
    case "Tripped":
      return "text-rose-400";
    default:
      return "text-muted-foreground";
  }
}
