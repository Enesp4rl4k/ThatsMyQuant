import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi } from "@/api/markets";
import { priceAlertStore, type PriceAlert } from "@/lib/priceAlerts";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/utils";

interface Props {
  symbol: string;
}

export function PriceAlertsCard({ symbol }: Props) {
  const [alerts, setAlerts] = useState<PriceAlert[]>(() =>
    priceAlertStore.forSymbol(symbol)
  );
  const [draftPrice, setDraftPrice] = useState("");
  const [draftDir, setDraftDir] = useState<"above" | "below">("above");

  // Sync with store mutations + symbol switches
  useEffect(() => {
    setAlerts(priceAlertStore.forSymbol(symbol));
    return priceAlertStore.subscribe((all) => {
      setAlerts(all.filter((a) => a.symbol === symbol));
    });
  }, [symbol]);

  // Live price feed — re-uses the symbols query (15s) for evaluation
  const symbolsQ = useQuery({
    queryKey: ["markets", "symbols"],
    queryFn: () => marketsApi.symbols(),
    refetchInterval: 15_000,
  });
  const last = symbolsQ.data?.find((s) => s.symbol === symbol)?.lastPrice;

  // Evaluate alerts whenever the price moves
  useEffect(() => {
    if (last && last > 0) {
      priceAlertStore.evaluate(symbol, last);
    }
  }, [symbol, last]);

  const submit = () => {
    const p = parseFloat(draftPrice);
    if (!Number.isFinite(p) || p <= 0) return;
    priceAlertStore.add(symbol, draftDir, p);
    setDraftPrice("");
  };

  const active = alerts.filter((a) => !a.triggeredUtc);
  const triggered = alerts.filter((a) => a.triggeredUtc);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden h-full flex flex-col">
      <div className="px-4 py-2.5 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          Alerts
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/60">
          {symbol} · {active.length} active{triggered.length > 0 ? ` · ${triggered.length} fired` : ""}
        </p>
      </div>

      {/* Add new alert */}
      <div className="px-4 py-3 border-b border-white/5 space-y-2">
        <div className="flex items-center gap-1.5">
          <button
            onClick={() => setDraftDir("above")}
            aria-pressed={draftDir === "above"}
            className={cn(
              "flex-1 py-1 rounded text-[10px] font-mono uppercase tracking-widest border transition-colors",
              draftDir === "above"
                ? "bg-emerald-500/15 text-emerald-400 border-emerald-500/30"
                : "border-white/10 text-muted-foreground hover:bg-white/[0.03]"
            )}
          >
            ↑ Above
          </button>
          <button
            onClick={() => setDraftDir("below")}
            aria-pressed={draftDir === "below"}
            className={cn(
              "flex-1 py-1 rounded text-[10px] font-mono uppercase tracking-widest border transition-colors",
              draftDir === "below"
                ? "bg-rose-500/15 text-rose-400 border-rose-500/30"
                : "border-white/10 text-muted-foreground hover:bg-white/[0.03]"
            )}
          >
            ↓ Below
          </button>
        </div>
        <div className="flex items-center gap-2">
          <input
            type="number"
            step="any"
            value={draftPrice}
            onChange={(e) => setDraftPrice(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") submit();
            }}
            placeholder={last ? last.toString() : "price…"}
            aria-label="Alert price"
            className="flex-1 bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
          <button
            onClick={submit}
            disabled={!draftPrice}
            className="px-3 py-1.5 rounded text-[10px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/40 hover:bg-accent/25 transition-colors disabled:opacity-40"
          >
            Set
          </button>
        </div>
        {last && (
          <p className="text-[9px] font-mono text-muted-foreground/40 italic">
            current {fmtPrice(last)}
          </p>
        )}
      </div>

      {/* Alert list */}
      <div className="flex-1 overflow-y-auto">
        {alerts.length === 0 ? (
          <p className="p-6 text-center text-xs italic font-serif text-muted-foreground/60">
            — no alerts on {symbol} —
          </p>
        ) : (
          <ul>
            {active.map((a) => (
              <Row key={a.id} alert={a} current={last} />
            ))}
            {triggered.length > 0 && (
              <li className="px-4 py-1.5 border-y border-white/5 bg-white/[0.02]">
                <div className="flex items-baseline justify-between">
                  <span className="text-[9px] uppercase tracking-widest text-muted-foreground/50">
                    fired
                  </span>
                  <button
                    onClick={() => priceAlertStore.clearTriggered()}
                    className="text-[9px] uppercase tracking-widest text-muted-foreground/50 hover:text-muted-foreground"
                  >
                    clear
                  </button>
                </div>
              </li>
            )}
            {triggered.map((a) => (
              <Row key={a.id} alert={a} current={last} fired />
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function Row({
  alert,
  current,
  fired = false,
}: {
  alert: PriceAlert;
  current: number | undefined;
  fired?: boolean;
}) {
  const tone = alert.direction === "above" ? "text-emerald-400" : "text-rose-400";
  const distance =
    current && current > 0
      ? ((alert.price - current) / current) * 100
      : null;

  return (
    <li
      className={cn(
        "px-4 py-2 border-b border-white/[0.04] flex items-center justify-between gap-3",
        fired && "opacity-60"
      )}
    >
      <div className="min-w-0">
        <p className="font-mono text-[12px] font-bold tabular-nums">
          <span className={tone}>{alert.direction === "above" ? "↑" : "↓"}</span>{" "}
          {fmtPrice(alert.price)}
        </p>
        <p className="text-[9px] text-muted-foreground/50 italic font-serif">
          {fired
            ? `fired ${formatDateTime(alert.triggeredUtc!)}`
            : distance !== null
              ? `${distance >= 0 ? "+" : ""}${distance.toFixed(2)}% away`
              : `set ${formatDateTime(alert.createdUtc)}`}
        </p>
      </div>
      <button
        onClick={() => priceAlertStore.remove(alert.id)}
        aria-label={`Remove alert at ${alert.price}`}
        className="shrink-0 w-6 h-6 rounded flex items-center justify-center text-muted-foreground/40 hover:text-rose-400 hover:bg-rose-500/10 transition-colors"
      >
        <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
          <path d="M2 2l6 6M8 2L2 8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
        </svg>
      </button>
    </li>
  );
}

function fmtPrice(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
