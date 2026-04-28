import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import type { PortfolioSummary } from "@/api/types";
import { StatCard } from "@/components/dashboard/StatCard";
import { PositionsTable } from "@/components/dashboard/PositionsTable";
import { LastSignalCard } from "@/components/dashboard/LastSignalCard";
import { EquityTimelineChart } from "@/components/dashboard/EquityTimelineChart";

import { LiveRiskBanner } from "@/components/dashboard/risk/LiveRiskBanner";
import { DrawdownWaterfall } from "@/components/dashboard/risk/DrawdownWaterfall";
import { RiskLimitUtilization } from "@/components/dashboard/risk/RiskLimitUtilization";
import { StressTestGauges } from "@/components/dashboard/risk/StressTestGauges";

import { DailyBriefCard } from "@/components/dashboard/intel/DailyBriefCard";
import { TopOpportunitiesCard } from "@/components/dashboard/intel/TopOpportunitiesCard";
import { ActionRecommendations } from "@/components/dashboard/intel/ActionRecommendations";
import { WeeklyDigestCard } from "@/components/dashboard/WeeklyDigestCard";
import { HeadlineRings } from "@/components/dashboard/HeadlineRings";
import { ActivePositionsCard } from "@/components/dashboard/ActivePositionsCard";
import { OrderHistoryCard } from "@/components/dashboard/OrderHistoryCard";
import { PaperPortfolioCard } from "@/components/dashboard/PaperPortfolioCard";

import { MultiPeriodStrip } from "@/components/dashboard/perf/MultiPeriodStrip";
import { StrategyRotationMatrix } from "@/components/dashboard/perf/StrategyRotationMatrix";
import { EdgeDecayTracker } from "@/components/dashboard/perf/EdgeDecayTracker";
import { TradeQualityCard } from "@/components/dashboard/perf/TradeQualityCard";

import { ServiceHealthTile } from "@/components/dashboard/ops/ServiceHealthTile";
import { PreFlightChecksCard } from "@/components/dashboard/ops/PreFlightChecksCard";
import { LatencyDashboard } from "@/components/dashboard/ops/LatencyDashboard";
import { QueueDepthCard } from "@/components/dashboard/ops/QueueDepthCard";

import { DashboardTabs, type DashboardTab } from "@/components/dashboard/layout/DashboardTabs";
import { TVMode } from "@/components/dashboard/layout/TVMode";
import { LiveTapeMarquee } from "@/components/dashboard/layout/LiveTapeMarquee";
import { LayoutPresetMenu, PRESETS, type LayoutPreset } from "@/components/dashboard/layout/LayoutPresetMenu";

import { CustomizeBar } from "@/components/ui/CustomizeBar";
import { usePageCustomization } from "@/hooks/usePageCustomization";
import type { WidgetSpec } from "@/lib/pageCustomization";

import { cn } from "@/lib/utils";
import { formatPercent, formatUsd, pnlColor } from "@/lib/format";

const TRADING_WIDGETS: WidgetSpec[] = [
  { id: "daily-brief", label: "Daily Brief", description: "AI-style 3-paragraph operator brief (overnight / outlook / recommendation)", category: "Intelligence", defaultVisible: true },
  { id: "headline-rings", label: "Headline Rings", description: "PnL % · Profit $ · Win Rate · Profit Factor — Notion-style donut KPIs", category: "Numbers", defaultVisible: true },
  { id: "stat-cards", label: "Headline Stats", description: "Equity / Realised / Unrealised / WR / PF / Trade count", category: "Numbers", defaultVisible: true, required: true },
  { id: "weekly-digest", label: "Weekly Digest", description: "7-day P&L · daily bars · best/worst trade · edge drift", category: "Intelligence", defaultVisible: true },
  { id: "equity-chart", label: "Equity Timeline", description: "Cumulative realised P&L curve across closed trades", category: "Numbers", defaultVisible: true },
  { id: "top-opportunities", label: "Top Opportunities", description: "Funding-alpha scanner picks · click to deep-link Markets", category: "Intelligence", defaultVisible: true },
  { id: "recent-signals", label: "Recent Signals", description: "Last 25 signal evaluations · approval / rejection trace", category: "Activity", defaultVisible: true },
  { id: "positions-table", label: "Open Positions", description: "Currently open trades with mark price + P&L", category: "Activity", defaultVisible: true, required: true },
];

const STORAGE_KEY = "tmq.dashboard.state";

interface PersistedState {
  preset: string;
  tab: DashboardTab;
  showMarquee: boolean;
  compactMode: boolean;
}

