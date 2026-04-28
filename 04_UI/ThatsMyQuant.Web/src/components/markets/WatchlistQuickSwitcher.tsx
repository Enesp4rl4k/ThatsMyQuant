import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi } from "@/api/markets";
import { cn } from "@/lib/utils";

interface Props {
  activeSymbol: string;
  onSelect: (symbol: string) => void;
  /** Defaults if nothing pinned yet. */
  defaults?: string[];
}

const STORAGE_KEY = "tmq.tradeMode.watchlist";

/**
 * A small pinned-symbols strip: 3-8 favourite symbols persisted in
 * localStorage. One click switches the chart symbol. Plus a `+` button to
 * pin the current symbol; click an existing pin to switch, alt-click to
 * unpin.
 *
 * Lives below the position chip strip in Trade Mode so it can complement
 * the open-positions list (not all watched symbols are open positions).
 */
export function WatchlistQuickSwitcher({ activeSymbol, onSelect, defaults = ["BTCUSDT", "ETHUSDT", "SOLUSDT"] }: Props) {
  const [pinned, setPinned] = useState<string[]>(() => loadPinned() ?? defaults);

  const symbolsQ = useQuery({
    queryKey: ["markets", "symbols"],
    queryFn:  () => marketsApi.symbols(pinned),
    enabled: pinned.length > 0,
    refetchInterval: 15_000,
  });

  useEffect(() => { storePinned(pinned); }, [pinned]);

  const togglePin = (sym: string) => {
    const up = sym.toUpperCase();
    setPinned((prev) => prev.includes(up) ? prev.filter((s) => s !== up) : [...prev, up]);
  };

  const pinCurrent = () => {
    const up = activeSymbol.toUpperCase();
    if (!pinned.includes(up)) setPinned((p) => [...p, up]);
  };

  if (pinned.length === 0) return null;

  return (
    <div className="flex items-center gap-1.5 px-3 py-1.5 border-b border-white/[0.06] bg-black/85 overflow-x-auto">
      <span className="text-[9px] font-mono uppercase tracking-widest text-muted-foreground/45 shrink-0">
        ⭐ Watch
      </span>
      {pinned.map((sym) => {
        const snap = symbolsQ.data?.find((s) => s.symbol === sym);
        const change = snap?.changePct24h ?? 0;
        const active = sym.toUpperCase() === activeSymbol.toUpperCase();
        const tone = change >= 0 ? "text-emerald-400" : "text-rose-400";
        return (
          <button
            key={sym}
            onClick={(e) => {
              if (e.altKey) togglePin(sym);
              else onSelect(sym);
            }}
            title={`${sym}${snap ? ` · last ${snap.lastPrice.toFixed(2)} · 24h ${change >= 0 ? "+" : ""}${change.toFixed(2)}%` : ""} · alt+click unpin`}
            className={cn(
              "shrink-0 flex items-center gap-1.5 px-2 py-0.5 rounded-md border text-[10px] font-mono transition-colors",
              active
                ? "border-amber-400/45 bg-amber-500/15 text-amber-200"
                : "border-white/[0.07] bg-white/[0.02] text-muted-foreground/80 hover:bg-white/[0.05] hover:text-foreground"
            )}
          >
            <span className="font-bold">{sym}</span>
            {snap && (
              <span className={cn("tabular-nums text-[9px]", tone)}>
                {change >= 0 ? "+" : ""}{change.toFixed(1)}%
              </span>
            )}
          </button>
        );
      })}
      <button
        onClick={pinCurrent}
        disabled={pinned.includes(activeSymbol.toUpperCase())}
        className="shrink-0 px-1.5 py-0.5 rounded-md border border-cyan-400/30 bg-cyan-500/10 text-cyan-300 text-[10px] font-mono disabled:opacity-40 disabled:cursor-not-allowed hover:bg-cyan-500/20"
        title="Pin current symbol to watchlist"
      >
        + Pin {activeSymbol}
      </button>
    </div>
  );
}

function loadPinned(): string[] | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch { return null; }
}
function storePinned(p: string[]) {
  try { localStorage.setItem(STORAGE_KEY, JSON.stringify(p)); } catch { /* ignore */ }
}
