import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { settingsApi } from "@/api/settings";
import { useVenue, venueColor, venueLabel, type VenueView } from "@/hooks/useVenue";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";

/**
 * Topbar exchange-mode switcher. Two-layer concept:
 *
 *   1. **View filter** (instant, client-side) — switches which subset of
 *      trades / positions / portfolio you see in the dashboard. No restart;
 *      just changes the `executionMode` query parameter on REST calls.
 *
 *   2. **Trading venue** (server-side, requires restart) — actually changes
 *      which exchange the bot trades on. Rewrites exchange_settings.json
 *      and reminds the user to restart Listener.Api + QuantEngine.Worker.
 *
 * Layer 1 is the instant click; Layer 2 is the "Apply to bot" sub-menu so
 * the user doesn't accidentally flip live mode while just exploring data.
 */

const VIEWS: { id: VenueView; label: string; sub: string }[] = [
  { id: "all",      label: "All Venues",  sub: "Combined view of every execution mode" },
  { id: "PAPER",    label: "Paper",       sub: "Simulator (no real exchange)" },
  { id: "OKX_DEMO", label: "OKX Demo",    sub: "OKX demo venue · real demo orders" },
  { id: "REAL",     label: "Live",        sub: "Real-money / live execution" },
];

const MODES: { id: string; isDemo: boolean; label: string; danger?: boolean; sub: string }[] = [
  { id: "OKXSwap",  isDemo: true,  label: "OKX Swap (Demo)", sub: "Active venue: OKX demo · paper-safe" },
  { id: "OKXSwap",  isDemo: false, label: "OKX Swap (Live)", sub: "Active venue: OKX live · REAL MONEY", danger: true },
  { id: "Binance",  isDemo: false, label: "Binance",          sub: "Active venue: Binance live · REAL MONEY", danger: true },
  { id: "Mock",     isDemo: true,  label: "Mock",            sub: "No real exchange (offline / dev mode)" },
];