function readPersisted(): PersistedState {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return { preset: "trader", tab: "trading", showMarquee: false, compactMode: false };
    const parsed = JSON.parse(raw) as Partial<PersistedState>;
    return {
      preset: parsed.preset || "trader",
      tab: (parsed.tab as DashboardTab) || "trading",
      showMarquee: parsed.showMarquee ?? false,
      compactMode: parsed.compactMode ?? false,
    };
  } catch {
    return { preset: "trader", tab: "trading", showMarquee: false, compactMode: false };
  }
}

export function DashboardPage() {
  const initial = readPersisted();
  const [tab, setTab] = useState<DashboardTab>(initial.tab);
  const [presetId, setPresetId] = useState(initial.preset);
  const [tvMode, setTvMode] = useState(false);
  const [showMarquee, setShowMarquee] = useState(initial.showMarquee);
  const [compactMode, setCompactMode] = useState(initial.compactMode);

  useEffect(() => {
    try {
      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify({ preset: presetId, tab, showMarquee, compactMode } as PersistedState)
      );
    } catch {
      /* ignore */
    }
  }, [presetId, tab, showMarquee, compactMode]);

  const onApplyPreset = (preset: LayoutPreset) => {
    setPresetId(preset.id);
    setTab(preset.defaultTab);
  };

  const portfolioQ = useQuery({
    queryKey: ["portfolio"],
    queryFn: () => dashboardApi.portfolio(),
    refetchInterval: 5_000,
  });
  const p = portfolioQ.data;

  return (
    <TVMode enabled={tvMode} onExit={() => setTvMode(false)} onTabChange={setTab}>
      <div className="space-y-5">
        {/* Title strip + controls */}
        <div className="flex items-baseline justify-between gap-3 pb-1 flex-wrap">
          <div>
            <h2 className="font-serif text-[22px] tracking-tight">Dashboard</h2>
            <p className="text-[11px] uppercase tracking-[0.25em] text-accent/70 font-medium mt-0.5">
              Command Center · {PRESETS.find((p) => p.id === presetId)?.name ?? "Trader"} layout
            </p>
          </div>
          <div className="flex items-center gap-2 flex-wrap">
            <button
              onClick={() => setCompactMode((v) => !v)}
              title={compactMode ? "Show all detail" : "Hide ancillary cards"}
              className={cn(
                "px-2.5 py-1 rounded-md text-[10px] font-mono border transition-colors",
                compactMode
                  ? "bg-accent/15 text-accent border-accent/30"
                  : "border-white/10 text-muted-foreground/70 hover:bg-white/[0.05] hover:text-foreground"
              )}
            >
              {compactMode ? "Compact" : "Detailed"}
            </button>
            <button
              onClick={() => setShowMarquee((v) => !v)}
              title={showMarquee ? "Hide live tape marquee" : "Show live tape marquee"}
              className={cn(
                "px-2.5 py-1 rounded-md text-[10px] font-mono border transition-colors",
                showMarquee
                  ? "bg-accent/15 text-accent border-accent/30"
                  : "border-white/10 text-muted-foreground/70 hover:bg-white/[0.05] hover:text-foreground"
              )}
            >
              {showMarquee ? "Tape ●" : "Tape ○"}
            </button>
            <LayoutPresetMenu current={presetId} onApply={onApplyPreset} />
            <button
              onClick={() => setTvMode(true)}
              className="flex items-center gap-2 px-2.5 py-1 rounded-md text-[10px] font-mono text-muted-foreground/70 border border-white/10 bg-white/[0.02] hover:bg-white/[0.05] hover:text-foreground transition-colors"
              title="Auto-rotating fullscreen mode (Esc to exit)"
            >
              <svg width="11" height="11" viewBox="0 0 16 16" fill="none">
                <rect
                  x="2"
                  y="3"
                  width="12"
                  height="9"
                  rx="1"
                  stroke="currentColor"
                  strokeWidth="1.4"
                />
                <path d="M6 14h4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
              </svg>
              TV mode
            </button>
          </div>
        </div>

        {/* Live Risk Banner — always at the top */}
        <LiveRiskBanner />

        {/* Tabs */}
        <DashboardTabs active={tab} onChange={setTab} />

        {tab === "trading" && <TradingTab portfolio={p} compact={compactMode} />}
        {tab === "risk" && <RiskTab />}
        {tab === "performance" && <PerformanceTab />}
        {tab === "ops" && <OpsTab />}

        {showMarquee && <LiveTapeMarquee />}
      </div>
    </TVMode>
  );
}

