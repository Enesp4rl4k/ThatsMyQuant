import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { pairsApi, type DiscoveryCandidate } from "@/api/pairs";
import { fundingApi } from "@/api/funding";
import { cn } from "@/lib/utils";

interface Props {
  /** When provided, clicking a discovery row switches the Trade Mode chart. */
  onSelectSymbol?: (symbol: string) => void;
}

/** Dispatch a global "prefill order entry" hint based on the discovery row. */
function prefillOrderHint(opts: { symbol: string; side?: "long" | "short"; entry?: number; riskPct?: number }) {
  document.dispatchEvent(new CustomEvent("tmq:prefill-order", { detail: opts }));
}

type Tab = "pairs" | "funding";

/**
 * "What the bot is finding right now" panel. Two tabs:
 *
 *   • Pairs    → top cointegrating pair candidates from
 *               PairsDiscoveryHostedService (every 6 h, JSON file).
 *   • Funding  → live funding rates for the configured watchlist;
 *               flags rows with |rate| ≥ 1 bp as opportunities.
 *
 * Each row is clickable; clicks call onSelectSymbol so the parent can
 * jump the Trade Mode chart to that symbol.
 */
export function BotDiscoveriesPanel({ onSelectSymbol }: Props) {
  const [tab, setTab] = useState<Tab>("pairs");

  const pairsDiscQ = useQuery({
    queryKey: ["bot-discoveries", "pairs"],
    queryFn:  pairsApi.discoveries,
    refetchInterval: 5 * 60_000, // refetch every 5 min — discovery service runs every 6 h
    staleTime: 60_000,
  });
  const fundingQ = useQuery({
    queryKey: ["bot-discoveries", "funding"],
    queryFn:  () => fundingApi.rates(),
    refetchInterval: 30_000,
    staleTime: 15_000,
  });

  const sortedPairs = useMemo(() => {
    const cs = pairsDiscQ.data?.candidates ?? [];
    return [...cs].sort((a, b) => b.qualityScore - a.qualityScore).slice(0, 12);
  }, [pairsDiscQ.data]);

  const sortedFunding = useMemo(() => {
    const rs = fundingQ.data ?? [];
    return [...rs].sort((a, b) => Math.abs(b.rateBps) - Math.abs(a.rateBps));
  }, [fundingQ.data]);

  return (
    <div className="rounded-xl border border-white/[0.05] bg-[#0A0A0A]/80 backdrop-blur-xl overflow-hidden flex flex-col">
      <div className="px-3 py-2 border-b border-white/[0.05] flex items-baseline justify-between shrink-0">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          🔭 Bot Discoveries
        </p>
        <div className="flex gap-0.5">
          <TabBtn active={tab === "pairs"}   onClick={() => setTab("pairs")}>
            Pairs
            {sortedPairs.length > 0 && (
              <span className="ml-1 text-[8px] text-muted-foreground/55">({sortedPairs.length})</span>
            )}
          </TabBtn>
          <TabBtn active={tab === "funding"} onClick={() => setTab("funding")}>
            Funding
            <span className="ml-1 text-[8px] text-muted-foreground/55">
              ({sortedFunding.filter((r) => Math.abs(r.rateBps) >= 1).length})
            </span>
          </TabBtn>
        </div>
      </div>

      <div className="overflow-y-auto max-h-[280px]">
        {tab === "pairs" ? (
          <PairsList rows={sortedPairs} loading={pairsDiscQ.isLoading} note={pairsDiscQ.data?.note} generatedUtc={pairsDiscQ.data?.generatedUtc ?? null} onSelect={onSelectSymbol} />
        ) : (
          <FundingList rows={sortedFunding} loading={fundingQ.isLoading} onSelect={onSelectSymbol} />
        )}
      </div>
    </div>
  );
}

// ── Pairs list ────────────────────────────────────────────────────────────