export function ExchangeModeSwitcher() {
  const { venue, setVenue } = useVenue();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Active server-side mode (read for display only)
  const settingsQ = useQuery({
    queryKey: ["settings"],
    queryFn:  settingsApi.get,
    staleTime: 30_000,
  });
  const serverMode  = settingsQ.data?.exchange.mode  ?? "Mock";
  const serverIsDemo = settingsQ.data?.exchange.isDemo ?? false;

  // Mutation to flip server mode
  const switchM = useMutation({
    mutationFn: ({ mode, isDemo }: { mode: string; isDemo: boolean }) =>
      settingsApi.setExchangeMode(mode, isDemo),
    onSuccess: (res) => {
      pushNotification({
        type: "warning",
        title: `🔄 Bot mode set: ${res.mode}${res.isDemo ? " (Demo)" : ""}`,
        body: res.note,
      });
      qc.invalidateQueries({ queryKey: ["settings"] });
    },
    onError: (err: unknown) => pushNotification({
      type: "error",
      title: "Mode switch failed",
      body: err instanceof Error ? err.message : String(err),
    }),
  });

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (!ref.current) return;
      if (!ref.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDoc);
    document.addEventListener("keydown",   onKey);
    return () => {
      document.removeEventListener("mousedown", onDoc);
      document.removeEventListener("keydown",   onKey);
    };
  }, [open]);

  const tone = venueColor(venue);
  const toneClasses =
    tone === "amber"   ? "border-amber-400/40 bg-amber-500/15 text-amber-200" :
    tone === "cyan"    ? "border-cyan-400/40 bg-cyan-500/15 text-cyan-200" :
    tone === "emerald" ? "border-emerald-400/40 bg-emerald-500/15 text-emerald-200" :
                         "border-violet-400/40 bg-violet-500/15 text-violet-200";

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((v) => !v)}
        className={cn(
          "flex items-center gap-1.5 px-2.5 py-1 rounded-md text-[10px] font-mono uppercase tracking-widest font-black border transition-colors",
          toneClasses
        )}
        title={`Viewing: ${venueLabel(venue)} · Bot venue: ${serverMode}${serverIsDemo ? " (demo)" : " (live)"}`}
      >
        <span className="w-1.5 h-1.5 rounded-full bg-current" />
        {venueLabel(venue)}
        <span className="opacity-55 text-[8px] ml-0.5">▼</span>
      </button>

      {open && (
        <div className="absolute right-0 top-full mt-1.5 z-[120] w-[320px] rounded-xl border border-white/10 bg-[#0D1117]/97 backdrop-blur-xl shadow-2xl overflow-hidden">
          {/* View filter section */}
          <div className="px-3 py-2 border-b border-white/[0.06]">
            <p className="text-[8px] font-black uppercase tracking-[0.3em] text-accent/70 mb-1.5">
              👁 View Filter — instant
            </p>
            <p className="text-[9px] text-muted-foreground/55 italic mb-2">
              Switch dashboard data view. No restart.
            </p>
            <div className="space-y-1">
              {VIEWS.map((v) => {
                const active = venue === v.id;
                return (
                  <button
                    key={v.id}
                    onClick={() => { setVenue(v.id); setOpen(false); }}
                    className={cn(
                      "w-full text-left px-2 py-1.5 rounded-md transition-colors",
                      active ? "bg-white/[0.06]" : "hover:bg-white/[0.04]"
                    )}
                  >
                    <div className="flex items-baseline justify-between">
                      <span className="text-[11px] font-bold">{v.label}</span>
                      {active && <span className="text-[9px] font-mono text-accent">✓ active</span>}
                    </div>
                    <p className="text-[9px] text-muted-foreground/55">{v.sub}</p>
                  </button>
                );
              })}
            </div>
          </div>

          {/* Bot venue section */}
          <div className="px-3 py-2">
            <p className="text-[8px] font-black uppercase tracking-[0.3em] text-rose-400/80 mb-1.5">
              🤖 Bot Venue — restart required
            </p>
            <p className="text-[9px] text-muted-foreground/55 italic mb-1.5">
              Where the BOT places real signals. Persists to <code>exchange_settings.json</code>.
            </p>
            <p className="text-[9px] text-muted-foreground/65 mb-2">
              Current: <span className="font-bold font-mono">{serverMode}{serverIsDemo ? " (demo)" : " (live)"}</span>
            </p>
            <div className="space-y-1">
              {MODES.map((m) => {
                const active = m.id === serverMode && m.isDemo === serverIsDemo;
                return (
                  <button
                    key={`${m.id}-${m.isDemo}`}
                    onClick={() => {
                      if (m.danger && !confirm(`⚠ Switching bot to ${m.label} = REAL MONEY trades. Continue?`)) return;
                      switchM.mutate({ mode: m.id, isDemo: m.isDemo });
                      setOpen(false);
                    }}
                    disabled={active || switchM.isPending}
                    className={cn(
                      "w-full text-left px-2 py-1.5 rounded-md transition-colors disabled:opacity-50 disabled:cursor-not-allowed",
                      active ? "bg-white/[0.06] cursor-default" : "hover:bg-white/[0.04]",
                      m.danger && !active && "hover:bg-rose-500/10"
                    )}
                  >
                    <div className="flex items-baseline justify-between">
                      <span className={cn(
                        "text-[11px] font-bold",
                        m.danger && !active && "text-rose-300"
                      )}>
                        {m.danger && "🚨 "}{m.label}
                      </span>
                      {active && <span className="text-[9px] font-mono text-emerald-300">● running</span>}
                    </div>
                    <p className="text-[9px] text-muted-foreground/55">{m.sub}</p>
                  </button>
                );
              })}
            </div>
            <p className="mt-2 text-[8px] font-mono italic text-muted-foreground/45">
              After mode switch: restart Listener.Api + QuantEngine.Worker so credentials reload.
            </p>
          </div>
        </div>
      )}
    </div>
  );
}
