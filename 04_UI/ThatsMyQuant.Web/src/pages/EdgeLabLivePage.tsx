import { useQuery } from "@tanstack/react-query";
import { edgeLabApi, type EdgeLabStrategyRow, type MakerFillRatioState } from "@/api/edgelab";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";

export function EdgeLabLivePage() {
  const liveQ = useQuery({
    queryKey: ["edgelab-live"],
    queryFn: () => edgeLabApi.live(),
    refetchInterval: 15_000,
  });

  const snap = liveQ.data;

  return (
    <div className="space-y-8">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          EdgeLab
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Live{" "}
          <span className="text-muted-foreground/30 font-light">
            — {snap?.strategies.length ?? 0} strategies · auto-refresh 15s
          </span>
        </p>
      </div>

      {liveQ.isLoading && (
        <div className="p-12 text-center text-sm text-muted-foreground">Loading EdgeLab state…</div>
      )}
      {liveQ.isError && (
        <div className="p-12 text-center text-sm text-rose-400">
          Failed to load /api/edgelab/live.
        </div>
      )}

      {snap && (
        <>
          <MakerFillRatioCard mfr={snap.maker_fill_ratio} />
          <StrategiesTable rows={snap.strategies} killSwitch={snap.edge_kill_switch?.Strategies} />
          <HrpCard hrp={snap.hrp_allocator} />
        </>
      )}
    </div>
  );
}

