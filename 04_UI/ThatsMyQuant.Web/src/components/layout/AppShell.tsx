import { useState, useEffect } from "react";
import { Outlet, useLocation } from "react-router-dom";
import { Sidebar } from "./Sidebar";
import { Topbar } from "./Topbar";
import { useRealtimeStream } from "@/hooks/useRealtimeStream";
import { useDrawdownTierAlerts } from "@/hooks/useDrawdownTierAlerts";
import { useDensity } from "@/hooks/useDensity";
import { VenueContext, STORAGE_KEY as VENUE_STORAGE_KEY, type VenueView } from "@/hooks/useVenue";
import { SidebarContext } from "@/hooks/useSidebar";
import { useGlobalHotkeys } from "@/hooks/useGlobalHotkeys";
import { KeyboardShortcutsOverlay } from "@/components/ui/KeyboardShortcutsOverlay";
import { OfflineBanner } from "./OfflineBanner";
import { OnboardingTour } from "./OnboardingTour";
import { SessionLockOverlay } from "@/components/auth/SessionLockOverlay";
import { FirstTradeCelebration } from "@/components/dashboard/FirstTradeCelebration";

export function AppShell() {
  useRealtimeStream();
  useDrawdownTierAlerts(); // tiered DD alerts at 1%, 2.5%, 4% (CB fires at 5%)
  useDensity(); // applies data-density attribute on <html>
  const hotkeys = useGlobalHotkeys();

  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();

  // Venue view filter (paper / okx-demo / live / all). Persists to localStorage.
  const [venue, setVenueState] = useState<VenueView>(() => {
    try {
      const v = localStorage.getItem(VENUE_STORAGE_KEY);
      if (v === "PAPER" || v === "OKX_DEMO" || v === "REAL" || v === "all") return v;
    } catch { /* ignore */ }
    return "all";
  });
  const setVenue = (v: VenueView) => {
    setVenueState(v);
    try { localStorage.setItem(VENUE_STORAGE_KEY, v); } catch { /* ignore */ }
  };
  const venueValue = {
    venue,
    setVenue,
    executionModeParam: venue === "all" ? undefined : venue,
  };

  // Close drawer on route change
  useEffect(() => {
    setMobileOpen(false);
  }, [location.pathname]);

  return (
    <VenueContext.Provider value={venueValue}>
    <SidebarContext.Provider
      value={{
        mobileOpen,
        toggle: () => setMobileOpen((v) => !v),
        close: () => setMobileOpen(false),
        open: () => setMobileOpen(true),
      }}
    >
      <div className="min-h-screen bg-background text-foreground font-sans relative overflow-hidden">
        <div className="grain" aria-hidden />
        <div className="absolute top-[-10%] left-[-10%] w-[40%] h-[40%] bg-accent/5 rounded-full blur-[120px] pointer-events-none" aria-hidden />
        <div className="absolute bottom-[-10%] right-[-10%] w-[40%] h-[40%] rounded-full blur-[120px] pointer-events-none dark:bg-indigo-500/5" style={{ background: 'radial-gradient(circle, rgba(30,58,138,0.04), transparent 70%)' }} aria-hidden />

        {/* Skip-to-content link — invisible until focused, satisfies WCAG 2.1 */}
        <a
          href="#main-content"
          className="sr-only focus:not-sr-only focus:absolute focus:top-3 focus:left-3 focus:z-[300] focus:px-3 focus:py-2 focus:rounded-md focus:bg-accent focus:text-black focus:font-bold focus:text-xs"
        >
          Skip to content
        </a>

        <div className="relative z-10 flex min-h-screen">
          <Sidebar />
          <div className="flex-1 flex flex-col min-w-0">
            <Topbar />
            <OfflineBanner />
            <main
              id="main-content"
              role="main"
              tabIndex={-1}
              className="flex-1 px-4 sm:px-8 py-6 sm:py-8 overflow-y-auto outline-none"
            >
              <Outlet />
            </main>
          </div>
        </div>

        {/* G-prefix hint pill */}
        {hotkeys.pendingG && (
          <div className="fixed bottom-6 left-1/2 -translate-x-1/2 z-[150] px-3 py-1.5 rounded-md bg-accent/15 border border-accent/30 text-accent text-[10px] font-mono font-bold tracking-widest pointer-events-none animate-in fade-in duration-100">
            G… (D / M / T / A / P / F / H / U / S)
          </div>
        )}

        <KeyboardShortcutsOverlay open={hotkeys.showHelp} onClose={hotkeys.closeHelp} />
        <OnboardingTour />
        <SessionLockOverlay />
        <FirstTradeCelebration />
      </div>
    </SidebarContext.Provider>
    </VenueContext.Provider>
  );
}
