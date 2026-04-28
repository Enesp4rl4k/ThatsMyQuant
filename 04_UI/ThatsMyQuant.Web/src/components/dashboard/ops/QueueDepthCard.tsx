import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";

/**
 * Compact queue / cache depth panel. Real Prometheus-style metrics aren't
 * exposed yet — this card derives plausible "depth" indicators from the
 * existing endpoints (recent signal count proxy as queue depth, position
 * count as live state, signal count as inflight). When proper metrics ship,
 * swap the source.
 */
export function QueueDepthCard() {
  const signalsQ = useQuery({
    queryKey: ["signals", "recent", 50],
    queryFn: () => dashboardApi.recentSignals(50),
    refetchInterval: 10_000,
  });
  const positionsQ = useQuery({
    queryKey: ["positions"],
    queryFn: () => dashboardApi.positions(),
    refetchInterval: 5_000,
  });

  const signals = signalsQ.data ?? [];
  const positions = positionsQ.data ?? [];

  // Pending signals — those with no associated trade yet (status proxy)
  const pending = signals.filter((s) => s.status?.toLowerCase() === "pending").length;
  const live = positions.length;
  const recent = signals.length;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
              Queues
            </p>
            <SyntheticBadge source="derived" />
          </div>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 mt-0.5">
            derived from /signals + /positions · awaiting Prometheus
          </p>
        </div>
      </div>

      <div className="grid grid-cols-3 gap-3">
        <DepthGauge
          label="Pending"
          value={pending}
          tone={pending > 10 ? "warn" : pending > 0 ? "info" : "neutral"}
          sub="awaiting validation"
        />
        <DepthGauge
          label="Live"
          value={live}
          tone={live >= 6 ? "warn" : "neutral"}
          sub={`${live} open position${live === 1 ? "" : "s"}`}
        />
        <DepthGauge
          label="Recent"
          value={recent}
          tone="neutral"
          sub="last 50 signals"
        />
      </div>

      {pending > 0 && (
        <p className="mt-3 pt-3 border-t border-white/5 text-[11px] font-serif italic text-amber-400">
          {pending} signal{pending === 1 ? "" : "s"} awaiting validator decision — review if this stays elevated.
        </p>
      )}
    </div>
  );
}

function DepthGauge({
  label,
  value,
  tone,
  sub,
}: {
  label: string;
  value: number;
  tone: "neutral" | "info" | "warn" | "neg";
  sub: string;
}) {
  return (
    <div className="rounded-lg bg-white/[0.02] border border-white/5 px-3 py-2.5">
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mb-0.5">
        {label}
      </p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-[20px] leading-none",
          tone === "info" && "text-accent",
          tone === "warn" && "text-amber-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
      <p className="text-[9px] font-mono italic text-muted-foreground/45 mt-1">{sub}</p>
    </div>
  );
}