function StrategiesTable({
  rows,
  killSwitch,
}: {
  rows: EdgeLabStrategyRow[];
  killSwitch?: Record<string, unknown> | undefined;
}) {
  if (rows.length === 0) {
    return (
      <div className="p-12 text-center text-sm text-muted-foreground rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl">
        No EdgeLab strategies configured.
      </div>
    );
  }

  const fmtAge = (sec: number | null) => {
    if (sec === null || sec === undefined) return "—";
    if (sec < 60) return `${sec.toFixed(0)}s`;
    if (sec < 3600) return `${(sec / 60).toFixed(0)}m`;
    if (sec < 86400) return `${(sec / 3600).toFixed(1)}h`;
    return `${(sec / 86400).toFixed(1)}d`;
  };

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60">
            <th className="text-left px-5 py-3 font-black">Strategy</th>
            <th className="text-right px-3 py-3 font-black">Weight</th>
            <th className="text-left px-3 py-3 font-black">Status</th>
            <th className="text-left px-3 py-3 font-black">State</th>
            <th className="text-right px-3 py-3 font-black">Target</th>
            <th className="text-right px-3 py-3 font-black">Signal Age</th>
            <th className="text-right px-3 py-3 font-black">Stale@</th>
            <th className="text-right px-3 py-3 font-black">BT Sharpe</th>
            <th className="text-right px-3 py-3 font-black">Realized</th>
            <th className="text-left px-3 py-3 font-black">Canary</th>
            <th className="text-left px-5 py-3 font-black">Kill?</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const targetEntries = r.target_weights ? Object.entries(r.target_weights) : [];
            const targetStr = targetEntries
              .map(([sym, w]) => `${sym.replace("USDT", "")}=${w > 0 ? "+" : ""}${w.toFixed(2)}`)
              .join(" ");
            const killed = killSwitch && r.name in killSwitch;
            return (
              <tr
                key={r.name}
                className={cn(
                  "border-b border-white/[0.02] hover:bg-white/[0.02]",
                  r.stale && "bg-rose-950/20"
                )}
              >
                <td className="px-5 py-3 font-bold font-mono text-xs">{r.name}</td>
                <td className="px-3 py-3 text-right font-mono">
                  {r.weight !== null ? `${(r.weight * 100).toFixed(0)}%` : "—"}
                </td>
                <td className="px-3 py-3 text-xs font-mono">
                  <span
                    className={cn(
                      "px-1.5 py-0.5 rounded text-[10px]",
                      r.promotion_status === "verified"
                        ? "bg-emerald-950/40 text-emerald-300"
                        : r.promotion_status === "soft-promote"
                          ? "bg-amber-950/40 text-amber-300"
                          : "bg-white/5 text-muted-foreground"
                    )}
                  >
                    {r.promotion_status ?? "—"}
                  </span>
                </td>
                <td className="px-3 py-3 text-xs font-mono text-muted-foreground">
                  {r.state ?? "—"}
                </td>
                <td className="px-3 py-3 text-right font-mono text-xs text-muted-foreground">
                  {targetStr || "—"}
                </td>
                <td
                  className={cn(
                    "px-3 py-3 text-right font-mono text-xs",
                    r.stale ? "text-rose-400" : "text-muted-foreground"
                  )}
                >
                  {fmtAge(r.signal_age_sec)}
                </td>
                <td className="px-3 py-3 text-right font-mono text-xs text-muted-foreground/60">
                  {r.stale_alert_hours !== null ? `${r.stale_alert_hours}h` : "—"}
                </td>
                <td className="px-3 py-3 text-right font-mono text-xs text-muted-foreground">
                  {r.expected_sharpe_ann !== null ? r.expected_sharpe_ann.toFixed(2) : "—"}
                </td>
                <td
                  className={cn(
                    "px-3 py-3 text-right font-mono text-xs",
                    r.calibration
                      ? r.calibration.realized.sharpe_ann >= 0.4
                        ? "text-emerald-400"
                        : "text-rose-400"
                      : "text-muted-foreground/40"
                  )}
                  title={r.calibration ? `${r.calibration.n_realized_days}d, ret ${(r.calibration.realized.total_return * 100).toFixed(1)}%, DD ${(r.calibration.realized.max_drawdown * 100).toFixed(1)}%` : "no calibration data"}
                >
                  {r.calibration ? r.calibration.realized.sharpe_ann.toFixed(2) : "—"}
                </td>
                <td className="px-3 py-3 text-xs font-mono">
                  {r.calibration ? (
                    r.calibration.promotion_gate.live_canary_eligible ? (
                      <span className="text-emerald-400">PASS</span>
                    ) : (
                      <span className="text-amber-400/70">no</span>
                    )
                  ) : (
                    <span className="text-muted-foreground/40">—</span>
                  )}
                </td>
                <td className="px-5 py-3 text-xs">
                  {killed ? (
                    <span className="text-rose-400 font-bold">KILLED</span>
                  ) : (
                    <span className="text-emerald-400">ok</span>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function MakerFillRatioCard({ mfr }: { mfr: MakerFillRatioState | null }) {
  if (!mfr) {
    return (
      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-6">
        <div className="text-[10px] uppercase tracking-[0.3em] text-muted-foreground/60 mb-2">
          Maker Fill Ratio (entry-only, last {30}d)
        </div>
        <div className="text-sm text-muted-foreground">No state file yet.</div>
      </div>
    );
  }
  const ratio = mfr.EntryRatio ?? 0;
  const verdict =
    mfr.EntryRatio === null
      ? "NO DATA"
      : ratio >= mfr.Threshold
        ? "OK"
        : "QUEUE-RACE-LOST";
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-6">
      <div className="flex items-baseline justify-between">
        <div>
          <div className="text-[10px] uppercase tracking-[0.3em] text-muted-foreground/60 mb-1">
            Maker Fill Ratio (entry-only, last {mfr.LookbackDays}d)
          </div>
          <div
            className={cn(
              "text-4xl font-black font-mono",
              mfr.EntryRatio === null
                ? "text-muted-foreground"
                : ratio >= mfr.Threshold
                  ? "text-emerald-400"
                  : "text-rose-400"
            )}
          >
            {mfr.EntryRatio === null ? "—" : (ratio * 100).toFixed(1) + "%"}
          </div>
          <div className="text-xs text-muted-foreground mt-1 font-mono">
            entries: M={mfr.EntryMakerFills} T={mfr.EntryTakerFills} · market exits filtered: {mfr.MarketExitFills}
          </div>
        </div>
        <div className="text-right">
          <div className="text-[10px] uppercase tracking-[0.3em] text-muted-foreground/60">verdict</div>
          <div
            className={cn(
              "text-sm font-bold font-mono mt-1",
              verdict === "OK"
                ? "text-emerald-400"
                : verdict === "QUEUE-RACE-LOST"
                  ? "text-rose-400"
                  : "text-muted-foreground"
            )}
          >
            {verdict}
          </div>
          <div className="text-[10px] text-muted-foreground/60 mt-1 font-mono">
            threshold {(mfr.Threshold * 100).toFixed(0)}% · computed {formatDateTime(mfr.ComputedAtUtc)}
          </div>
        </div>
      </div>
      {mfr.Symbols && mfr.Symbols.length > 0 && (
        <div className="mt-4 pt-4 border-t border-white/5 grid grid-cols-3 gap-2 text-xs font-mono">
          {mfr.Symbols.slice(0, 9).map((s) => (
            <div
              key={s.Symbol}
              className={cn(
                "flex justify-between px-2 py-1 rounded",
                s.BelowThreshold && (s.EntryFills ?? 0) >= 3
                  ? "bg-rose-950/20 text-rose-300"
                  : "text-muted-foreground"
              )}
            >
              <span>{s.Symbol.replace("USDT", "")}</span>
              <span>
                {s.EntryRatio === null ? "n/a" : (s.EntryRatio * 100).toFixed(0) + "%"}{" "}
                <span className="text-muted-foreground/40">
                  ({s.EntryMakerFills}M/{s.EntryTakerFills}T)
                </span>
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function HrpCard({ hrp }: { hrp: EdgeLabLiveSnapshotHrp | null }) {
  if (!hrp) return null;
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-6">
      <div className="flex items-baseline justify-between mb-3">
        <div className="text-[10px] uppercase tracking-[0.3em] text-muted-foreground/60">
          HRP Allocator (last {hrp.LookbackDays}d)
        </div>
        <div className="text-[10px] font-mono text-muted-foreground/60">
          {hrp.Verdict} · computed {formatDateTime(hrp.ComputedAtUtc)}
        </div>
      </div>
      <div className="grid grid-cols-2 gap-2 text-xs font-mono">
        {hrp.Strategies.map((s) => (
          <div key={s.Tag} className="flex justify-between px-2 py-1 rounded">
            <span className="text-muted-foreground">{s.Tag}</span>
            <span>
              {(s.HrpWeight * 100).toFixed(1)}%
              <span className="text-muted-foreground/40"> (n={s.TradeCount})</span>
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}

type EdgeLabLiveSnapshotHrp = NonNullable<
  Awaited<ReturnType<typeof edgeLabApi.live>>["hrp_allocator"]
>;
