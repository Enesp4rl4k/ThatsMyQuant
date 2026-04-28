import { useEffect, useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { getHubConnection } from "@/lib/signalr";
import { formatUsd } from "@/lib/format";
import { cn } from "@/lib/utils";

const STORAGE_KEY = "tmq.firstTrade.celebrated";

interface PositionClosedEvent {
  symbol: string;
  strategyTag: string;
  exitReason: string;
  side: string;
  netPnl: number;
  netPnlPercent: number;
  exitTimeUtc: string;
}

/**
 * Listens once for the FIRST PositionClosed SignalR event after the user
 * starts the app for the first time. Shows a full-screen celebration modal:
 * confetti-style emoji burst, big PnL number, strategy tag, "Continue" CTA.
 * Plays a chime via Web Audio. Persists to localStorage so it only fires once.
 *
 * For subsequent closes there's a smaller toast (the existing
 * useRealtimeStream pushNotification handles those — this layer is purely
 * for the milestone moment).
 */
export function FirstTradeCelebration() {
  const [event, setEvent] = useState<PositionClosedEvent | null>(null);
  const [alreadyDone, setAlreadyDone] = useState<boolean>(() => {
    try { return localStorage.getItem(STORAGE_KEY) === "1"; } catch { return false; }
  });

  useEffect(() => {
    if (alreadyDone) return;
    const conn = getHubConnection();
    let active = true;
    const onClose = (e: PositionClosedEvent) => {
      if (!active) return;
      setEvent(e);
      try { localStorage.setItem(STORAGE_KEY, "1"); } catch { /* ignore */ }
      setAlreadyDone(true);
      // Triumphant chime
      try {
        const ctx = new (window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext)();
        [523.25, 659.25, 783.99, 1046.50].forEach((freq, i) => {
          const osc = ctx.createOscillator();
          const gain = ctx.createGain();
          osc.frequency.value = freq;
          osc.type = "sine";
          const t = ctx.currentTime + i * 0.12;
          gain.gain.setValueAtTime(0.0001, t);
          gain.gain.exponentialRampToValueAtTime(0.18, t + 0.02);
          gain.gain.exponentialRampToValueAtTime(0.0001, t + 0.45);
          osc.connect(gain).connect(ctx.destination);
          osc.start(t);
          osc.stop(t + 0.5);
        });
      } catch { /* silent */ }
    };
    conn.on("PositionClosed", onClose);
    return () => { active = false; conn.off("PositionClosed", onClose); };
  }, [alreadyDone]);

  if (!event) return null;

  const isWin = event.netPnl >= 0;
  const close = () => setEvent(null);

  return (
    <AnimatePresence>
      {event && (
        <motion.div
          key="celebration-backdrop"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          className="fixed inset-0 z-[400] flex items-center justify-center bg-black/85 backdrop-blur-md p-6"
          onClick={close}
        >
          {/* Confetti burst */}
          <div className="absolute inset-0 pointer-events-none overflow-hidden">
            {Array.from({ length: 28 }).map((_, i) => {
              const angle = (i / 28) * Math.PI * 2;
              const dist = 280 + Math.random() * 200;
              const dx = Math.cos(angle) * dist;
              const dy = Math.sin(angle) * dist;
              const emoji = ["✨", "🎉", "🎊", "💫", "⭐", "🚀"][i % 6];
              return (
                <motion.span
                  key={i}
                  initial={{ x: 0, y: 0, opacity: 0, scale: 0.4 }}
                  animate={{ x: dx, y: dy, opacity: [0, 1, 0], scale: [0.4, 1.1, 0.7] }}
                  transition={{ duration: 1.6 + (i % 3) * 0.3, ease: "easeOut" }}
                  className="absolute left-1/2 top-1/2 text-2xl"
                >
                  {emoji}
                </motion.span>
              );
            })}
          </div>

          <motion.div
            key="celebration-card"
            initial={{ scale: 0.7, y: 40, opacity: 0 }}
            animate={{ scale: 1,   y: 0,  opacity: 1 }}
            exit={{    scale: 0.85, y: 0, opacity: 0 }}
            transition={{ type: "spring", stiffness: 320, damping: 24 }}
            onClick={(e) => e.stopPropagation()}
            className={cn(
              "relative max-w-md w-full rounded-3xl border p-8 shadow-2xl backdrop-blur-xl text-center",
              isWin
                ? "border-emerald-400/30 bg-gradient-to-br from-[#0d2018]/95 via-[#0a1714]/95 to-[#0d1117]/95"
                : "border-rose-400/30 bg-gradient-to-br from-[#201013]/95 via-[#1a0e10]/95 to-[#0d1117]/95"
            )}
          >
            <p className="text-[10px] font-mono uppercase tracking-[0.4em] text-muted-foreground/55 mb-1">
              🏁 First Trade · ThatsMyQuant
            </p>
            <h2 className="font-serif text-3xl tracking-tight mb-2">
              {isWin ? "Bot just made its first profit." : "First trade closed in a loss."}
            </h2>
            <p className="text-[13px] font-serif italic text-muted-foreground/70 mb-6">
              {isWin
                ? "The edge is real — and it just paid out."
                : "Drawdown is part of every edge. The risk gates worked, capital is preserved."}
            </p>

            <div className={cn(
              "rounded-2xl border p-5 mb-5",
              isWin ? "border-emerald-400/20 bg-emerald-500/[0.05]"
                     : "border-rose-400/20    bg-rose-500/[0.05]"
            )}>
              <p className={cn(
                "font-mono font-black text-5xl tabular-nums tracking-tight",
                isWin ? "text-emerald-300" : "text-rose-300"
              )}>
                {isWin ? "+" : ""}{formatUsd(event.netPnl)}
              </p>
              <p className={cn("font-mono text-sm tabular-nums mt-1", isWin ? "text-emerald-400/80" : "text-rose-400/80")}>
                {(event.netPnlPercent * 100).toFixed(2)}%
              </p>
            </div>

            <div className="grid grid-cols-3 gap-3 text-[10px] font-mono uppercase tracking-widest mb-6">
              <div>
                <p className="text-muted-foreground/45 mb-1">Symbol</p>
                <p className="font-bold text-foreground/90">{event.symbol}</p>
              </div>
              <div>
                <p className="text-muted-foreground/45 mb-1">Side</p>
                <p className="font-bold text-foreground/90">{event.side}</p>
              </div>
              <div>
                <p className="text-muted-foreground/45 mb-1">Strategy</p>
                <p className="font-bold text-foreground/90 truncate" title={event.strategyTag}>{event.strategyTag}</p>
              </div>
            </div>

            <p className="text-[10px] font-mono text-muted-foreground/45 mb-4">
              Exit reason: <span className="text-foreground/75">{event.exitReason}</span>
            </p>

            <button
              onClick={close}
              className={cn(
                "w-full px-4 py-2.5 rounded-md text-[11px] font-black uppercase tracking-widest border transition-colors",
                isWin
                  ? "border-emerald-400/40 bg-emerald-500/15 text-emerald-200 hover:bg-emerald-500/25"
                  : "border-rose-400/40 bg-rose-500/15 text-rose-200 hover:bg-rose-500/25"
              )}
            >
              Continue trading
            </button>
            <p className="mt-3 text-[8px] font-mono uppercase tracking-widest text-muted-foreground/30">
              this celebration appears once · subsequent closes show as toasts
            </p>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
