import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { auditApi, type AuditEvent } from "@/api/audit";
import { getHubConnection } from "@/lib/signalr";
import { cn } from "@/lib/utils";

interface OperatorAlertPush {
  severity: "info" | "success" | "warning" | "error";
  source: string;
  title: string;
  body: string;
  utcTime: string;
}

interface TickerItem {
  key:    string;
  time:   string;
  severity: "info" | "success" | "warning" | "error";
  source: string;
  title:  string;
  body:   string;
}

const MAX_ITEMS = 8;

/**
 * Bottom-of-screen alert ticker for Trade Mode. Seeded from /api/audit/events
 * (last 8 events) and live-augmented via SignalR `OperatorAlert` push so new
 * events flow in real-time without polling. CSS marquee scroll.
 */
export function AlertTicker() {
  const [items, setItems] = useState<TickerItem[]>([]);

  // Initial seed via REST
  const seedQ = useQuery({
    queryKey: ["audit-ticker-seed"],
    queryFn:  () => auditApi.events({ limit: MAX_ITEMS }),
    staleTime: 60_000,
  });
  useEffect(() => {
    if (!seedQ.data) return;
    const seed = seedQ.data.slice(0, MAX_ITEMS).map(toItem);
    setItems(seed);
  }, [seedQ.data]);

  // Live SignalR augmentation
  useEffect(() => {
    const conn = getHubConnection();
    const onAlert = (e: OperatorAlertPush) => {
      setItems((prev) => {
        const next: TickerItem = {
          key:    `live-${e.utcTime}-${e.title}`,
          time:   e.utcTime,
          severity: e.severity,
          source: e.source,
          title:  e.title,
          body:   e.body,
        };
        return [next, ...prev].slice(0, MAX_ITEMS);
      });
    };
    conn.on("OperatorAlert", onAlert);
    return () => conn.off("OperatorAlert", onAlert);
  }, []);

  if (items.length === 0) {
    return (
      <div className="text-[10px] font-mono text-muted-foreground/40 italic">
        — no recent alerts —
      </div>
    );
  }

  return (
    <div className="relative overflow-hidden flex-1 min-w-0">
      <div className="flex items-center gap-6 whitespace-nowrap animate-[ticker_60s_linear_infinite] hover:[animation-play-state:paused]">
        {items.concat(items).map((it, i) => (
          <span key={`${it.key}-${i}`} className="inline-flex items-center gap-2 text-[10px] font-mono">
            <span className={cn("w-1.5 h-1.5 rounded-full", sevDot(it.severity))} />
            <span className="text-muted-foreground/45 uppercase tracking-widest text-[9px]">
              {fmtRel(it.time)}
            </span>
            <span className="text-muted-foreground/65 uppercase tracking-widest text-[9px]">
              {it.source}
            </span>
            <span className={cn("font-bold", sevText(it.severity))}>{it.title}</span>
            <span className="text-muted-foreground/55 truncate max-w-[280px]">{it.body}</span>
            <span className="text-muted-foreground/15">·</span>
          </span>
        ))}
      </div>
      {/* Edge fade for cleanliness */}
      <div className="absolute inset-y-0 left-0 w-8 bg-gradient-to-r from-black to-transparent pointer-events-none" />
      <div className="absolute inset-y-0 right-0 w-8 bg-gradient-to-l from-black to-transparent pointer-events-none" />
      {/* Keyframes inline (CSS @keyframes ticker) */}
      <style>{`
        @keyframes ticker {
          0%   { transform: translateX(0); }
          100% { transform: translateX(-50%); }
        }
      `}</style>
    </div>
  );
}

function toItem(e: AuditEvent): TickerItem {
  return {
    key:    `seed-${e.id}`,
    time:   e.utcTime,
    severity: e.severity,
    source: e.source,
    title:  e.title,
    body:   e.body,
  };
}

function sevDot(s: TickerItem["severity"]): string {
  return s === "error"   ? "bg-rose-400"
       : s === "warning" ? "bg-amber-400"
       : s === "success" ? "bg-emerald-400"
       :                   "bg-cyan-400";
}
function sevText(s: TickerItem["severity"]): string {
  return s === "error"   ? "text-rose-300"
       : s === "warning" ? "text-amber-300"
       : s === "success" ? "text-emerald-300"
       :                   "text-cyan-300";
}
function fmtRel(iso: string): string {
  try {
    const d = new Date(iso);
    const diff = (Date.now() - d.getTime()) / 1000;
    if (diff < 60)   return `${Math.floor(diff)}s`;
    if (diff < 3600) return `${Math.floor(diff/60)}m`;
    if (diff < 86400) return `${Math.floor(diff/3600)}h`;
    return `${Math.floor(diff/86400)}d`;
  } catch { return ""; }
}
