import { useState, useEffect, useCallback } from "react";
import { cn } from "@/lib/utils";
import { subscribeToNotifications, type Notification } from "@/lib/notifications";

interface ToastItem extends Notification {
  exiting: boolean;
}

const DURATION: Record<Notification["type"], number> = {
  error: 8_000,
  warning: 6_000,
  success: 4_000,
  info: 4_000,
};

const ICON: Record<Notification["type"], string> = {
  error: "✕",
  warning: "⚠",
  success: "✓",
  info: "i",
};

const TONE: Record<Notification["type"], string> = {
  error: "border-rose-500/50 bg-rose-500/[0.08] text-rose-300",
  warning: "border-amber-500/50 bg-amber-500/[0.08] text-amber-300",
  success: "border-emerald-500/50 bg-emerald-500/[0.08] text-emerald-300",
  info: "border-accent/40 bg-accent/[0.08] text-accent",
};

const ICON_TONE: Record<Notification["type"], string> = {
  error: "bg-rose-500/20 text-rose-400",
  warning: "bg-amber-500/20 text-amber-400",
  success: "bg-emerald-500/20 text-emerald-400",
  info: "bg-accent/20 text-accent",
};

function Toast({ item, onDismiss }: { item: ToastItem; onDismiss: (id: string) => void }) {
  return (
    <div
      className={cn(
        "flex items-start gap-3 rounded-xl border px-4 py-3 shadow-2xl backdrop-blur-xl min-w-0 max-w-sm w-full",
        "transition-all duration-300",
        item.exiting ? "opacity-0 translate-x-4" : "opacity-100 translate-x-0",
        TONE[item.type]
      )}
    >
      <span
        className={cn(
          "shrink-0 w-5 h-5 rounded-full flex items-center justify-center text-[10px] font-black mt-0.5",
          ICON_TONE[item.type]
        )}
      >
        {ICON[item.type]}
      </span>
      <div className="flex-1 min-w-0">
        <p className="text-xs font-bold leading-tight truncate">{item.title}</p>
        {item.body && (
          <p className="text-[10px] opacity-70 mt-0.5 leading-snug line-clamp-2">{item.body}</p>
        )}
      </div>
      <button
        onClick={() => onDismiss(item.id)}
        className="shrink-0 opacity-50 hover:opacity-100 transition-opacity text-[10px] mt-0.5"
        aria-label="Dismiss"
      >
        ✕
      </button>
    </div>
  );
}

export function Toaster() {
  const [toasts, setToasts] = useState<ToastItem[]>([]);

  const dismiss = useCallback((id: string) => {
    setToasts((prev) =>
      prev.map((t) => (t.id === id ? { ...t, exiting: true } : t))
    );
    setTimeout(() => {
      setToasts((prev) => prev.filter((t) => t.id !== id));
    }, 320);
  }, []);

  useEffect(() => {
    return subscribeToNotifications((n) => {
      const item: ToastItem = { ...n, exiting: false };
      setToasts((prev) => [item, ...prev].slice(0, 6));

      const timer = setTimeout(() => dismiss(n.id), DURATION[n.type]);
      return () => clearTimeout(timer);
    });
  }, [dismiss]);

  if (toasts.length === 0) return null;

  return (
    <div
      className="fixed bottom-6 right-6 z-[100] flex flex-col-reverse gap-2 pointer-events-none"
      aria-live="polite"
      aria-label="Notifications"
    >
      {toasts.map((t) => (
        <div key={t.id} className="pointer-events-auto">
          <Toast item={t} onDismiss={dismiss} />
        </div>
      ))}
    </div>
  );
}
