import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { api } from "@/api/client";
import type { Position } from "@/api/types";
import { CandleChart } from "@/components/charts/CandleChart";
import { RsiPane, MacdPane } from "@/components/charts/IndicatorPanes";
import { cn } from "@/lib/utils";
import { formatDateTime, formatPercentLoose, formatUsd, pnlColor } from "@/lib/format";

interface CandleDto {
  time: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

interface Props {
  position: Position;
  onClose: () => void;
}

export function PositionDetailModal({ position, onClose }: Props) {
  const detailQ = useQuery({
    queryKey: ["position-detail", position.id],
    queryFn: () => dashboardApi.positionDetail(position.id),
    staleTime: 10_000,
  });

  const candlesQ = useQuery({
    queryKey: ["position-candles", position.id],
    queryFn: () => api.get<CandleDto[]>(`/api/positions/${position.id}/candles`),
    staleTime: 60_000,
  });

  const d = detailQ.data;

  return (
    <div
      className="fixed inset-0 z-50 flex items-end justify-center sm:items-center"
      onClick={onClose}
    >
      {/* Backdrop */}
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" />

      {/* Panel */}
      <div
        className="relative z-10 w-full max-w-4xl max-h-[90vh] overflow-y-auto rounded-t-2xl sm:rounded-2xl border border-white/10 bg-[#0A0E14] shadow-2xl"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/5 sticky top-0 bg-[#0A0E14]/95 backdrop-blur-sm z-10">
          <div>
            <div className="flex items-center gap-3">
              <span className="text-lg font-black tracking-tight">{position.symbol}</span>
              <span
                className={cn(
                  "px-2 py-0.5 rounded text-[10px] font-black uppercase tracking-wider",
                  position.side === "LONG"
                    ? "bg-emerald-400/10 text-emerald-400"
                    : "bg-rose-400/10 text-rose-400"
                )}
              >
                {position.side}
              </span>
              <span className="text-[10px] uppercase tracking-widest text-muted-foreground/50">
                {position.strategyTag}
              </span>
            </div>
            <p className="text-xs text-muted-foreground/50 mt-0.5">
              {position.lifecycleStatus} · {position.exitPolicyTag} · Entered{" "}
              {formatDateTime(position.entryTime)}
            </p>
          </div>
          <button
            onClick={onClose}
            className="text-muted-foreground/40 hover:text-foreground transition-colors text-xl leading-none px-2"
          >
            ✕
          </button>
        </div>

        <div className="px-6 py-5 space-y-6">
          {/* Key metrics row */}
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <MetricCard label="Entry" value={formatUsd(position.entryPrice)} />
            <MetricCard label="Mark" value={formatUsd(position.markPrice)} />
            <MetricCard
              label="Unrealized PnL"
              value={formatUsd(position.unrealizedPnL)}
              valueClass={pnlColor(position.unrealizedPnL)}
            />
            <MetricCard
              label="ROE"
              value={formatPercentLoose(position.roe)}
              valueClass={pnlColor(position.roe)}
            />
            <MetricCard
              label="Stop Loss"
              value={position.stopLoss > 0 ? formatUsd(position.stopLoss) : "—"}
              valueClass="text-rose-400"
            />
            <MetricCard
              label="Take Profit"
              value={position.takeProfit > 0 ? formatUsd(position.takeProfit) : "—"}
              valueClass="text-emerald-400"
            />
            <MetricCard label="Leverage" value={`${position.leverage}×`} />
            <MetricCard label="Qty" value={position.remainingQuantity.toFixed(4)} />
          </div>

          {/* Candle chart */}
          <div className="rounded-xl border border-white/5 bg-[#070A0F]/60 p-4">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-1">
              Price Chart (1h · 120 bars)
            </p>
            <p className="text-sm font-bold mb-4">{position.symbol} — Entry / SL / TP overlays</p>
            {candlesQ.isLoading ? (
              <div className="h-[300px] flex items-center justify-center text-xs text-muted-foreground/50">
                Loading candle data…
              </div>
            ) : (
              <div className="overflow-x-auto">
                <CandleChart
                  candles={candlesQ.data ?? []}
                  entryTime={position.entryTime}
                  overlays={[
                    {
                      price: position.entryPrice,
                      color: "rgba(250,204,21,0.9)",
                      label: "ENTRY",
                    },
                    ...(position.stopLoss > 0
                      ? [
                          {
                            price: position.stopLoss,
                            color: "rgba(248,113,113,0.9)",
                            label: "SL",
                            dash: true,
                          },
                        ]
                      : []),
                    ...(position.takeProfit > 0
                      ? [
                          {
                            price: position.takeProfit,
                            color: "rgba(52,211,153,0.9)",
                            label: "TP",
                            dash: true,
                          },
                        ]
                      : []),
                  ]}
                  width={680}
                  height={300}
                />
                {/* Multi-indicator panes — same closes feed RSI(14) + MACD(12,26,9) */}
                {(candlesQ.data?.length ?? 0) > 30 && (
                  <div className="mt-2 space-y-1 border-t border-white/5 pt-2">
                    <RsiPane
                      closes={(candlesQ.data ?? []).map((c) => c.close)}
                      width={680}
                      height={88}
                    />
                    <MacdPane
                      closes={(candlesQ.data ?? []).map((c) => c.close)}
                      width={680}
                      height={100}
                    />
                  </div>
                )}
              </div>
            )}
          </div>

          {/* Audit section — only if detail loaded */}
          {d && (
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              {/* Excursion metrics */}
              <div className="rounded-xl border border-white/5 bg-[#070A0F]/60 p-4">
                <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-3">
                  Excursion Analysis
                </p>
                <div className="space-y-2">
                  <AuditRow
                    label="MFE"
                    value={formatPercentLoose(d.maxFavorableExcursionPercent)}
                    valueClass="text-emerald-400"
                  />
                  <AuditRow
                    label="MAE"
                    value={formatPercentLoose(d.maxAdverseExcursionPercent)}
                    valueClass="text-rose-400"
                  />
                  <AuditRow
                    label="Signal→Fill drift"
                    value={`${d.signalToFillDriftBps.toFixed(1)} bp`}
                  />
                  <AuditRow label="Fees paid" value={formatUsd(d.totalFeesPaid)} valueClass="text-amber-400" />
                  <AuditRow
                    label="Net funding"
                    value={formatUsd(d.netFunding)}
                    valueClass={pnlColor(d.netFunding)}
                  />
                </div>
              </div>

              {/* Metadata */}
              <div className="rounded-xl border border-white/5 bg-[#070A0F]/60 p-4">
                <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-3">
                  Trade Metadata
                </p>
                <div className="space-y-2">
                  <AuditRow label="ML Model" value={d.mlModelVersion || "—"} />
                  <AuditRow label="Promotion Stage" value={d.promotionStage || "—"} />
                  <AuditRow label="Execution Mode" value={d.trade?.executionMode || "—"} />
                  <AuditRow label="Exit Reason" value={d.trade?.exitReason || "Open"} />
                </div>
              </div>

              {/* Reasoning (full width) */}
              {d.reasoning && (
                <div className="sm:col-span-2 rounded-xl border border-white/5 bg-[#070A0F]/60 p-4">
                  <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
                    Signal Reasoning
                  </p>
                  <p className="text-xs text-muted-foreground/80 leading-relaxed whitespace-pre-wrap">
                    {d.reasoning}
                  </p>
                </div>
              )}
            </div>
          )}

          {detailQ.isLoading && (
            <p className="text-xs text-muted-foreground/50 text-center py-4">Loading audit data…</p>
          )}
        </div>
      </div>
    </div>
  );
}

function MetricCard({
  label,
  value,
  valueClass = "",
}: {
  label: string;
  value: string;
  valueClass?: string;
}) {
  return (
    <div className="rounded-lg border border-white/5 bg-white/[0.02] px-3 py-2.5">
      <p className="text-[9px] font-black uppercase tracking-[0.2em] text-muted-foreground/40 mb-1">
        {label}
      </p>
      <p className={cn("font-mono text-sm font-bold leading-none", valueClass)}>{value}</p>
    </div>
  );
}

function AuditRow({
  label,
  value,
  valueClass = "",
}: {
  label: string;
  value: string;
  valueClass?: string;
}) {
  return (
    <div className="flex items-center justify-between text-xs">
      <span className="text-muted-foreground/50 font-medium">{label}</span>
      <span className={cn("font-mono font-bold", valueClass || "text-foreground/80")}>{value}</span>
    </div>
  );
}
