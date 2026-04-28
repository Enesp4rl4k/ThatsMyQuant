import { useQuery } from "@tanstack/react-query";
import { dashboardIntelApi } from "@/api/dashboardIntel";
import { cn } from "@/lib/utils";

/**
 * Composite green-light card. If any check is fail → header turns red.
 * Used as the "is the system OK to trade right now" gate at the top of the
 * Operations tab.
 */
export function PreFlightChecksCard() {
  const q = useQuery({
    queryKey: ["dashboard", "preflight"],
    queryFn: dashboardIntelApi.preflight,
    refetchInterval: 30_000,
  });

  const data = q.data;

  return (
    <div
      className={cn(
        "rounded-xl border backdrop-blur-xl overflow-hidden transition-colors",
        !data
          ? "border-white/5 bg-[#0D1117]/60"
          : data.greenLight
            ? "border-emerald-500/30 bg-emerald-500/[0.04]"
            : data.failedCount > 0
              ? "border-rose-500/30 bg-rose-500/[0.04]"
              : "border-amber-500/30 bg-amber-500/[0.04]"
      )}
    >
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p
            className={cn(
              "text-[9px] font-black uppercase tracking-[0.3em] mb-0.5",
              !data
                ? "text-accent/70"
                : data.greenLight
                  ? "text-emerald-400/80"
                  : data.failedCount > 0
                    ? "text-rose-400/80"
                    : "text-amber-400/80"
            )}
          >
            Pre-Flight
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/70">
            {data
              ? data.greenLight
                ? "all systems go"
                : data.failedCount > 0
                  ? `${data.failedCount} hard issue${data.failedCount === 1 ? "" : "s"} · do not trade`
                  : `${data.warnCount} warning${data.warnCount === 1 ? "" : "s"} · review before next session`
              : "running checks…"}
          </p>
        </div>
        {data && (
          <div className="flex items-center gap-1.5">
            <span
              className={cn(
                "w-2 h-2 rounded-full",
                data.greenLight
                  ? "bg-emerald-400 animate-pulse"
                  : data.failedCount > 0
                    ? "bg-rose-400 animate-pulse"
                    : "bg-amber-400"
              )}
            />
            <span className="text-[10px] font-black uppercase tracking-widest">
              {data.greenLight ? "GO" : data.failedCount > 0 ? "STOP" : "CAUTION"}
            </span>
          </div>
        )}
      </div>

      <ul className="divide-y divide-white/[0.04]">
        {!data
          ? null
          : data.checks.map((c) => (
              <li key={c.id} className="px-4 py-2 flex items-center gap-3">
                <Pill status={c.status} />
                <div className="min-w-0 flex-1">
                  <p className="text-[12px] font-bold">{c.title}</p>
                  <p className="text-[10px] font-mono italic text-muted-foreground/60 truncate">
                    {c.detail}
                  </p>
                </div>
              </li>
            ))}
      </ul>
    </div>
  );
}

function Pill({ status }: { status: "ok" | "warn" | "fail" }) {
  const map = {
    ok: { bg: "bg-emerald-400/15 text-emerald-400 border-emerald-500/30", label: "OK" },
    warn: { bg: "bg-amber-400/15 text-amber-400 border-amber-500/30", label: "WARN" },
    fail: { bg: "bg-rose-400/15 text-rose-400 border-rose-500/30", label: "FAIL" },
  } as const;
  const v = map[status];
  return (
    <span
      className={cn(
        "shrink-0 px-2 py-0.5 rounded text-[8px] font-black uppercase tracking-widest border w-12 text-center",
        v.bg
      )}
    >
      {v.label}
    </span>
  );
}