/* ===================== TRADING TAB ===================== */
function TradingTab({
  portfolio,
  compact,
}: {
  portfolio: PortfolioSummary | undefined;
  compact: boolean;
}) {
  const positionsQ = useQuery({
    queryKey: ["positions"],
    queryFn: () => dashboardApi.positions(),
    refetchInterval: 3_000,
  });
  const p = portfolio;
  const cust = usePageCustomization("dashboard.trading", TRADING_WIDGETS);

  return (
    <div className="space-y-6">
      {/* AI commander — compact mode forces hide; otherwise customizable */}
      {!compact && cust.isVisible("daily-brief") && <DailyBriefCard />}

      {/* Headline rings — Notion-style donut KPIs (single-glance system status) */}
      {cust.isVisible("headline-rings") && <HeadlineRings />}

      {/* Paper portfolio snapshot — equity / P&L / win-rate from Worker */}
      <PaperPortfolioCard />

      {/* Active positions (bridges state.json + OKX demo + DB) */}
      <ActivePositionsCard />

      {/* Order / position event history from audit log */}
      <OrderHistoryCard />

      {/* Headline stats — required */}
      {cust.isVisible("stat-cards") && (
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-3">
        <StatCard
          label="Total Equity"
          value={p ? formatUsd(p.totalEquity) : "—"}
          accent={p && p.totalEquity > 0 ? "positive" : p && p.totalEquity < 0 ? "negative" : "neutral"}
        />
        <StatCard
          label="Realized PnL"
          value={p ? formatUsd(p.realizedPnL) : "—"}
          accent={p ? (pnlColor(p.realizedPnL).includes("emerald") ? "positive" : pnlColor(p.realizedPnL).includes("rose") ? "negative" : "neutral") : "neutral"}
        />
        <StatCard
          label="Unrealized PnL"
          value={p ? formatUsd(p.unrealizedPnL) : "—"}
          accent={p ? (pnlColor(p.unrealizedPnL).includes("emerald") ? "positive" : pnlColor(p.unrealizedPnL).includes("rose") ? "negative" : "neutral") : "neutral"}
        />
        <StatCard
          label="Win Rate"
          value={p ? formatPercent(p.winRate) : "—"}
          subValue={p ? `${p.winningTradeCount}W / ${p.losingTradeCount}L` : undefined}
          accent="info"
        />
        <StatCard
          label="Profit Factor"
          value={p ? p.profitFactor.toFixed(2) : "—"}
          subValue={p ? `Avg R:R ${p.averageRiskReward.toFixed(2)}` : undefined}
          accent="info"
        />
        <StatCard
          label="Trades / Open"
          value={p ? `${p.closedTradeCount} / ${p.openPositionCount}` : "—"}
          subValue={p ? `Fees ${formatUsd(p.totalFeesPaid)}` : undefined}
        />
      </div>
      )}

      {/* Weekly digest — single screenshot-worthy summary */}
      {cust.isVisible("weekly-digest") && <WeeklyDigestCard />}

      {/* Equity curve + top opportunities — both togglable */}
      {(cust.isVisible("equity-chart") || cust.isVisible("top-opportunities")) && (
        <div
          className={cn(
            "grid gap-4",
            cust.isVisible("equity-chart") && cust.isVisible("top-opportunities")
              ? "grid-cols-1 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]"
              : "grid-cols-1"
          )}
        >
          {cust.isVisible("equity-chart") && <EquityTimelineChart />}
          {cust.isVisible("top-opportunities") && <TopOpportunitiesCard />}
        </div>
      )}

      {/* Recent signals — hidden in compact mode and respects customization */}
      {!compact && cust.isVisible("recent-signals") && <LastSignalCard />}

      {cust.isVisible("positions-table") && (
        <PositionsTable positions={positionsQ.data ?? []} isLoading={positionsQ.isLoading} />
      )}
      {positionsQ.isError && (
        <p className="text-xs text-rose-400">
          Failed to load positions. Check that Listener.Api is running on the configured URL.
        </p>
      )}

      {/* Customize bar — operator can toggle individual panels */}
      <CustomizeBar customization={cust} pageLabel="Dashboard · Trading" />
    </div>
  );
}

/* ===================== RISK TAB ===================== */
function RiskTab() {
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-1 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)] gap-4">
        <DrawdownWaterfall />
        <RiskLimitUtilization />
      </div>
      <StressTestGauges />
      {/* Recommendations live here — naturally risk-driven */}
      <ActionRecommendations />
    </div>
  );
}

/* ===================== PERFORMANCE TAB ===================== */
function PerformanceTab() {
  return (
    <div className="space-y-4">
      <MultiPeriodStrip />
      <div className="grid grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-4">
        <StrategyRotationMatrix />
        <EdgeDecayTracker />
      </div>
      {/* Trade Quality stands alone (Equity chart lives in Trading tab) */}
      <TradeQualityCard />
    </div>
  );
}

/* ===================== OPS TAB ===================== */
function OpsTab() {
  return (
    <div className="space-y-4">
      <PreFlightChecksCard />
      <div className="grid grid-cols-1 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-4">
        <ServiceHealthTile />
        <QueueDepthCard />
      </div>
      <LatencyDashboard />
    </div>
  );
}
