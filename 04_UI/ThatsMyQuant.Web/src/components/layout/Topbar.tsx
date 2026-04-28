import { useQuery } from "@tanstack/react-query";
import { ExchangeHealthBadge } from "./ExchangeHealthBadge";
import { ExchangeModeSwitcher } from "./ExchangeModeSwitcher";
import { NotificationsBell } from "./NotificationsBell";
import { ThemeToggle } from "./ThemeToggle";
import { UserMenu } from "./UserMenu";
import { QuickActions } from "./QuickActions";
import { ConnectionStatusPill } from "@/components/ui/ConnectionStatusPill";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { pnlColor } from "@/lib/format";
import { useSidebar } from "@/hooks/useSidebar";
import { useDensity } from "@/hooks/useDensity";

function LiveEquity() {
  const q = useQuery({
    queryKey: ["portfolio"],
    queryFn: () => dashboardApi.portfolio(),
    refetchInterval: 5_000,
    staleTime: 4_000,
  });

  if (!q.data) return null;
  const { totalEquity, unrealizedPnL, openPositionCount } = q.data;

  const equityStr =
    Math.abs(totalEquity) >= 1000
      ? `$${(totalEquity / 1000).toFixed(1)}k`
      : `$${totalEquity.toFixed(0)}`;

  return (
    <div className="hidden md:flex items-center gap-4 border-r border-white/5 pr-4">
      <div className="flex flex-col leading-none items-end">
        <span className="text-[8px] font-black uppercase tracking-[0.3em] text-muted-foreground/40 mb-0.5">
          Equity
        </span>
        <span className="text-sm font-black font-mono tracking-tight">{equityStr}</span>
      </div>
      <div className="flex flex-col leading-none items-end">
        <span className="text-[8px] font-black uppercase tracking-[0.3em] text-muted-foreground/40 mb-0.5">
          Open PnL
        </span>
        <span
          className={cn(
            "text-sm font-black font-mono tracking-tight",
            pnlColor(unrealizedPnL)
          )}
        >
          {unrealizedPnL >= 0 ? "+" : ""}
          {unrealizedPnL.toFixed(0)}
        </span>
      </div>
      {openPositionCount > 0 && (
        <div className="flex flex-col leading-none items-end">
          <span className="text-[8px] font-black uppercase tracking-[0.3em] text-muted-foreground/40 mb-0.5">
            Pos
          </span>
          <span className="text-sm font-black font-mono tracking-tight text-accent">
            {openPositionCount}
          </span>
        </div>
      )}
    </div>
  );
}

function DensityToggle() {
  const [density, setDensity] = useDensity();
  const next = density === "comfortable" ? "compact" : "comfortable";
  return (
    <button
      onClick={() => setDensity(next)}
      title={`Switch to ${next}`}
      className="hidden sm:flex items-center justify-center w-8 h-8 rounded-md text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.05] transition-colors"
    >
      {density === "comfortable" ? (
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
          <rect x="2" y="3" width="12" height="2" fill="currentColor" rx="0.5" />
          <rect x="2" y="7" width="12" height="2" fill="currentColor" rx="0.5" />
          <rect x="2" y="11" width="12" height="2" fill="currentColor" rx="0.5" />
        </svg>
      ) : (
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
          <rect x="2" y="2" width="12" height="1.5" fill="currentColor" rx="0.5" />
          <rect x="2" y="5" width="12" height="1.5" fill="currentColor" rx="0.5" />
          <rect x="2" y="8" width="12" height="1.5" fill="currentColor" rx="0.5" />
          <rect x="2" y="11" width="12" height="1.5" fill="currentColor" rx="0.5" />
        </svg>
      )}
    </button>
  );
}

function HamburgerButton() {
  const { toggle } = useSidebar();
  return (
    <button
      onClick={toggle}
      className="lg:hidden flex items-center justify-center w-9 h-9 -ml-2 rounded-md text-muted-foreground hover:text-foreground hover:bg-white/[0.05]"
      aria-label="Toggle sidebar"
    >
      <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
        <path d="M2 4h14M2 9h14M2 14h14" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
      </svg>
    </button>
  );
}

export function Topbar() {
  return (
    <header className="h-16 px-4 sm:px-8 border-b border-white/5 bg-[#070A0F]/60 backdrop-blur-xl flex items-center justify-between sticky top-0 z-20">
      <div className="flex items-center gap-3 min-w-0">
        <HamburgerButton />
        <div className="flex flex-col leading-none min-w-0">
          <span className="text-[9px] font-black uppercase tracking-[0.4em] text-accent/70">
            Live Console
          </span>
          <span className="text-sm font-bold tracking-tight mt-1 truncate">
            Trading Operations{" "}
            <span className="text-muted-foreground/40 font-light hidden sm:inline">— Realtime</span>
          </span>
        </div>
      </div>
      <div className="flex items-center gap-2 sm:gap-3">
        <LiveEquity />
        <ConnectionStatusPill />
        <ExchangeHealthBadge />
        <ExchangeModeSwitcher />
        <QuickActions />
        <DensityToggle />
        <ThemeToggle />
        <NotificationsBell />
        <UserMenu />
      </div>
    </header>
  );
}
