import { useEffect, useRef, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { proModeApi } from "@/api/proMode";
import { cn } from "@/lib/utils";
import { pushNotification } from "@/lib/notifications";

/**
 * One-click critical actions surfaced from the Topbar — disarm bot, pause N
 * hours, kill switch. Bot ARM / RE-ARM / advanced controls still live on
 * Pro Mode (single source of truth); this is just the panic button.
 */
export function QuickActions() {
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, [open]);

  const disarmM = useMutation({
    mutationFn: (note: string) => proModeApi.disarm(note),
    onSuccess: (_, note) => {
      pushNotification({
        type: "info",
        title: "Bot disarmed",
        body: note,
      });
      qc.invalidateQueries({ queryKey: ["pro-mode"] });
      setOpen(false);
    },
    onError: (e) =>
      pushNotification({
        type: "error",
        title: "Disarm failed",
        body: e instanceof Error ? e.message : "unknown error",
      }),
  });

  const killM = useMutation({
    mutationFn: (note: string) => proModeApi.kill(note),
    onSuccess: () => {
      pushNotification({
        type: "error",
        title: "🛑 Kill switch fired",
        body: "All positions flattened",
      });
      qc.invalidateQueries({ queryKey: ["pro-mode"] });
      setOpen(false);
    },
  });

  const onPauseFor = (hours: number) => {
    const note = `Pause ${hours}h via Quick Actions @ ${new Date().toLocaleTimeString()}`;
    disarmM.mutate(note);
    // Schedule a polite reminder to re-arm. We can't auto-re-arm safely
    // (timer dies if tab closes), so this is just a notification heads-up.
    setTimeout(() => {
      pushNotification({
        type: "warning",
        title: `Pause window ended (${hours}h)`,
        body: "Re-arm the bot from Pro Mode if conditions are still good.",
      });
    }, hours * 3_600_000);
  };

  const onKill = () => {
    if (
      !confirm(
        "🛑 KILL SWITCH\n\nThis flattens ALL open positions immediately and disarms the bot. Continue?"
      )
    )
      return;
    killM.mutate("Kill switch via Quick Actions");
  };

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((v) => !v)}
        title="Critical operator actions — pause, kill, flatten"
        aria-label="Quick actions"
        className="flex items-center gap-1.5 px-2.5 py-1 rounded-md text-[10px] font-mono text-rose-400/80 border border-rose-500/30 bg-rose-500/[0.05] hover:bg-rose-500/[0.12] hover:text-rose-300 transition-colors"
      >
        <svg width="11" height="11" viewBox="0 0 16 16" fill="none">
          <path
            d="M8 1l1.5 5h5l-4 3 1.5 5L8 11l-4 3 1.5-5-4-3h5z"
            stroke="currentColor"
            strokeWidth="1.4"
            strokeLinejoin="round"
          />
        </svg>
        Quick Actions
      </button>

      {open && (
        <div className="absolute right-0 top-9 w-72 rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-2xl z-50 overflow-hidden">
          <div className="px-3 py-2 border-b border-white/5">
            <p className="text-[9px] font-black uppercase tracking-widest text-rose-400/80">
              Critical Operator Actions
            </p>
            <p className="text-[10px] font-serif italic text-muted-foreground/60 mt-0.5">
              full controls live on Pro Mode · these are panic buttons
            </p>
          </div>

          {/* Pause options */}
          <div className="px-3 py-2 border-b border-white/5 space-y-1.5">
            <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
              Pause bot for
            </p>
            <div className="grid grid-cols-3 gap-1.5">
              {[1, 4, 24].map((h) => (
                <button
                  key={h}
                  onClick={() => onPauseFor(h)}
                  disabled={disarmM.isPending}
                  className={cn(
                    "py-1.5 rounded text-[10px] font-mono uppercase tracking-widest border transition-colors",
                    "border-amber-500/30 bg-amber-500/[0.06] text-amber-400 hover:bg-amber-500/15",
                    "disabled:opacity-40 disabled:cursor-not-allowed"
                  )}
                >
                  {h < 24 ? `${h}h` : `${h / 24}d`}
                </button>
              ))}
            </div>
          </div>

          {/* Disarm immediately */}
          <button
            onClick={() => disarmM.mutate("Manual disarm via Quick Actions")}
            disabled={disarmM.isPending}
            className="w-full px-3 py-2.5 text-left text-[11px] hover:bg-white/[0.03] flex items-center gap-3 border-b border-white/5"
          >
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none" className="text-amber-400">
              <rect x="4" y="4" width="3" height="8" fill="currentColor" />
              <rect x="9" y="4" width="3" height="8" fill="currentColor" />
            </svg>
            <div>
              <p className="font-bold">Disarm bot</p>
              <p className="text-[9px] font-serif italic text-muted-foreground/60">
                stop scanning · keep open positions
              </p>
            </div>
          </button>

          {/* Kill switch */}
          <button
            onClick={onKill}
            disabled={killM.isPending}
            className="w-full px-3 py-2.5 text-left text-[11px] hover:bg-rose-500/10 flex items-center gap-3 group"
          >
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none" className="text-rose-400">
              <circle cx="8" cy="8" r="6" stroke="currentColor" strokeWidth="1.5" />
              <path d="M5 5l6 6M11 5l-6 6" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            </svg>
            <div>
              <p className="font-bold text-rose-400">🛑 Kill switch</p>
              <p className="text-[9px] font-serif italic text-rose-400/70">
                flatten ALL positions immediately
              </p>
            </div>
          </button>
        </div>
      )}
    </div>
  );
}
