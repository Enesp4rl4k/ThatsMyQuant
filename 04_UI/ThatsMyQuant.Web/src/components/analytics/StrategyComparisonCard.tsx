import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi, type StrategyBreakdown } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsd, pnlColor } from "@/lib/format";

type MetricKey =
  | "tradeCount"
  | "winRate"
  | "profitFactor"
  | "sharpe"
  | "netPnl"
  | "averagePnl"
  | "maxWin"
  | "maxLoss";

interface MetricSpec {
  key: MetricKey;
  label: string;
  format: (v: number) => string;
  /** "max" → higher is better (green delta); "min" → lower is better. */
  direction: "max" | "min";
  /** Use absolute value when comparing (e.g. for maxLoss where less negative wins). */
  absForRanking?: boolean;
}

const METRICS: MetricSpec[] = [
  { key: "tradeCount", label: "Trades", format: (v) => v.toFixed(0), direction: "max" },
  { key: "winRate", label: "Win Rate", format: (v) => `${(v * 100).toFixed(1)}%`, direction: "max" },
  { key: "profitFactor", label: "Profit Factor", format: (v) => v.toFixed(2), direction: "max" },
  { key: "sharpe", label: "Sharpe", format: (v) => v.toFixed(2), direction: "max" },
  { key: "netPnl", label: "Net P&L", format: formatUsd, direction: "max" },
  { key: "averagePnl", label: "Avg P&L", format: formatUsd, direction: "max" },
  { key: "maxWin", label: "Best Trade", format: formatUsd, direction: "max" },
  { key: "maxLoss", label: "Worst Trade", format: formatUsd, direction: "max" }, // less negative wins
];

export function StrategyComparisonCard() {
  const q = useQuery({
    queryKey: ["trades", "strategy-breakdown"],
    queryFn: dashboardApi.strategyBreakdown,
    refetchInterval: 30_000,
  });

  const strategies = q.data ?? [];
  const [leftTag, setLeftTag] = useState<string>("");
  const [rightTag, setRightTag] = useState<string>("");

  // Auto-pick first two strategies on load
  useEffect(() => {
    if (strategies.length >= 1 && !leftTag) setLeftTag(strategies[0].strategyTag);
    if (strategies.length >= 2 && !rightTag) setRightTag(strategies[1].strategyTag);
  }, [strategies, leftTag, rightTag]);

  const left = useMemo(
    () => strategies.find((s) => s.strategyTag === leftTag) ?? null,
    [strategies, leftTag]
  );
  const right = useMemo(
    () => strategies.find((s) => s.strategyTag === rightTag) ?? null,
    [strategies, rightTag]
  );

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-4">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Head-to-Head
          </p>
          <p className="text-sm font-bold">
            Strategy Comparison{" "}
            <span className="text-muted-foreground/40 font-light text-xs">
              — pick two strategies to delta their metrics
            </span>
          </p>
        </div>
      </div>

      {q.isLoading ? (
        <p className="text-xs text-muted-foreground/60">Loading breakdown…</p>
      ) : strategies.length < 2 ? (
        <p className="text-xs text-muted-foreground/40 py-12 text-center">
          Need at least 2 strategies with closed trades to compare.
        </p>
      ) : (
        <>
          {/* selectors */}
          <div className="grid grid-cols-2 gap-3 mb-4">
            <StrategyPicker
              label="Strategy A"
              value={leftTag}
              onChange={setLeftTag}
              options={strategies}
              tone="left"
            />
            <StrategyPicker
              label="Strategy B"
              value={rightTag}
              onChange={setRightTag}
              options={strategies}
              tone="right"
            />
          </div>

          {/* table */}
          <div className="rounded-lg border border-white/5 overflow-hidden">
            <table className="w-full text-xs">
              <thead className="bg-white/[0.02] border-b border-white/5">
                <tr>
                  <th className="text-left px-3 py-2 text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
                    Metric
                  </th>
                  <th className="text-right px-3 py-2 text-[9px] font-black uppercase tracking-widest text-accent/70">
                    {left?.strategyTag ?? "—"}
                  </th>
                  <th className="text-right px-3 py-2 text-[9px] font-black uppercase tracking-widest text-amber-300/70">
                    {right?.strategyTag ?? "—"}
                  </th>
                  <th className="text-right px-3 py-2 text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
                    Δ (B − A)
                  </th>
                </tr>
              </thead>
              <tbody>
                {METRICS.map((m) => (
                  <ComparisonRow key={m.key} metric={m} left={left} right={right} />
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}

function StrategyPicker({
  label,
  value,
  onChange,
  options,
  tone,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: StrategyBreakdown[];
  tone: "left" | "right";
}) {
  return (
    <div>
      <p
        className={cn(
          "text-[9px] font-black uppercase tracking-widest mb-1",
          tone === "left" ? "text-accent/70" : "text-amber-300/70"
        )}
      >
        {label}
      </p>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className={cn(
          "w-full bg-[#070A0F] border rounded px-3 py-2 text-sm font-mono outline-none transition-colors",
          tone === "left"
            ? "border-accent/20 focus:border-accent/50"
            : "border-amber-500/20 focus:border-amber-400/50"
        )}
      >
        <option value="">— select —</option>
        {options.map((s) => (
          <option key={s.strategyTag} value={s.strategyTag}>
            {s.strategyTag} ({s.tradeCount})
          </option>
        ))}
      </select>
    </div>
  );
}

function ComparisonRow({
  metric,
  left,
  right,
}: {
  metric: MetricSpec;
  left: StrategyBreakdown | null;
  right: StrategyBreakdown | null;
}) {
  const lv = left ? (left[metric.key] as number) : null;
  const rv = right ? (right[metric.key] as number) : null;
  const delta = lv !== null && rv !== null ? rv - lv : null;
  const winnerIsRight = lv !== null && rv !== null ? rv > lv : null;
  // For "max" direction: B wins if B > A (delta > 0).
  const deltaGood = delta === null ? null : delta > 0;

  return (
    <tr className="border-b border-white/[0.03] last:border-0">
      <td className="px-3 py-2 text-muted-foreground/70 font-medium">{metric.label}</td>
      <td
        className={cn(
          "px-3 py-2 text-right font-mono font-bold tabular-nums",
          winnerIsRight === false && "text-emerald-400",
          metric.key === "netPnl" && lv !== null && pnlColor(lv)
        )}
      >
        {lv !== null ? metric.format(lv) : "—"}
      </td>
      <td
        className={cn(
          "px-3 py-2 text-right font-mono font-bold tabular-nums",
          winnerIsRight === true && "text-emerald-400",
          metric.key === "netPnl" && rv !== null && pnlColor(rv)
        )}
      >
        {rv !== null ? metric.format(rv) : "—"}
      </td>
      <td
        className={cn(
          "px-3 py-2 text-right font-mono tabular-nums",
          deltaGood === true && "text-emerald-400",
          deltaGood === false && "text-rose-400",
          deltaGood === null && "text-muted-foreground/40"
        )}
      >
        {delta !== null ? `${delta >= 0 ? "+" : ""}${metric.format(Math.abs(delta) * Math.sign(delta))}` : "—"}
      </td>
    </tr>
  );
}
