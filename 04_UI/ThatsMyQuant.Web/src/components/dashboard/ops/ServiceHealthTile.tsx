import { useQuery } from "@tanstack/react-query";
import { systemHealthApi } from "@/api/systemHealth";
import { cn } from "@/lib/utils";

/**
 * Compact service heartbeat tile — one row per known service with state pill.
 * Reuses /api/system/health (Phase 13). Click → /health page for full detail.
 */
export function ServiceHealthTile() {
  const q = useQuery({
    queryKey: ["system", "health"],
    queryFn: systemHealthApi.get,
    refetchInterval: 10_000,
  });

  const services = q.data?.services ?? [];
  const summary = q.data?.summary;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Services
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            background workers heartbeat
          </p>
        </div>
        {summary && (
          <div className="flex items-center gap-1.5">
            <span
              className={cn(
                "w-1.5 h-1.5 rounded-full animate-pulse",
                summary.downCount === 0 && summary.staleCount === 0
                  ? "bg-emerald-400"
                  : summary.downCount === 0
                    ? "bg-amber-400"
                    : "bg-rose-400"
              )}
            />
            <span className="text-[10px] font-mono uppercase tracking-widest">
              {summary.healthyCount}/{summary.totalServices} healthy
            </span>
          </div>
        )}
      </div>

      <div className="p-3 space-y-1">
        {services.length === 0 ? (
          <p className="text-[11px] italic font-serif text-muted-foreground/50 py-3 text-center">
            — no heartbeats yet —
          </p>
        ) : (
          services.map((s) => (
            <div
              key={s.serviceName}
              className="flex items-center justify-between gap-2 px-2 py-1 rounded hover:bg-white/[0.02]"
            >
              <div className="flex items-center gap-2 min-w-0">
                <span
                  className={cn(
                    "w-1.5 h-1.5 rounded-full shrink-0",
                    s.state === "healthy"
                      ? "bg-emerald-400"
                      : s.state === "stale"
                        ? "bg-amber-400"
                        : "bg-rose-400"
                  )}
                />
                <span className="text-[11px] font-mono truncate">{s.serviceName}</span>
              </div>
              <div className="flex items-center gap-2 shrink-0 text-[10px] font-mono">
                <span className="text-muted-foreground/50 hidden md:inline">
                  {s.memoryMb.toFixed(0)} MB
                </span>
                <span
                  className={cn(
                    "tabular-nums",
                    s.ageSeconds < 60
                      ? "text-emerald-400"
                      : s.ageSeconds < 300
                        ? "text-amber-400"
                        : "text-rose-400"
                  )}
                >
                  {s.ageSeconds < 60
                    ? `${Math.round(s.ageSeconds)}s`
                    : s.ageSeconds < 3600
                      ? `${Math.round(s.ageSeconds / 60)}m`
                      : `${Math.round(s.ageSeconds / 3600)}h`}
                </span>
              </div>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
