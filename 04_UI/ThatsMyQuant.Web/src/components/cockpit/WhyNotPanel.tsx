import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { pairsApi } from "@/api/pairs";
import { fundingApi } from "@/api/funding";
import { dashboardApi } from "@/api/dashboard";
import { proModeApi } from "@/api/proMode";
import { cn } from "@/lib/utils";

/**
 * "Why is the bot quiet right now?" diagnostic. Synthesises a single
 * unified explanation from existing live state:
 *
 *   • Active pair states + z-distance to entry
 *   • Funding rates + threshold distance
 *   • Recent rejected signals (REST-based audit trail)
 *   • Pro-mode state (CB tripped, regime, sizing)
 *
 * The output is a list of "evaluation facts" annotated with severity:
 *   • IDLE  (gray) — not close to firing, normal quiet state
 *   • CLOSE (cyan) — within 50% of threshold, watching
 *   • READY (amber/green) — over threshold, bot WOULD enter on next tick
 *
 * If the bot is gated by something OTHER than threshold (CB tripped, regime
 * blocking, market hours), that's surfaced first as a banner.
 */
export function WhyNotPanel() {
  const pairsQ   = useQuery({ queryKey: ["whynot", "pairs"],   queryFn: pairsApi.active,         refetchInterval: 5_000 });
  const fundingQ = useQuery({ queryKey: ["whynot", "funding"], queryFn: () => fundingApi.rates(), refetchInterval: 10_000 });
  const proQ     = useQuery({ queryKey: ["whynot", "pro"],     queryFn: proModeApi.snapshot,       refetchInterval: 5_000 });
  const sigQ     = useQuery({ queryKey: ["whynot", "signals"], queryFn: () => dashboardApi.recentSignals(15), refetchInterval: 8_000 });

  const proSnap = proQ.data;
  const cbState = proSnap?.circuitBreaker?.state ?? "Armed";
  const isArmed = proSnap?.isArmed ?? false;
  const regime  = proSnap?.currentRegime ?? "Unknown";

  // High-level gates — bot IS doing nothing because of these
  const blockers = useMemo(() => {
    const out: { tone: "rose" | "amber" | "cyan"; text: string }[] = [];
    if (!isArmed) out.push({ tone: "amber", text: "Pro Mode is DISARMED — no automatic entries until armed." });
    if (cbState.toLowerCase().includes("trip")) out.push({ tone: "rose", text: `Circuit Breaker is TRIPPED (state=${cbState}) — entries blocked, exits allowed.` });
    if (regime.toLowerCase().includes("volatile")) out.push({ tone: "amber", text: `Regime is VOLATILE — sizing reduced, pairs/funding may be down-throttled.` });
    return out;
  }, [isArmed, cbState, regime]);

  // Per-strategy evaluation facts
  const facts = useMemo(() => {
    const out: { strategy: string; symbol: string; tier: "IDLE" | "CLOSE" | "READY"; line: string }[] = [];
    const entryZ = pairsQ.data?.entryZ ?? 2.0;

    for (const p of (pairsQ.data?.pairs ?? [])) {
      const cur = Math.abs(p.currentZ);
      const need = Math.max(0, entryZ - cur);
      const tier: "IDLE" | "CLOSE" | "READY" =
        cur >= entryZ          ? "READY" :
        cur >= entryZ * 0.5    ? "CLOSE" : "IDLE";
      out.push({
        strategy: "Pairs",
        symbol: `${p.symbolY}/${p.symbolX}`,
        tier,
        line: tier === "READY"
          ? `|z| = ${cur.toFixed(2)} ≥ ${entryZ}σ ENTRY threshold · would emit signal next tick`
          : `|z| = ${cur.toFixed(2)} < ${entryZ}σ entry threshold · need +${need.toFixed(2)}σ more`,
      });
    }

    const fundingThr = 1.0;
    for (const r of (fundingQ.data ?? [])) {
      const cur = Math.abs(r.rateBps);
      const need = Math.max(0, fundingThr - cur);
      const tier: "IDLE" | "CLOSE" | "READY" =
        cur >= fundingThr       ? "READY" :
        cur >= fundingThr * 0.5 ? "CLOSE" : "IDLE";
      out.push({
        strategy: "Funding",
        symbol: r.symbol,
        tier,
        line: tier === "READY"
          ? `rate = ${r.rateBps.toFixed(2)} bp ≥ ${fundingThr} bp threshold · would emit signal next tick`
          : `rate = ${r.rateBps.toFixed(2)} bp < ${fundingThr} bp threshold · need +${need.toFixed(2)} bp more`,
      });
    }

    return out.sort((a, b) => tierWeight(b.tier) - tierWeight(a.tier));
  }, [pairsQ.data, fundingQ.data]);

  // Recent rejected signals (the audit trail)
  const rejects = useMemo(() => {
    return (sigQ.data ?? [])
      .filter((s) => s.status === "Rejected" || s.status === "Failed")
      .slice(0, 8);
  }, [sigQ.data]);

  const readyCount = facts.filter((f) => f.tier === "READY").length;
  const closeCount = facts.filter((f) => f.tier === "CLOSE").length;

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <div className="flex items-baseline justify-between mb-3">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          🤔 Why-Not Panel
        </p>
        <p className="text-[10px] font-mono text-muted-foreground/45">
          {readyCount} ready · {closeCount} close · {facts.length} watched
        </p>
      </div>

      {/* High-level blockers */}
      {blockers.length > 0 && (
        <div className="space-y-1 mb-3">
          {blockers.map((b, i) => (
            <p
              key={i}
              className={cn(
                "px-2.5 py-1.5 rounded-md border text-[10px] font-mono leading-relaxed",
                b.tone === "rose"
                  ? "border-rose-400/30 bg-rose-500/10 text-rose-200"
                  : b.tone === "amber"
                    ? "border-amber-400/30 bg-amber-500/10 text-amber-200"
                    : "border-cyan-400/25 bg-cyan-500/10 text-cyan-200"
              )}
            >
              ⚠ {b.text}
            </p>
          ))}
        </div>
      )}

      {/* Per-strategy facts */}
      {facts.length > 0 && (
        <div className="space-y-1 mb-3">
          {facts.map((f, i) => (
            <div
              key={`${f.strategy}-${f.symbol}-${i}`}
              className={cn(
                "grid grid-cols-[60px_140px_56px_1fr] items-baseline gap-2 px-2 py-1 rounded text-[10px] font-mono",
                f.tier === "READY" && "border border-emerald-400/30 bg-emerald-500/10 animate-pulse",
                f.tier === "CLOSE" && "border border-amber-400/20 bg-amber-500/[0.05]",
                f.tier === "IDLE"  && "border border-white/5 bg-white/[0.01]"
              )}
            >
              <span className={cn(
                "px-1.5 py-0 rounded text-[8px] uppercase tracking-widest font-black text-center",
                f.tier === "READY" ? "bg-emerald-500/20 text-emerald-200" :
                f.tier === "CLOSE" ? "bg-amber-500/15 text-amber-200" :
                                     "bg-white/[0.04] text-muted-foreground/55"
              )}>
                {f.tier}
              </span>
              <span className="font-bold truncate">{f.symbol}</span>
              <span className="text-cyan-400/55 uppercase tracking-widest text-[8px]">{f.strategy}</span>
              <span className={cn(
                "truncate text-right",
                f.tier === "READY" ? "text-emerald-200" :
                f.tier === "CLOSE" ? "text-amber-200/80" :
                                     "text-muted-foreground/55"
              )}>
                {f.line}
              </span>
            </div>
          ))}
        </div>
      )}

      {/* Recent rejections audit */}
      {rejects.length > 0 && (
        <div className="mt-3 pt-3 border-t border-white/[0.05]">
          <p className="text-[8px] font-mono uppercase tracking-widest text-muted-foreground/45 mb-1.5">
            Recent rejected signals (audit trail)
          </p>
          <div className="space-y-0.5">
            {rejects.map((s) => (
              <div key={s.id} className="grid grid-cols-[60px_60px_70px_1fr] gap-2 text-[9px] font-mono items-baseline">
                <span className="text-muted-foreground/45">{fmtTime(s.timestamp)}</span>
                <span className="font-bold truncate">{s.symbol}</span>
                <span className="text-rose-400/65 uppercase tracking-widest text-[8px]">{s.status}</span>
                <span className="truncate text-muted-foreground/65" title={s.reasoning}>{s.reasoning || "—"}</span>
              </div>
            ))}
          </div>
        </div>
      )}

      <p className="mt-3 pt-3 border-t border-white/[0.04] text-[9px] font-serif italic text-muted-foreground/45 leading-relaxed">
        Bot is INTENTIONALLY conservative on paper. Pairs need |z| ≥ 2σ, Funding needs |rate| ≥ 1 bp.
        Most ticks are quiet — that's correct behaviour. The READY tier shows opportunities about to fire.
      </p>
    </div>
  );
}

function tierWeight(t: "IDLE" | "CLOSE" | "READY"): number {
  return t === "READY" ? 2 : t === "CLOSE" ? 1 : 0;
}

function fmtTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  } catch { return ""; }
}
