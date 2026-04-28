import { useState, useEffect, useCallback } from "react";
import { Link } from "react-router-dom";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";
import { subscribeToNotifications, type Notification } from "@/lib/notifications";

export type { Notification };

export function NotificationsBell() {
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState<Notification[]>([]);

  const addItem = useCallback((n: Notification) => {
    setItems((prev) => [n, ...prev].slice(0, 50));
  }, []);

  useEffect(() => {
    return subscribeToNotifications(addItem);
  }, [addItem]);

  const unread = items.filter((i) => !i.read).length;

  const markAllRead = () =>
    setItems((prev) => prev.map((i) => ({ ...i, read: true })));

  return (
    <div className="relative">
      <button
        onClick={() => {
          setOpen((v) => !v);
          if (!open) markAllRead();
        }}
        className="relative flex items-center justify-center w-8 h-8 rounded-md text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.05] transition-colors"
        title="Notifications"
        aria-label="Notifications"
      >
        <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
          <path
            d="M8 1a5 5 0 0 0-5 5v2.5L1.5 10h13L13 8.5V6A5 5 0 0 0 8 1z"
            stroke="currentColor"
            strokeWidth="1.2"
            fill="none"
          />
          <path d="M6.5 13a1.5 1.5 0 0 0 3 0" stroke="currentColor" strokeWidth="1.2" fill="none" />
        </svg>
        {unread > 0 && (
          <span className="absolute -top-0.5 -right-0.5 min-w-[14px] h-[14px] flex items-center justify-center rounded-full bg-rose-500 text-white text-[8px] font-black leading-none px-0.5">
            {unread > 9 ? "9+" : unread}
          </span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 top-10 w-80 rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-2xl z-50 overflow-hidden">
          <div className="flex items-center justify-between px-4 py-3 border-b border-white/5">
            <span className="text-[10px] font-black uppercase tracking-widest text-muted-foreground/60">
              Notifications
            </span>
            <div className="flex items-center gap-3">
              <Link
                to="/settings"
                onClick={() => setOpen(false)}
                title="Configure sound + desktop alerts"
                className="text-[10px] text-muted-foreground/40 hover:text-accent transition-colors flex items-center gap-1"
              >
                <svg width="10" height="10" viewBox="0 0 16 16" fill="none">
                  <circle cx="8" cy="8" r="2.5" stroke="currentColor" strokeWidth="1.4" />
                  <path
                    d="M8 1v2M8 13v2M1 8h2M13 8h2M2.8 2.8l1.4 1.4M11.8 11.8l1.4 1.4M2.8 13.2l1.4-1.4M11.8 4.2l1.4-1.4"
                    stroke="currentColor"
                    strokeWidth="1.4"
                    strokeLinecap="round"
                  />
                </svg>
                Configure
              </Link>
              {items.length > 0 && (
                <button
                  onClick={() => setItems([])}
                  className="text-[10px] text-muted-foreground/40 hover:text-muted-foreground transition-colors"
                >
                  Clear all
                </button>
              )}
            </div>
          </div>
          <div className="max-h-80 overflow-y-auto">
            {items.length === 0 ? (
              <p className="p-6 text-center text-xs text-muted-foreground/40">
                No notifications yet
              </p>
            ) : (
              items.map((n) => (
                <div
                  key={n.id}
                  className={cn(
                    "px-4 py-3 border-b border-white/[0.03] last:border-0",
                    !n.read && "bg-accent/[0.03]"
                  )}
                >
                  <div className="flex items-start gap-2">
                    <span
                      className={cn(
                        "mt-0.5 w-1.5 h-1.5 rounded-full shrink-0",
                        n.type === "error" && "bg-rose-400",
                        n.type === "warning" && "bg-amber-400",
                        n.type === "success" && "bg-emerald-400",
                        n.type === "info" && "bg-accent"
                      )}
                    />
                    <div className="flex-1 min-w-0">
                      <p className="text-xs font-bold leading-tight">{n.title}</p>
                      <p className="text-[10px] text-muted-foreground/60 mt-0.5 leading-snug">
                        {n.body}
                      </p>
                      <p className="text-[9px] text-muted-foreground/30 mt-1 font-mono">
                        {formatDateTime(n.time.toISOString())}
                      </p>
                    </div>
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}
