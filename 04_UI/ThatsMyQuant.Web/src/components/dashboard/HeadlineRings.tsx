import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { RingStat, RingStatRow, type RingTone } from "@/components/ui/RingStat";
import { ZoomableTile } from "@/components/ui/ZoomableTile";
import { useVenue } from "@/hooks/useVenue";
import { formatUsd } from "@/lib/format";

/**
 * Notion-template-style headline KPI rings — 4 donuts giving the operator
 * the single-glance answer to "is the system OK right now?".
 *
 *   PnL %    Profit $    Win Rate    Profit Factor
 *
 * Source: PortfolioSummaryDto + closed-trade aggregates already on the
 * dashboard query graph (no new endpoints).
 *
 * Filling logic:
 *   - PnL %        : maps |pct| / 5 (so 5% = full ring); green/rose by sign.
 *   - Profit $     : maps progress to InitialBalance×0.05 ceiling; pink branding.
 *   - Win Rate     : 0–100 direct fill; green ≥55, amber 45–55, rose <45.
 *   - Profit Factor: maps PF / 3 (so PF≥3 = full ring); cyan branding.
 */
export function HeadlineRings() {
  const { executionModeParam } = useVenue();
  const portfolioQ = useQuery({
    queryKey: ["portfolio", executionModeParam ?? "all"],
    queryFn: () => dashboardApi.portfolio(executionModeParam),
    refetchInterval: 5_000,
  });
  // Paper-state fallback: hybrid executors don't write to the SQLite Trade table,
  // so /api/portfolio reports zeros even while the worker is actively trading. We
  // pull the worker's authoritative paper_portfolio.json snapshot in parallel and
  // promote it whenever the DB-backed source is empty.
  const paperQ = useQuery({
    queryKey: ["paper-portfolio"],
    queryFn:  dashboardApi.paperPortfolio,
    refetchInterval: 5_000,
  });

  const p = portfolioQ.data;
  const paper = paperQ.data;
  const dbHasData = (p?.closedTradeCount ?? 0) > 0 || (p?.totalEquity ?? 0) > 0;

  const equity   = dbHasData ? (p?.totalEquity ?? 0)       : (paper?.totalEquity ?? 0);
  const realised = dbHasData ? (p?.realizedPnL ?? 0)       : (paper?.totalPnL    ?? 0);
  const wr       = dbHasData ? (p?.winRate ?? 0)           : ((paper?.winRate ?? 0) / 100); // paper returns 0-100
  const closed   = dbHasData ? (p?.closedTradeCount ?? 0)  : (paper?.closedCount ?? 0);
  const wins     = dbHasData ? (p?.winningTradeCount ?? 0) : (paper?.winningTrades ?? 0);
  const losses   = closed - wins;
  // Profit factor — DB exposes it directly; paper snapshot doesn't, so derive from wins/losses
  // when in paper mode (gross W / gross L approximation by win-rate ratio when all trades are
  // similar size — good enough as a headline indicator until the snapshot exposes per-trade PnL).
  const pf       = dbHasData ? (p?.profitFactor ?? 0)
                  : (losses > 0 && wins > 0 ? wins / losses : (wins > 0 ? 99 : 0));

  // Total PnL % — anchor on equity so realised+unrealised both feed in.
  // (equity - initial) / initial × 100 — but PortfolioSummary doesn't expose
  // initial; closest proxy: realised+unrealised divided by equity.
  const totalPnL = realised + (p?.unrealizedPnL ?? 0);
  const pnlPct = equity > 0 ? (totalPnL / equity) * 100 : 0;
  const pnlTone: RingTone = pnlPct >= 0 ? "green" : "rose";
  const pnlFill = Math.min(100, (Math.abs(pnlPct) / 5) * 100);

  // Profit $ — fill scaled against $200 default ceiling (will tune later)
  const profitFill = Math.min(100, (Math.abs(realised) / 200) * 100);
  const profitTone: RingTone = realised >= 0 ? "pink" : "rose";

  // Win rate — direct
  const wrPct = wr * 100;
  const wrTone: RingTone = wrPct >= 55 ? "green" : wrPct >= 45 ? "amber" : "rose";

  // Profit Factor — fill against PF=3 ceiling
  const pfFill = Math.min(100, (pf / 3) * 100);
  const pfTone: RingTone =
    pf >= 1.5 ? "cyan" :
    pf >= 1.0 ? "amber" :
                "rose";

  const noTrades = closed === 0;

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0A0A0A]/80 backdrop-blur-xl p-5">
      <RingStatRow>
        <ZoomableTile
          title="Total P&L"
          subtitle={`Equity ${formatUsd(equity)} · ${closed} closed trades`}
          preview={
            <RingStat
              value={pnlFill}
              display={`${pnlPct >= 0 ? "+" : ""}${pnlPct.toFixed(1)}%`}
              label="PnL"
              sub={equity > 0 ? formatUsd(equity) : undefined}
              tone={pnlTone}
              size="lg"
              empty={equity === 0}
            />
          }
          expanded={() => (
            <DetailLayout
              ring={
                <RingStat
                  value={pnlFill}
                  display={`${pnlPct >= 0 ? "+" : ""}${pnlPct.toFixed(1)}%`}
                  label="PnL"
                  tone={pnlTone}
                  size="lg"
                  empty={equity === 0}
                />
              }
              rows={[
                ["Total P&L", `${pnlPct >= 0 ? "+" : ""}${pnlPct.toFixed(2)}%`],
                ["Realised", formatUsd(realised)],
                ["Unrealised", formatUsd(p?.unrealizedPnL ?? 0)],
                ["Total equity", formatUsd(equity)],
                ["Closed trades", String(closed)],
                ["Open positions", String(p?.openPositionCount ?? 0)],
              ]}
              note="Ring fills against a 5% return ceiling. Sign drives green/rose tone."
            />
          )}
        />
        <ZoomableTile
          title="Realised Profit"
          subtitle={`${closed} closed trades`}
          preview={
            <RingStat
              value={profitFill}
              display={formatUsd(realised)}
              label="Profit"
              sub={`${closed} trades`}
              tone={profitTone}
              size="lg"
              empty={noTrades}
            />
          }
          expanded={() => (
            <DetailLayout
              ring={
                <RingStat
                  value={profitFill}
                  display={formatUsd(realised)}
                  label="Profit"
                  tone={profitTone}
                  size="lg"
                  empty={noTrades}
                />
              }
              rows={[
                ["Realised P&L", formatUsd(realised)],
                ["Net funding", formatUsd(p?.netFunding ?? 0)],
                ["Total fees paid", formatUsd(p?.totalFeesPaid ?? 0)],
                ["Avg trade P&L", formatUsd(p?.averageTradePnL ?? 0)],
                ["Closed trades", String(closed)],
              ]}
              note="Ring fills against $200 ceiling — first six figures of profit fill the donut. Beyond that the value keeps growing in the centre but the ring caps."
            />
          )}
        />
        <ZoomableTile
          title="Win Rate"
          subtitle={`${p?.winningTradeCount ?? 0} W · ${p?.losingTradeCount ?? 0} L`}
          preview={
            <RingStat
              value={wrPct}
              display={noTrades ? "—" : `${wrPct.toFixed(0)}%`}
              label="Win Rate"
              sub={p ? `${p.winningTradeCount}/${closed}` : undefined}
              tone={wrTone}
              size="lg"
              empty={noTrades}
            />
          }
          expanded={() => (
            <DetailLayout
              ring={
                <RingStat
                  value={wrPct}
                  display={noTrades ? "—" : `${wrPct.toFixed(1)}%`}
                  label="Win Rate"
                  tone={wrTone}
                  size="lg"
                  empty={noTrades}
                />
              }
              rows={[
                ["Win rate", `${wrPct.toFixed(2)}%`],
                ["Wins", String(p?.winningTradeCount ?? 0)],
                ["Losses", String(p?.losingTradeCount ?? 0)],
                ["Avg risk:reward", (p?.averageRiskReward ?? 0).toFixed(2)],
                ["Total closed", String(closed)],
              ]}
              note="Green ≥55% · Amber 45–55% · Rose <45%. Below 45% with low PF is the classic edge-decay signature."
            />
          )}
        />
        <ZoomableTile
          title="Profit Factor"
          subtitle="Gross Wins / Gross Losses"
          preview={
            <RingStat
              value={pfFill}
              display={noTrades ? "—" : pf >= 99 ? "∞" : pf.toFixed(2)}
              label="Profit Factor"
              sub="gross W / gross L"
              tone={pfTone}
              size="lg"
              empty={noTrades}
            />
          }
          expanded={() => (
            <DetailLayout
              ring={
                <RingStat
                  value={pfFill}
                  display={noTrades ? "—" : pf >= 99 ? "∞" : pf.toFixed(2)}
                  label="Profit Factor"
                  tone={pfTone}
                  size="lg"
                  empty={noTrades}
                />
              }
              rows={[
                ["Profit factor", pf >= 99 ? "∞" : pf.toFixed(3)],
                ["Win rate", `${wrPct.toFixed(2)}%`],
                ["Avg risk:reward", (p?.averageRiskReward ?? 0).toFixed(2)],
                ["Avg trade P&L", formatUsd(p?.averageTradePnL ?? 0)],
                ["Closed trades", String(closed)],
              ]}
              note="PF ≥ 1.5 is healthy alpha · 1.0–1.5 marginal · <1.0 losing. Ring fills against PF=3 ceiling."
            />
          )}
        />
      </RingStatRow>
    </div>
  );
}

function DetailLayout({
  ring, rows, note,
}: {
  ring: React.ReactNode;
  rows: [string, string][];
  note: string;
}) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-[auto_1fr] gap-6 items-start">
      <div className="flex justify-center md:justify-start">{ring}</div>
      <div className="space-y-2">
        <div className="divide-y divide-white/[0.04]">
          {rows.map(([k, v]) => (
            <div key={k} className="flex items-baseline justify-between py-2 text-[12px]">
              <span className="text-muted-foreground/65">{k}</span>
              <span className="font-mono font-bold tabular-nums text-foreground/90">{v}</span>
            </div>
          ))}
        </div>
        <p className="text-[10px] font-serif italic text-muted-foreground/55 leading-relaxed pt-2">
          {note}
        </p>
      </div>
    </div>
  );
}
