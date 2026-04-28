import { useQuery } from "@tanstack/react-query";
import { pairsApi } from "@/api/pairs";
import { fundingApi } from "@/api/funding";
import { proModeApi } from "@/api/proMode";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";

/**
 * Compact bot-status row for Trade Mode header. Shows:
 *   • Pairs scan status + active count
 *   • Funding active opportunities
 *   • Pro-mode arm + circuit-breaker state
 *   • Current market regime
 *   • Heartbeat dot (green pulsing) — fed by recent-signal timestamp
 *
 * All data already exposed via existing dashboard / pairs / funding / pro-mode
 * endpoints. Refreshes every 5 s.
 */
export function BotStatusStrip() {
  const pairsQ   = useQuery({ queryKey: ["bot-status", "pairs"],   queryFn: pairsApi.active,    refetchInterval: 5_000 });
  const fundingQ = useQuery({ queryKey: ["bot-status", "funding"], queryFn: () => fundingApi.rates(), refetchInterval: 10_000 });
  const proQ     = useQuery({ queryKey: ["bot-status", "pro"],     queryFn: proModeApi.snapshot, refetchInterval: 5_000 });
  const sigQ     = useQuery({ queryKey: ["bot-status", "signals"], queryFn: () => dashboardApi.recentSignals(5), refetchInterval: 5_000 });

  const pairsCount    = pairsQ.data?.pairs?.length ?? 0;
  const fundingActive = (fundingQ.data ?? []).filter((r) => Math.abs(r.rateBps) >= 1).length;
  const fundingTotal  = (fundingQ.data ?? []).length;

  const isArmed   = proQ.data?.isArmed ?? false;
  const cbState   = proQ.data?.circuitBreaker?.state ?? "—";
  const regime    = proQ.data?.currentRegime ?? "—";

  // Heartbeat: how long since last signal
  const lastSig   = sigQ.data?.[0]?.timestamp;
  const heartbeatMs = lastSig ? Date.now() - new Date(lastSig).getTime() : Infinity;
  const isHeartbeatFresh = heartbeatMs < 5 * 60 * 1000; // < 5 min = alive

  const cbColor =
    cbState.toLowerCase().includes("trip")    ? "text-rose-300 border-rose-400/30 bg-rose-500/15"  :
    cbState.toLowerCase().includes("warn")    ? "text-amber-300 border-amber-400/30 bg-amber-500/15" :
                                                "text-emerald-300 border-emerald-400/30 bg-emerald-500/15";
  const regimeColor =
    regime.toLowerCase().includes("trending") ? "text-emerald-300" :
    regime.toLowerCase().includes("volatile") ? "text-rose-300"    :
    regime.toLowerCase().includes("ranging")  ? "text-amber-300"   :
                                                "text-muted-foreground/70";

  return (
    <div className="flex items-center gap-3 px-3 py-1.5 rounded-md border border-white/[0.06] bg-white/[0.02] text-[10px] font-mono">
      {/* Heartbeat */}
      <div className="flex items-center gap-1.5">
        <span
          className={cn(
            "w-1.5 h-1.5 rounded-full inline-block",
            isHeartbeatFresh ? "bg-emerald-400" : "bg-rose-400"
          )}
          style={isHeartbeatFresh ? { animation: "pulse 1.6s ease-in-out infinite" } : undefined}
        />
        <span className="text-muted-foreground/60 uppercase tracking-widest text-[9px]">
          {isHeartbeatFresh ? "Live" : "Stale"}
        </span>
      </div>

      <Sep />
      <Stat label="Pairs"   value={`${pairsCount} active`}                   tone="cyan" />
      <Sep />
      <Stat label="Funding" value={`${fundingActive}/${fundingTotal}`}        tone="pink" />
      <Sep />
      <span className={cn("inline-flex items-center gap-1 px-1.5 py-0.5 rounded border uppercase tracking-widest text-[9px] font-black",
        isArmed ? "text-emerald-300 border-emerald-400/30 bg-emerald-500/15"
                : "text-muted-foreground/50 border-white/10 bg-white/[0.03]")}>
        ● {isArmed ? "Armed" : "Disarmed"}
      </span>
      <Sep />
      <span className={cn("px-1.5 py-0.5 rounded border uppercase tracking-widest text-[9px] font-black", cbColor)}>
        CB {cbState}
      </span>
      <Sep />
      <Stat label="Regime"  value={regime}                                    valueColor={regimeColor} />
    </div>
  );
}

function Stat({ label, value, tone, valueColor }: { label: string; value: string; tone?: "cyan" | "pink"; valueColor?: string }) {
  const labelColor =
    tone === "cyan" ? "text-cyan-400/60" :
    tone === "pink" ? "text-pink-400/60" :
                       "text-muted-foreground/55";
  return (
    <span className="flex items-baseline gap-1">
      <span className={cn("uppercase tracking-widest text-[9px]", labelColor)}>{label}</span>
      <span className={cn("font-bold tabular-nums", valueColor ?? "text-foreground/85")}>{value}</span>
    </span>
  );
}

function Sep() {
  return <span className="w-px h-3 bg-white/[0.07]" aria-hidden />;
}