function PairsList({
  rows, loading, note, generatedUtc, onSelect,
}: {
  rows: DiscoveryCandidate[];
  loading: boolean;
  note?: string;
  generatedUtc: string | null;
  onSelect?: (symbol: string) => void;
}) {
  if (loading) return <Empty msg="Loading candidates…" />;
  if (rows.length === 0) {
    return <Empty msg={note ?? "No discovery candidates yet."} />;
  }
  return (
    <div>
      <div className="grid grid-cols-[1fr_44px_44px_38px] gap-1.5 px-3 py-1 text-[8px] uppercase tracking-widest text-muted-foreground/45 border-b border-white/[0.04] sticky top-0 bg-black/85">
        <span>Pair</span>
        <span className="text-right">Score</span>
        <span className="text-right">β</span>
        <span className="text-right">HL</span>
      </div>
      {rows.map((c) => {
        const [y, x] = c.pair.split(":");
        const tone = scoreTone(c.qualityScore);
        return (
          <button
            key={c.pair}
            onClick={() => {
              onSelect?.(y);
              // Pair trades don't have a clear directional side suggestion
              // until the user reads current z-score; just hint the symbol.
              prefillOrderHint({ symbol: y });
            }}
            className="w-full grid grid-cols-[1fr_44px_44px_38px] gap-1.5 px-3 py-1.5 text-[10px] font-mono hover:bg-white/[0.03] border-b border-white/[0.02] text-left items-baseline"
            title={`Click to inspect ${y} on chart + pre-fill order entry · ADF ${c.adfTStat.toFixed(2)} · Hurst ${c.hurstExponent.toFixed(2)} · ρ ${c.correlation.toFixed(2)}`}
          >
            <span className="truncate">
              <span className="text-foreground/85 font-bold">{y}</span>
              <span className="text-muted-foreground/40 mx-0.5">/</span>
              <span className="text-foreground/85 font-bold">{x}</span>
            </span>
            <span className={cn("text-right tabular-nums font-bold", tone)}>
              {c.qualityScore.toFixed(0)}
            </span>
            <span className="text-right tabular-nums text-muted-foreground/65">{c.beta.toFixed(2)}</span>
            <span className="text-right tabular-nums text-muted-foreground/65">{c.halfLifeBars.toFixed(0)}b</span>
          </button>
        );
      })}
      {generatedUtc && (
        <p className="px-3 py-1.5 text-[8px] font-mono uppercase tracking-widest text-muted-foreground/35">
          Last scan: {new Date(generatedUtc).toLocaleString([], { dateStyle: "short", timeStyle: "short" })}
        </p>
      )}
    </div>
  );
}

// ── Funding list ──────────────────────────────────────────────────────────

interface FundingRow {
  symbol: string;
  rateBps: number;
  signal?: string;
  sparklineBps?: number[];
}

function FundingList({
  rows, loading, onSelect,
}: {
  rows: FundingRow[];
  loading: boolean;
  onSelect?: (symbol: string) => void;
}) {
  if (loading) return <Empty msg="Loading funding rates…" />;
  if (rows.length === 0) return <Empty msg="No funding watchlist configured." />;
  return (
    <div>
      <div className="grid grid-cols-[1fr_64px_70px] gap-1.5 px-3 py-1 text-[8px] uppercase tracking-widest text-muted-foreground/45 border-b border-white/[0.04] sticky top-0 bg-black/85">
        <span>Symbol</span>
        <span className="text-right">Rate (bp)</span>
        <span className="text-right">Signal</span>
      </div>
      {rows.map((r) => {
        const isOpp = Math.abs(r.rateBps) >= 1;
        const positive = r.rateBps >= 0;
        const sigTone =
          r.signal?.toLowerCase().includes("strong") ? (positive ? "text-emerald-300" : "text-rose-300") :
          r.signal?.toLowerCase().includes("mod")    ? "text-amber-300" :
                                                       "text-muted-foreground/55";
        return (
          <button
            key={r.symbol}
            onClick={() => {
              onSelect?.(r.symbol);
              // Funding alpha = FADE the funding direction.
              // Positive funding rate → longs are paying shorts → open SHORT to collect.
              // Negative funding rate → shorts paying longs → open LONG to collect.
              prefillOrderHint({
                symbol: r.symbol,
                side: r.rateBps >= 0 ? "short" : "long",
                riskPct: 0.5,
              });
            }}
            className={cn(
              "w-full grid grid-cols-[1fr_64px_70px] gap-1.5 px-3 py-1.5 text-[10px] font-mono hover:bg-white/[0.03] border-b border-white/[0.02] text-left items-baseline",
              isOpp && "bg-white/[0.015]"
            )}
            title={`${r.symbol} funding ${r.rateBps.toFixed(2)} bp · suggests ${r.rateBps >= 0 ? "SHORT (fade longs paying)" : "LONG (collect funding)"} · click to chart + pre-fill`}
          >
            <span className="truncate">
              {isOpp && <span className="text-amber-400 mr-0.5">●</span>}
              <span className="text-foreground/85 font-bold">{r.symbol}</span>
            </span>
            <span className={cn("text-right tabular-nums font-bold", positive ? "text-emerald-300" : "text-rose-300")}>
              {positive ? "+" : ""}{r.rateBps.toFixed(2)}
            </span>
            <span className={cn("text-right text-[9px] uppercase tracking-widest font-black", sigTone)}>
              {r.signal ?? "—"}
            </span>
          </button>
        );
      })}
    </div>
  );
}

function TabBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      role="tab"
      aria-selected={active}
      className={cn(
        "px-2 py-0.5 rounded text-[9px] font-mono uppercase tracking-widest font-black transition-colors",
        active
          ? "bg-foreground/85 text-background"
          : "text-muted-foreground/55 hover:text-foreground"
      )}
    >
      {children}
    </button>
  );
}

function Empty({ msg }: { msg: string }) {
  return <p className="text-[11px] italic font-serif text-muted-foreground/50 text-center py-4">— {msg} —</p>;
}

function scoreTone(score: number): string {
  if (score >= 80) return "text-emerald-300";
  if (score >= 60) return "text-cyan-300";
  if (score >= 40) return "text-amber-300";
  return "text-muted-foreground/65";
}
