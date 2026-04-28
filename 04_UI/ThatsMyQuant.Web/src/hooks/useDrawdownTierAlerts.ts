import { useEffect, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { pushNotification } from "@/lib/notifications";

/**
 * Watches the equity timeline and fires tiered drawdown alerts BEFORE the
 * circuit-breaker would trip. The CB is hard-coded at 5% — this hook adds
 * early warnings:
 *
 *   1.0% DD  → info notification (one-time per session)
 *   2.5% DD  → warning ("approaching half-CB threshold")
 *   4.0% DD  → error  ("CB will trip at 5%, take action")
 *
 * Alerts persist via sessionStorage keyed by tier so they fire ONCE per
 * day per session — no spam if DD oscillates around a threshold.
 */
export function useDrawdownTierAlerts() {
  const equityQ = useQuery({
    queryKey: ["equity-timeline-dd"],
    queryFn:  dashboardApi.equityTimeline,
    refetchInterval: 30_000,
  });

  const firedRef = useRef<Set<string>>(new Set());
  // Hydrate from sessionStorage
  useEffect(() => {
    try {
      const raw = sessionStorage.getItem("tmq.ddTier.fired");
      if (raw) firedRef.current = new Set(JSON.parse(raw));
    } catch { /* ignore */ }
  }, []);

  useEffect(() => {
    if (!equityQ.data || equityQ.data.length < 2) return;
    const points = equityQ.data;
    let peak = -Infinity;
    let curDD = 0;
    for (const p of points) {
      const eq = p.cumulativeEquity;
      if (eq > peak) peak = eq;
      const dd = peak !== 0 ? (peak - eq) / Math.abs(peak) : 0;
      if (dd > curDD) curDD = dd;
    }
    // Only alert on the LATEST point (current state)
    const last = points[points.length - 1];
    const lastDD = peak !== 0 ? (peak - last.cumulativeEquity) / Math.abs(peak) : 0;
    const ddPct = lastDD * 100;

    const today = new Date().toISOString().slice(0, 10);
    const tiers: { threshold: number; type: "info" | "warning" | "error"; title: string; body: string }[] = [
      { threshold: 1.0, type: "info",    title: "📉 Drawdown 1%",  body: "Portfolio entered drawdown. Standard fluctuation — monitor for continuation." },
      { threshold: 2.5, type: "warning", title: "⚠ Drawdown 2.5%", body: "Halfway to circuit-breaker (5%). Consider pausing new entries until structure stabilises." },
      { threshold: 4.0, type: "error",   title: "🚨 Drawdown 4%",  body: "Circuit breaker fires at 5% DD. Auto-flatten will trigger soon. Manual exit possible from Cockpit." },
    ];

    for (const tier of tiers) {
      if (ddPct >= tier.threshold) {
        const key = `${today}-${tier.threshold}`;
        if (!firedRef.current.has(key)) {
          firedRef.current.add(key);
          try { sessionStorage.setItem("tmq.ddTier.fired", JSON.stringify(Array.from(firedRef.current))); } catch { /* ignore */ }
          pushNotification({
            type: tier.type,
            title: tier.title,
            body:  `${tier.body} (current: ${ddPct.toFixed(2)}%)`,
          });
        }
      }
    }
  }, [equityQ.data]);
}
