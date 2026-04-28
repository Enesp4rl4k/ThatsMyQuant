import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, type MarketSymbol } from "@/api/markets";
import { cn } from "@/lib/utils";

interface Props {
  open: boolean;
  onClose: () => void;
  onSelect: (symbol: string) => void;
}

/**
 * Cmd/Ctrl+K command palette. Fuzzy filters the loaded watchlist and lets
 * the user paste any custom symbol (e.g. "DOGEUSDT") that's not in the basket.
 * Arrow up/down to navigate, Enter to switch, Esc to close.
 */
export function QuickSwitcher({ open, onClose, onSelect }: Props) {
  const [query, setQuery] = useState("");
  const [cursor, setCursor] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  const symbolsQ = useQuery({
    queryKey: ["markets", "symbols"],
    queryFn: () => marketsApi.symbols(),
    staleTime: 60_000,
  });

  const all = symbolsQ.data ?? [];

  const matches = useMemo<MarketSymbol[]>(() => {
    if (!query) return all.slice(0, 12);
    const q = query.toUpperCase().replace(/[^A-Z0-9]/g, "");
    if (!q) return all.slice(0, 12);

    const scored = all
      .map((s) => {
        const sym = s.symbol;
        const score =
          sym === q ? 100 :
          sym.startsWith(q) ? 50 :
          sym.includes(q) ? 20 :
          0;
        return { s, score };
      })
      .filter((x) => x.score > 0)
      .sort((a, b) => b.score - a.score)
      .slice(0, 12)
      .map((x) => x.s);

    // Allow typing arbitrary symbol — if no match starts with query, offer raw
    if (scored.length === 0 && q.length >= 3) {
      return [{ symbol: q, lastPrice: 0, changePct24h: 0, sparkline: [] }];
    }
    return scored;
  }, [query, all]);

  useEffect(() => {
    if (open) {
      setQuery("");
      setCursor(0);
      // focus on next tick
      requestAnimationFrame(() => inputRef.current?.focus());
    }
  }, [open]);

  useEffect(() => {
    setCursor(0);
  }, [query]);

  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
        return;
      }
      if (e.key === "ArrowDown") {
        e.preventDefault();
        setCursor((c) => Math.min(matches.length - 1, c + 1));
        return;
      }
      if (e.key === "ArrowUp") {
        e.preventDefault();
        setCursor((c) => Math.max(0, c - 1));
        return;
      }
      if (e.key === "Enter") {
        e.preventDefault();
        const m = matches[cursor];
        if (m) {
          onSelect(m.symbol);
          onClose();
        }
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, matches, cursor, onSelect, onClose]);

  if (!open) return null;

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label="Symbol quick-switcher"
      onClick={onClose}
      className="fixed inset-0 z-[200] flex items-start justify-center pt-[12vh] p-4 bg-black/40 backdrop-blur-sm animate-in fade-in duration-100"
    >
      <div
        onClick={(e) => e.stopPropagation()}
        className="w-full max-w-lg rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-paper-lg overflow-hidden"
      >
        {/* Search input */}
        <div className="px-4 py-3 border-b border-white/5 flex items-center gap-3">
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" className="text-muted-foreground/50">
            <circle cx="7" cy="7" r="5" stroke="currentColor" strokeWidth="1.4" />
            <path d="M11 11l3.5 3.5" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
          </svg>
          <input
            ref={inputRef}
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Switch to symbol… (e.g. BTC, ETH, SOLUSDT)"
            className="flex-1 bg-transparent border-0 outline-0 text-[14px] font-mono"
          />
          <kbd className="text-[9px] font-mono px-1.5 py-0.5 rounded bg-white/5 border border-white/10 text-muted-foreground">
            Esc
          </kbd>
        </div>

        {/* Matches */}
        <ul className="max-h-[50vh] overflow-y-auto py-1">
          {matches.length === 0 ? (
            <li className="px-4 py-6 text-center text-xs italic font-serif text-muted-foreground/60">
              — no symbols match —
            </li>
          ) : (
            matches.map((m, i) => (
              <li
                key={m.symbol}
                onMouseEnter={() => setCursor(i)}
                onClick={() => {
                  onSelect(m.symbol);
                  onClose();
                }}
                className={cn(
                  "px-4 py-2 flex items-center justify-between gap-3 cursor-pointer transition-colors",
                  i === cursor ? "bg-accent/10" : "hover:bg-white/[0.03]"
                )}
              >
                <div className="flex items-center gap-3 min-w-0">
                  <span
                    className={cn(
                      "w-1.5 h-1.5 rounded-full shrink-0",
                      i === cursor ? "bg-accent" : "bg-muted-foreground/30"
                    )}
                  />
                  <span className="font-bold text-[13px]">{m.symbol}</span>
                  {m.lastPrice === 0 && (
                    <span className="text-[10px] italic font-serif text-muted-foreground/50">
                      ad-hoc lookup
                    </span>
                  )}
                </div>
                {m.lastPrice > 0 && (
                  <div className="flex items-baseline gap-3 shrink-0 text-[11px] font-mono tabular-nums">
                    <span>{fmt(m.lastPrice)}</span>
                    <span
                      className={cn(
                        "font-bold w-[60px] text-right",
                        m.changePct24h >= 0 ? "text-emerald-400" : "text-rose-400"
                      )}
                    >
                      {m.changePct24h >= 0 ? "+" : ""}
                      {m.changePct24h.toFixed(2)}%
                    </span>
                  </div>
                )}
              </li>
            ))
          )}
        </ul>

        {/* Footer */}
        <div className="px-4 py-2 border-t border-white/5 flex items-center justify-between text-[10px] font-mono text-muted-foreground/50">
          <div className="flex items-center gap-3">
            <span>
              <kbd className="px-1 py-0.5 rounded bg-white/5 border border-white/10 mr-1">↑↓</kbd>
              navigate
            </span>
            <span>
              <kbd className="px-1 py-0.5 rounded bg-white/5 border border-white/10 mr-1">↵</kbd>
              switch
            </span>
          </div>
          <span className="italic font-serif">{matches.length} match{matches.length !== 1 ? "es" : ""}</span>
        </div>
      </div>
    </div>
  );
}

function fmt(v: number): string {
  if (v >= 1000) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (v >= 1) return v.toFixed(2);
  return v.toFixed(5);
}
