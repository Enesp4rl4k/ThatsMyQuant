import { useMutation, useQueryClient } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import type { Position } from "@/api/types";

interface Props {
  positions: Position[];
  livePrice: number;
}

/**
 * Per-open-position risk meters. For each position shows:
 *   • A horizontal segmented bar with three colored zones:
 *       SL ── Entry ── current price ── TP
 *     The cursor (white tick) marks the current mark-price position; the
 *     filled segment behind it shows progress along the trade arc.
 *   • Distance % to SL (rose) and TP (green)
 *   • Running uPnL + close ✕ button (uses cross-process queue endpoint)
 *
 * Designed for the right-side column of Trade Mode (~300 px).
 */
export function RiskMeterPanel({ positions, livePrice }: Props) {
  const qc = useQueryClient();
  const closeM = useMutation({
    mutationFn: dashboardApi.closePosition,
    onSuccess: (res) => {
      pushNotification({
        type: "success",
        title: `Close requested · ${res.symbol}`,
        body: `Queued #${res.id}. Worker processes within ~3s.`,
      });
      setTimeout(() => {
        qc.invalidateQueries({ queryKey: ["positions"] });
        qc.invalidateQueries({ queryKey: ["portfolio"] });
      }, 4_000);
    },
    onError: (err: unknown) => {
      pushNotification({
        type: "error",
        title: "Close failed",
        body: err instanceof Error ? err.message : String(err),
      });
    },
  });

  if (positions.length === 0) {
    return (
      <div className="rounded-xl border border-white/[0.05] bg-[#0A0A0A]/80 backdrop-blur-xl p-3">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
          Risk Meters
        </p>
        <p className="text-[11px] italic font-serif text-muted-foreground/55 text-center py-3">
          — no open positions on this symbol —
        </p>
      </div>
    );
  }

  return (
    <div className="rounded-xl border border-white/[0.05] bg-[#0A0A0A]/80 backdrop-blur-xl p-3 space-y-3">
      <div className="flex items-baseline justify-between">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          Risk Meters
        </p>
        <p className="text-[9px] font-mono text-muted-foreground/45">
          {positions.length} open
        </p>
      </div>

      {positions.map((p) => {
        const px = livePrice > 0 ? livePrice : p.markPrice > 0 ? p.markPrice : p.entryPrice;
        const isLong = p.side === "LONG";

        // Distance to SL/TP (% from entry)
        const slDistPct = p.stopLoss > 0   ? Math.abs((px - p.stopLoss)   / px) * 100 : 0;
        const tpDistPct = p.takeProfit > 0 ? Math.abs((p.takeProfit - px) / px) * 100 : 0;

        // Bar position — 0% = at SL, 100% = at TP, regardless of side
        const slP = p.stopLoss;
        const tpP = p.takeProfit;
        let barPct = 50; // fallback when SL/TP missing
        if (slP > 0 && tpP > 0 && slP !== tpP) {
          // For LONG: SL is below entry, TP is above entry → progress = (px - SL) / (TP - SL)
          // For SHORT: SL is above entry, TP is below entry → progress = (SL - px) / (SL - TP)
          if (isLong) barPct = ((px - slP) / (tpP - slP)) * 100;
          else        barPct = ((slP - px) / (slP - tpP)) * 100;
        }
        barPct = Math.max(0, Math.min(100, barPct));

        // Entry tick position relative to bar
        let entryPct = 50;
        if (slP > 0 && tpP > 0 && slP !== tpP) {
          if (isLong) entryPct = ((p.entryPrice - slP) / (tpP - slP)) * 100;
          else        entryPct = ((slP - p.entryPrice) / (slP - tpP)) * 100;
        }
        entryPct = Math.max(0, Math.min(100, entryPct));

        const pnlTone = p.unrealizedPnL >= 0 ? "text-emerald-300" : "text-rose-300";
        const sideTone = isLong ? "text-emerald-300 border-emerald-400/30 bg-emerald-500/10"
                                : "text-rose-300 border-rose-400/30 bg-rose-500/10";

        return (
          <div key={p.id} className="rounded-lg border border-white/[0.05] bg-white/[0.02] p-2.5">
            {/* Header */}
            <div className="flex items-center justify-between mb-2">
              <div className="flex items-center gap-2">
                <span className={cn("px-1.5 py-0.5 rounded border text-[9px] font-black uppercase tracking-widest", sideTone)}>
                  {isLong ? "↑ LONG" : "↓ SHORT"}
                </span>
                <span className="text-[10px] font-mono text-muted-foreground/65">
                  {p.quantity.toFixed(4)}
                </span>
              </div>
              <button
                onClick={() => {
                  if (!confirm(`Close ${p.symbol} ${p.side}?`)) return;
                  closeM.mutate({
                    symbol: p.symbol,
                    side:   isLong ? "buy" : "sell",
                    reason: "TradeMode.Manual",
                  });
                }}
                disabled={closeM.isPending}
                className="px-1.5 py-0.5 rounded text-[9px] font-mono uppercase tracking-widest border border-rose-400/30 text-rose-300 hover:bg-rose-500/15 disabled:opacity-40"
                title="Close position"
              >
                ✕
              </button>
            </div>

            {/* Risk arc bar */}
            <div className="relative h-2 rounded-full bg-rose-500/10 overflow-hidden mb-1">
              {/* Fill from start to current */}
              <div
                className="absolute inset-y-0 left-0 rounded-full"
                style={{
                  width: `${barPct}%`,
                  background: "linear-gradient(90deg, rgba(240,90,90,0.45) 0%, rgba(245,158,11,0.45) 50%, rgba(34,197,94,0.55) 100%)",
                }}
              />
              {/* Entry tick */}
              <div
                className="absolute top-[-2px] bottom-[-2px] w-px bg-cyan-400/80"
                style={{ left: `${entryPct}%` }}
                title={`Entry ${formatUsd(p.entryPrice)}`}
              />
              {/* Current price tick (white) */}
              <div
                className="absolute top-[-3px] bottom-[-3px] w-[2px] bg-white"
                style={{ left: `${barPct}%` }}
                title={`Mark ${formatUsd(px)}`}
              />
            </div>

            {/* SL / Entry / TP labels */}
            <div className="flex justify-between text-[8px] font-mono text-muted-foreground/45 mb-2">
              <span>SL {p.stopLoss > 0 ? formatUsd(p.stopLoss) : "—"}</span>
              <span className="text-cyan-400/70">{formatUsd(p.entryPrice)}</span>
              <span>TP {p.takeProfit > 0 ? formatUsd(p.takeProfit) : "—"}</span>
            </div>

            {/* Stats row */}
            <div className="grid grid-cols-3 gap-2 text-[10px] font-mono">
              <Stat label="To SL"  value={p.stopLoss > 0   ? `-${slDistPct.toFixed(2)}%` : "—"} tone="text-rose-300" />
              <Stat label="To TP"  value={p.takeProfit > 0 ? `+${tpDistPct.toFixed(2)}%` : "—"} tone="text-emerald-300" />
              <Stat label="uPnL"   value={formatUsd(p.unrealizedPnL)} tone={pnlTone} />
            </div>
          </div>
        );
      })}
    </div>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone: string }) {
  return (
    <div className="text-center">
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/40 mb-0.5">{label}</p>
      <p className={cn("font-bold tabular-nums", tone)}>{value}</p>
    </div>
  );
}
