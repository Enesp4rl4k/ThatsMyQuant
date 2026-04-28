import { useQuery } from "@tanstack/react-query";
import { fundingApi } from "@/api/funding";
import { Sparkline } from "@/components/charts/Sparkline";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";

export function FundingMonitorPage() {
  const ratesQ = useQuery({
    queryKey: ["funding-rates"],
    queryFn: () => fundingApi.rates(),
    refetchInterval: 60_000,
  });

  return (
    <div className="space-y-8">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Perp Funding
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Funding Monitor{" "}
          <span className="text-muted-foreground/30 font-light">— 8h rates per perp · auto-refresh 60s</span>
        </p>
      </div>

      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
        {ratesQ.isLoading ? (
          <div className="p-12 text-center text-sm text-muted-foreground">Loading funding rates…</div>
        ) : ratesQ.isError ? (
          <div className="p-12 text-center text-sm text-rose-400">
            Failed to load funding rates.
          </div>
        ) : (ratesQ.data ?? []).length === 0 ? (
          <div className="p-12 text-center text-sm text-muted-foreground">No symbols configured.</div>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60">
                <th className="text-left px-5 py-3 font-black">Symbol</th>
                <th className="text-right px-3 py-3 font-black">Rate (bp / 8h)</th>
                <th className="text-left px-3 py-3 font-black">Signal</th>
                <th className="text-left px-3 py-3 font-black">Sparkline (48h)</th>
                <th className="text-left px-5 py-3 font-black">Last Update</th>
              </tr>
            </thead>
            <tbody>
              {(ratesQ.data ?? []).map((row) => (
                <tr key={row.symbol} className="border-b border-white/[0.02] hover:bg-white/[0.02]">
                  <td className="px-5 py-3 font-bold">{row.symbol}</td>
                  <td
                    className={cn(
                      "px-3 py-3 text-right font-mono font-bold",
                      row.rateBps > 0 ? "text-emerald-400" : row.rateBps < 0 ? "text-rose-400" : "text-muted-foreground"
                    )}
                  >
                    {row.rateBps.toFixed(2)}
                  </td>
                  <td className="px-3 py-3">
                    <SignalBadge signal={row.signal} />
                  </td>
                  <td className="px-3 py-3">
                    <Sparkline values={row.sparklineBps} width={120} height={24} zeroLine />
                  </td>
                  <td className="px-5 py-3 text-[10px] font-mono text-muted-foreground">
                    {formatDateTime(row.lastUpdatedUtc)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <p className="text-[11px] text-muted-foreground/50 max-w-2xl leading-relaxed">
        Positive funding = longs paying shorts (potential mean-reversion short setup if extreme). Negative funding = shorts paying longs.
        Strong threshold: ±3bp/8h. Mild: ±1bp/8h. Source: <code>IFundingRateProvider</code>.
      </p>
    </div>
  );
}

function SignalBadge({ signal }: { signal: string }) {
  const map: Record<string, { label: string; cls: string }> = {
    STRONG_LONG_FUNDING: { label: "STRONG ⇣ FUND LONGS", cls: "bg-rose-500/15 text-rose-400 border-rose-500/30" },
    MILD_LONG_FUNDING: { label: "MILD ⇣ FUND LONGS", cls: "bg-amber-500/15 text-amber-400 border-amber-500/30" },
    STRONG_SHORT_FUNDING: { label: "STRONG ⇡ FUND SHORTS", cls: "bg-emerald-500/15 text-emerald-400 border-emerald-500/30" },
    MILD_SHORT_FUNDING: { label: "MILD ⇡ FUND SHORTS", cls: "bg-cyan-500/15 text-cyan-400 border-cyan-500/30" },
    NEUTRAL: { label: "NEUTRAL", cls: "bg-zinc-500/15 text-zinc-400 border-zinc-500/30" },
  };
  const m = map[signal] ?? map.NEUTRAL;
  return (
    <span className={cn("px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-wider border", m.cls)}>
      {m.label}
    </span>
  );
}
