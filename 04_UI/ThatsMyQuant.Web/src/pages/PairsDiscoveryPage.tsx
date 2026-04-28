import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { pairsApi } from "@/api/pairs";
import type { PairCandidate, PairTestResponse } from "@/api/pairs";
import { LineChart } from "@/components/charts/LineChart";
import { cn } from "@/lib/utils";

const DEFAULT_UNIVERSE = "BTC-USDT-SWAP,ETH-USDT-SWAP,SOL-USDT-SWAP,AVAX-USDT-SWAP,LINK-USDT-SWAP,DOT-USDT-SWAP,TRX-USDT-SWAP,XLM-USDT-SWAP";

export function PairsDiscoveryPage() {
  const [universe, setUniverse] = useState(DEFAULT_UNIVERSE);
  const [timeframe, setTimeframe] = useState("4H");
  const [days, setDays] = useState(180);
  const [topN, setTopN] = useState(10);

  const discoverM = useMutation({
    mutationFn: () =>
      pairsApi.discover({
        symbols: universe.split(",").map((s) => s.trim()).filter(Boolean),
        timeframe,
        days,
        topN,
      }),
  });

  const [selected, setSelected] = useState<PairCandidate | null>(null);
  const testM = useMutation({
    mutationFn: (c: PairCandidate) =>
      pairsApi.test({ symbolY: c.symbolY, symbolX: c.symbolX, timeframe, days }),
  });

  const onSelectPair = (c: PairCandidate) => {
    setSelected(c);
    testM.mutate(c);
  };

  return (
    <div className="space-y-8">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Stat-Arb / Cointegration
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Pairs Discovery{" "}
          <span className="text-muted-foreground/30 font-light">
            — Engle-Granger · Hurst · half-life · OU calibration
          </span>
        </p>
      </div>

      <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5 space-y-4">
        <p className="text-[10px] uppercase tracking-widest text-muted-foreground/60">Universe + Parameters</p>
        <textarea
          value={universe}
          onChange={(e) => setUniverse(e.target.value)}
          rows={2}
          className="w-full px-3 py-2 rounded bg-black/40 border border-white/10 text-xs font-mono"
          placeholder="comma-separated OKX swap symbols"
        />
        <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
          <Field label="Timeframe">
            <select
              value={timeframe}
              onChange={(e) => setTimeframe(e.target.value)}
              className="w-full px-2 py-1.5 rounded bg-black/40 border border-white/10 text-xs font-mono"
            >
              <option value="1H">1H</option>
              <option value="4H">4H</option>
              <option value="1D">1D</option>
            </select>
          </Field>
          <Field label="Days">
            <input
              type="number"
              value={days}
              onChange={(e) => setDays(Number(e.target.value))}
              className="w-full px-2 py-1.5 rounded bg-black/40 border border-white/10 text-xs font-mono"
            />
          </Field>
          <Field label="Top N">
            <input
              type="number"
              value={topN}
              onChange={(e) => setTopN(Number(e.target.value))}
              className="w-full px-2 py-1.5 rounded bg-black/40 border border-white/10 text-xs font-mono"
            />
          </Field>
          <div className="flex items-end">
            <button
              onClick={() => discoverM.mutate()}
              disabled={discoverM.isPending}
              className="w-full px-4 py-2 rounded bg-accent/15 text-accent border border-accent/40 hover:bg-accent/25 text-[11px] font-black uppercase tracking-wider disabled:opacity-50"
            >
              {discoverM.isPending ? "Scanning…" : "DISCOVER"}
            </button>
          </div>
        </div>

        {discoverM.isError && (
          <p className="text-xs text-rose-400">
            Discovery failed. Check that historical market data is available for these symbols.
          </p>
        )}
      </div>

      {discoverM.data && (
        <div className="space-y-4">
          <p className="text-xs text-muted-foreground">
            Tested universe of <strong>{discoverM.data.universeSize}</strong> symbols ·{" "}
            <strong>{discoverM.data.bars}</strong> bars · {discoverM.data.timeframe}
          </p>

          <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-white/5 text-[9px] uppercase tracking-[0.2em] text-muted-foreground/60">
                  <th className="text-left px-4 py-3 font-black">Y</th>
                  <th className="text-left px-3 py-3 font-black">X</th>
                  <th className="text-right px-3 py-3 font-black">Quality</th>
                  <th className="text-right px-3 py-3 font-black">β</th>
                  <th className="text-right px-3 py-3 font-black">Hurst</th>
                  <th className="text-right px-3 py-3 font-black">HL</th>
                  <th className="text-right px-3 py-3 font-black">ρ</th>
                  <th className="text-right px-3 py-3 font-black">ADF t</th>
                  <th className="text-left px-4 py-3 font-black"></th>
                </tr>
              </thead>
              <tbody>
                {discoverM.data.candidates.map((c, i) => (
                  <tr
                    key={i}
                    className={cn(
                      "border-b border-white/[0.02] cursor-pointer hover:bg-white/[0.03]",
                      selected?.symbolY === c.symbolY && selected?.symbolX === c.symbolX && "bg-accent/5"
                    )}
                    onClick={() => onSelectPair(c)}
                  >
                    <td className="px-4 py-2 font-bold">{c.symbolY}</td>
                    <td className="px-3 py-2 text-muted-foreground">{c.symbolX}</td>
                    <td className="px-3 py-2 text-right font-mono font-bold text-accent">{c.qualityScore.toFixed(0)}</td>
                    <td className="px-3 py-2 text-right font-mono">{Number(c.diagnostics.hedgeRatio).toFixed(3)}</td>
                    <td className="px-3 py-2 text-right font-mono">{c.diagnostics.hurstExponent.toFixed(2)}</td>
                    <td className="px-3 py-2 text-right font-mono">{c.diagnostics.halfLife.toFixed(1)}</td>
                    <td className="px-3 py-2 text-right font-mono">{c.diagnostics.correlation.toFixed(2)}</td>
                    <td className="px-3 py-2 text-right font-mono">{c.diagnostics.adfTStatistic.toFixed(2)}</td>
                    <td className="px-4 py-2 text-[10px] uppercase tracking-wider text-accent/80">View ▸</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {discoverM.data.candidates.length === 0 && (
              <p className="p-12 text-center text-sm text-muted-foreground">
                No candidates passed the cointegration gate.
              </p>
            )}
          </div>

          {selected && testM.data && (
            <SpreadChart pair={testM.data} />
          )}
          {selected && testM.isPending && (
            <div className="p-12 text-center text-sm text-muted-foreground">Computing spread…</div>
          )}
        </div>
      )}
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/60">{label}</span>
      <div className="mt-1">{children}</div>
    </label>
  );
}

function SpreadChart({ pair }: { pair: PairTestResponse }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <p className="text-[10px] uppercase tracking-widest text-muted-foreground/60 mb-1">
        Spread Z-Score · {pair.symbolY} / {pair.symbolX}
      </p>
      <p className="text-sm font-bold mb-4">
        β={Number(pair.diagnostics.hedgeRatio).toFixed(3)} · HL={pair.diagnostics.halfLife.toFixed(1)} bars · Hurst={pair.diagnostics.hurstExponent.toFixed(2)} · |ADF t|={Math.abs(pair.diagnostics.adfTStatistic).toFixed(2)}
      </p>
      <LineChart
        values={pair.zScore.map(Number)}
        width={900}
        height={260}
        stroke="rgba(30, 58, 138, 0.95)"
        fill="rgba(30, 58, 138, 0.06)"
        zeroLine
        yLabelFormatter={(v) => v.toFixed(1)}
      />
      <div className="mt-3 flex gap-4 text-[10px] text-muted-foreground/60 font-mono">
        <span>+2σ entry zone (short Y / long X)</span>
        <span>−2σ entry zone (long Y / short X)</span>
        <span>0 reversion target</span>
      </div>
    </div>
  );
}
