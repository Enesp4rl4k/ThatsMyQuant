import { useEffect } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { getHubConnection } from "@/lib/signalr";
import { pushNotification } from "@/lib/notifications";
import type { PortfolioSummary } from "@/api/types";

interface PositionClosedEvent {
  symbol: string;
  strategyTag: string;
  exitReason: string;
  executionMode: string;
  side: string;
  netPnl: number;
  netPnlPercent: number;
  exitTimeUtc: string;
}

interface OperatorAlertEvent {
  severity: "info" | "success" | "warning" | "error";
  source: string;
  title: string;
  body: string;
  utcTime: string;
}

interface CircuitBreakerEvent {
  reason: string;
  realizedDrawdown: number;
  peakEquity: number;
  currentEquity: number;
  trippedUtc: string;
}

/**
 * Wires SignalR push events into TanStack Query cache + notifications bell.
 * Mount this once in AppShell. Falls back gracefully when the hub is unreachable
 * (queries continue using their own polling intervals).
 */
export function useRealtimeStream() {
  const qc = useQueryClient();

  useEffect(() => {
    const conn = getHubConnection();

    const onPortfolio = (data: PortfolioSummary) => {
      qc.setQueryData(["portfolio"], data);
    };

    const onPositionClosed = (e: PositionClosedEvent) => {
      const sign = e.netPnl >= 0 ? "+" : "";
      pushNotification({
        type: e.netPnl >= 0 ? "success" : "warning",
        title: `${e.symbol} closed`,
        body: `${sign}$${e.netPnl.toFixed(2)} (${(e.netPnlPercent * 100).toFixed(2)}%) · ${e.exitReason}`,
      });
      // Invalidate trades + positions so the tables refresh
      qc.invalidateQueries({ queryKey: ["trades", 100] });
      qc.invalidateQueries({ queryKey: ["positions"] });
    };

    const onCircuitBreaker = (e: CircuitBreakerEvent) => {
      pushNotification({
        type: "error",
        title: "🛑 Circuit Breaker Tripped",
        body: `${e.reason} · DD ${(Number(e.realizedDrawdown) * 100).toFixed(2)}% · Equity $${Number(
          e.currentEquity
        ).toFixed(0)}`,
      });
    };

    const onOperatorAlert = (e: OperatorAlertEvent) => {
      pushNotification({
        type: e.severity,
        title: `[${e.source}] ${e.title}`,
        body: e.body,
      });
    };

    conn.on("PortfolioUpdate", onPortfolio);
    conn.on("PositionClosed", onPositionClosed);
    conn.on("CircuitBreakerTripped", onCircuitBreaker);
    conn.on("OperatorAlert", onOperatorAlert);

    return () => {
      conn.off("PortfolioUpdate", onPortfolio);
      conn.off("PositionClosed", onPositionClosed);
      conn.off("CircuitBreakerTripped", onCircuitBreaker);
      conn.off("OperatorAlert", onOperatorAlert);
    };
  }, [qc]);
}
