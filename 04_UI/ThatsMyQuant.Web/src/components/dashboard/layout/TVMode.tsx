import { useEffect, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";
import type { DashboardTab } from "./DashboardTabs";

interface Props {
  children: ReactNode;
  enabled: boolean;
  onExit: () => void;
  onTabChange?: (tab: DashboardTab) => void;
  rotateInterval?: number;
}

const ROTATE_ORDER: DashboardTab[] = ["trading", "risk", "performance", "ops"];

/**
 * TV mode wrapper — fullscreen API + auto-rotate between tabs every N seconds.
 * Hides app chrome (sidebar, topbar) via CSS. Esc exits.
 */
export function TVMode({
  children,
  enabled,
  onExit,
  onTabChange,
  rotateInterval = 30_000,
}: Props) {
  const [rotateIdx, setRotateIdx] = useState(0);
  const [paused, setPaused] = useState(false);
  const [secondsLeft, setSecondsLeft] = useState(rotateInterval / 1000);

  useEffect(() => {
    if (!enabled) return;

    // Request fullscreen on enter
    const el = document.documentElement;
    if (el.requestFullscreen && !document.fullscreenElement) {
      el.requestFullscreen().catch(() => {});
    }

    document.body.dataset.tvMode = "true";
    return () => {
      delete document.body.dataset.tvMode;
      if (document.fullscreenElement && document.exitFullscreen) {
        document.exitFullscreen().catch(() => {});
      }
    };
  }, [enabled]);

  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onExit();
      if (e.key.toLowerCase() === "p") setPaused((v) => !v);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [enabled, onExit]);

  useEffect(() => {
    if (!enabled || paused) return;
    setSecondsLeft(rotateInterval / 1000);
    const tick = setInterval(() => {
      setSecondsLeft((s) => Math.max(0, s - 1));
    }, 1000);
    const rot = setInterval(() => {
      setRotateIdx((i) => (i + 1) % ROTATE_ORDER.length);
      setSecondsLeft(rotateInterval / 1000);
    }, rotateInterval);
    return () => {
      clearInterval(tick);
      clearInterval(rot);
    };
  }, [enabled, paused, rotateInterval]);

  useEffect(() => {
    if (enabled && onTabChange) {
      onTabChange(ROTATE_ORDER[rotateIdx]);
    }
  }, [enabled, rotateIdx, onTabChange]);

  if (!enabled) return <>{children}</>;

  return (
    <div className="fixed inset-0 z-[180] bg-background overflow-y-auto">
      {/* TV overlay header */}
      <div className="sticky top-0 z-10 px-6 py-3 border-b border-accent/20 bg-background/95 backdrop-blur-xl flex items-center justify-between">
        <div className="flex items-center gap-3">
          <span className="w-2 h-2 rounded-full bg-rose-400 animate-pulse" />
          <span className="font-serif text-[16px] tracking-tight">TV Mode</span>
          <span className="text-[10px] uppercase tracking-widest text-muted-foreground/60">
            auto-rotate · {ROTATE_ORDER[rotateIdx]} · {secondsLeft}s
          </span>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => setPaused((v) => !v)}
            className={cn(
              "px-2.5 py-1 rounded text-[10px] font-mono uppercase tracking-widest border transition-colors",
              paused
                ? "bg-accent/15 text-accent border-accent/30"
                : "border-white/10 text-muted-foreground hover:text-foreground"
            )}
          >
            {paused ? "▶ play" : "⏸ pause"}
          </button>
          <button
            onClick={onExit}
            className="px-2.5 py-1 rounded text-[10px] font-mono uppercase tracking-widest border border-white/10 hover:border-rose-500/40 hover:text-rose-400 transition-colors"
          >
            exit · esc
          </button>
        </div>
      </div>

      <div className="p-6">{children}</div>
    </div>
  );
}
