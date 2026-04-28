import { useQuery } from "@tanstack/react-query";
import { dashboardIntelApi, type StressScenario } from "@/api/dashboardIntel";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";

/**
 * Pre-canned shock scenarios run against the current book. Each scenario
 * shows expected loss + survivability — green if recovery is plausible,
 * amber if material damage, red if account-killing.
 */
export function StressTestGauges() {
  const q = useQuery({
    queryKey: ["dashboard", "stress-test"],
    queryFn: dashboardIntelApi.stressTest,
    refetchInterval: 60_000,
  });

  const data = q.data;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-rose-400/70">
              Stress Tests
            </p>
            <SyntheticBadge source="derived" />
          </div>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 mt-0.5">
            heuristic shock × open notional · not full Monte Carlo
          </p>
        </div>
        {data && (
          <div className="text-[10px] font-mono text-muted-foreground/50">
            equity {formatUsd(data.currentEquity)} · notional {formatUsd(data.openNotional)}
          </div>
        )}
      </div>

      {q.isLoading || !data ? (
        <p className="p-6 text-xs italic font-serif text-muted-foreground/60 text-center">
          — running scenarios —
        </p>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3 p-4">
          {data.scenarios.map((s) => (
            <ScenarioCard key={s.name} scenario={s} equity={data.currentEquity} />
          ))}
        </div>
      )}
    </div>
  );
}

function ScenarioCard({ scenario: s, equity }: { scenario: StressScenario; equity: number }) {
  const survivable = s.postEquity > equity * 0.5;
  const tone =
    s.status === "ok"
      ? "border-emerald-500/30 bg-emerald-500/[0.04]"
      : s.status === "warn"
        ? "border-amber-500/30 bg-amber-500/[0.04]"
        : "border-rose-500/30 bg-rose-500/[0.04]";

  return (
    <div className={cn("rounded-lg border p-4", tone)}>
      <div className="flex items-baseline justify-between mb-2">
        <span className="font-bold text-[13px] font-serif">{s.name}</span>
        <Pill status={s.status} />
      </div>
      <p className="text-[11px] font-serif italic text-muted-foreground/70 mb-3">
        {s.description}
      </p>

      <div className="grid grid-cols-2 gap-3 text-[10px] font-mono">
        <div>
          <p className="text-[8px] uppercase tracking-widest text-muted-foreground/50 mb-0.5">
            Loss
          </p>
          <p className="font-bold tabular-nums text-rose-400 text-[14px] leading-none">
            {formatUsd(s.lossUsd)}
          </p>
          <p className="text-[10px] text-rose-400/70 tabular-nums mt-0.5">
            {s.lossPct.toFixed(2)}% equity
          </p>
        </div>
        <div>
          <p className="text-[8px] uppercase tracking-widest text-muted-foreground/50 mb-0.5">
            Post-event
          </p>
          <p
            className={cn(
              "font-bold tabular-nums text-[14px] leading-none",
              survivable ? "text-emerald-400" : "text-amber-400"
            )}
          >
            {formatUsd(s.postEquity)}
          </p>
          <p className="text-[10px] text-muted-foreground/60 mt-0.5">
            {survivable ? "survivable" : "material damage"}
          </p>
        </div>
      </div>
    </div>
  );
}

function Pill({ status }: { status: "ok" | "warn" | "fail" }) {
  const map = {
    ok: { bg: "bg-emerald-400/15 text-emerald-400 border-emerald-500/30", label: "OK" },
    warn: { bg: "bg-amber-400/15 text-amber-400 border-amber-500/30", label: "WARN" },
    fail: { bg: "bg-rose-400/15 text-rose-400 border-rose-500/30", label: "DANGER" },
  } as const;
  const v = map[status];
  return (
    <span
      className={cn(
        "px-2 py-0.5 rounded text-[8px] font-black uppercase tracking-widest border",
        v.bg
      )}
    >
      {v.label}
    </span>
  );
}
