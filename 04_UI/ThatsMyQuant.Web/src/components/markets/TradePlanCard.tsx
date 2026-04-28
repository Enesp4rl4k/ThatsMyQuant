import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { marketsApi } from "@/api/markets";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";

interface Props {
  symbol: string;
}

type Side = "long" | "short";

const PLANS_KEY_PREFIX = "tmq.tradePlans.";

interface SavedPlan {
  name: string;
  side: Side;
  entry: number;
  stop: number;
  tp: number;
  riskPct: number;
  leverage: number;
  savedAt: string;
}

/**
 * Compact "trade plan" card for Trade Mode. Combines:
 *   • Inline order-entry inputs (entry / SL / TP / risk% / leverage)
 *   • Real-time profit-scenario summary ($risk / $reward / R:R / qty / margin)
 *   • Submit button → POST /api/markets/orders (paper-aware)
 *   • Save / Load named plans (localStorage, scoped per symbol)
 *   • Listens for `tmq:prefill-order` events (Bot Discoveries, Order Book click)
 *
 * Lives on the right-side stack of TradeModeView; replaces the need to switch
 * to the regular Markets page just to size a trade.
 */
export function TradePlanCard({ symbol }: Props) {
  const qc = useQueryClient();

  // Equity for risk-based sizing
  const portfolioQ = useQuery({
    queryKey: ["portfolio"],
    queryFn:  () => dashboardApi.portfolio(),
    refetchInterval: 5_000,
  });
  const equity = portfolioQ.data?.totalEquity ?? 0;

  // Last price for placeholder
  const symbolsQ = useQuery({
    queryKey: ["markets", "symbols"],
    queryFn:  () => marketsApi.symbols(),
    refetchInterval: 15_000,
  });
  const lastPrice = symbolsQ.data?.find((s) => s.symbol === symbol)?.lastPrice ?? 0;

  const [side, setSide]   = useState<Side>("long");
  const [riskPct, setRiskPct] = useState("0.5");
  const [lev, setLev]     = useState("3");
  const [entry, setEntry] = useState("");
  const [stop, setStop]   = useState("");
  const [tp, setTp]       = useState("");
  const [planName, setPlanName] = useState("");
  const [saved, setSaved] = useState<SavedPlan[]>([]);

  // Reset on symbol change
  useEffect(() => {
    setEntry(""); setStop(""); setTp(""); setPlanName("");
    setSaved(loadPlans(symbol));
  }, [symbol]);

  // Pre-fill listener
  useEffect(() => {
    const handler = (e: Event) => {
      const d = (e as CustomEvent<{ symbol?: string; side?: Side; entry?: number; stop?: number; tp?: number; riskPct?: number }>).detail;
      if (!d) return;
      if (d.symbol && d.symbol.toUpperCase() !== symbol.toUpperCase()) return;
      if (d.side) setSide(d.side);
      if (d.entry && d.entry > 0) setEntry(d.entry.toString());
      if (d.stop  && d.stop  > 0) setStop(d.stop.toString());
      if (d.tp    && d.tp    > 0) setTp(d.tp.toString());
      if (d.riskPct && d.riskPct > 0) setRiskPct(String(d.riskPct));
    };
    document.addEventListener("tmq:prefill-order", handler);
    return () => document.removeEventListener("tmq:prefill-order", handler);
  }, [symbol]);

  // Scenario calculator
  const calc = useMemo(() => {
    const e = parseFloat(entry);
    const s = parseFloat(stop);
    const t = parseFloat(tp);
    const rp = Math.max(0, parseFloat(riskPct) || 0);
    const lv = Math.max(1, parseFloat(lev) || 1);
    const validE = Number.isFinite(e) && e > 0;
    const validS = Number.isFinite(s) && s > 0;
    const validT = Number.isFinite(t) && t > 0;
    if (!validE || equity <= 0) return null;

    const isLong   = side === "long";
    const stopOk   = validS && (isLong ? s < e : s > e);
    const tpOk     = validT && (isLong ? t > e : t < e);
    const stopDist = validS ? Math.abs(e - s) : 0;
    const tpDist   = validT ? Math.abs(t - e) : 0;
    const dollarRisk = (equity * rp) / 100;
    const qty = stopOk && stopDist > 0 ? dollarRisk / stopDist : 0;
    const notional = qty * e;
    const margin = lv > 0 ? notional / lv : 0;
    const dollarReward = qty * tpDist;
    const rr = stopDist > 0 && tpDist > 0 ? tpDist / stopDist : 0;
    return {
      isLong, validE, validS, validT, stopOk, tpOk, stopDist, tpDist,
      dollarRisk, qty, notional, margin, dollarReward, rr,
      stopDistPct: validS ? (stopDist / e) * 100 : 0,
      tpDistPct:   validT ? (tpDist   / e) * 100 : 0,
      marginPct:   equity > 0 ? (margin / equity) * 100 : 0,
    };
  }, [entry, stop, tp, riskPct, lev, equity, side]);

  // Submit
  const submitM = useMutation({
    mutationFn: marketsApi.submitOrder,
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ["signals"] });
      qc.invalidateQueries({ queryKey: ["positions"] });
      qc.invalidateQueries({ queryKey: ["trades"] });
      pushNotification({
        type: res.accepted ? "success" : "warning",
        title: res.accepted ? `📋 Order accepted · ${symbol}` : `🚫 Rejected · ${symbol}`,
        body:  res.accepted ? `Signal ${res.signalId.slice(0, 8)}…` : (res.reasoning || res.status),
      });
    },
    onError: (err: unknown) => pushNotification({
      type: "error",
      title: `❌ Submit failed · ${symbol}`,
      body: err instanceof Error ? err.message : String(err),
    }),
  });

  const submit = () => {
    if (!calc || !calc.stopOk || calc.qty <= 0) return;
    submitM.mutate({
      symbol,
      side,
      orderType: "limit",
      quantity: Number(calc.qty.toFixed(6)),
      entry: parseFloat(entry),
      stopLoss:   calc.validS ? parseFloat(stop) : undefined,
      takeProfit: calc.validT ? parseFloat(tp)   : undefined,
      leverage: parseFloat(lev) || undefined,
      riskPct:  parseFloat(riskPct) || 0,
    });
  };

  const savePlan = () => {
    const name = planName.trim() || `Plan-${saved.length + 1}`;
    const e = parseFloat(entry), s = parseFloat(stop), t = parseFloat(tp);
    if (!Number.isFinite(e) || e <= 0) {
      pushNotification({ type: "warning", title: "Cannot save", body: "Entry must be set." });
      return;
    }
    const next = [
      ...saved.filter((p) => p.name !== name),
      { name, side, entry: e, stop: s, tp: t, riskPct: parseFloat(riskPct), leverage: parseFloat(lev), savedAt: new Date().toISOString() },
    ];
    setSaved(next);
    storePlans(symbol, next);
    setPlanName("");
    pushNotification({ type: "success", title: `💾 Plan "${name}" saved`, body: `${next.length} plan(s) for ${symbol}` });
  };

  const loadPlan = (p: SavedPlan) => {
    setSide(p.side);
    setEntry(String(p.entry));
    setStop(p.stop ? String(p.stop) : "");
    setTp(p.tp ? String(p.tp) : "");
    setRiskPct(String(p.riskPct));
    setLev(String(p.leverage));
    pushNotification({ type: "info", title: `📋 Loaded plan "${p.name}"`, body: `${symbol} ${p.side.toUpperCase()} @ ${p.entry}` });
  };

  const deletePlan = (name: string) => {
    const next = saved.filter((p) => p.name !== name);
    setSaved(next);
    storePlans(symbol, next);
  };

  const canSubmit = !!calc && calc.stopOk && calc.qty > 0 && !submitM.isPending;

  return (
    <div className="rounded-xl border border-white/[0.05] bg-[#0A0A0A]/80 backdrop-blur-xl overflow-hidden">
      <div className="px-3 py-2 border-b border-white/[0.05] flex items-baseline justify-between">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">📋 Trade Plan</p>
        <span className="text-[9px] font-mono text-muted-foreground/45">{symbol} · eq {formatUsd(equity)}</span>
      </div>

      <div className="p-3 space-y-2">
        {/* Side */}
        <div className="grid grid-cols-2 gap-1">
          <button
            onClick={() => setSide("long")}
            className={cn(
              "py-1 rounded text-[9px] font-black uppercase tracking-widest border",
              side === "long" ? "bg-emerald-500/15 text-emerald-300 border-emerald-400/40" : "border-white/10 text-muted-foreground/65"
            )}
          >↑ Long</button>
          <button
            onClick={() => setSide("short")}
            className={cn(
              "py-1 rounded text-[9px] font-black uppercase tracking-widest border",
              side === "short" ? "bg-rose-500/15 text-rose-300 border-rose-400/40" : "border-white/10 text-muted-foreground/65"
            )}
          >↓ Short</button>
        </div>

        {/* Entry */}
        <Field label="Entry">
          <input type="number" step="any" value={entry} placeholder={lastPrice ? lastPrice.toString() : "0.00"} onChange={(e) => setEntry(e.target.value)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[10px] font-mono" />
        </Field>

        {/* Stop + TP */}
        <div className="grid grid-cols-2 gap-1.5">
          <Field label="Stop"><input type="number" step="any" value={stop} onChange={(e) => setStop(e.target.value)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[10px] font-mono" /></Field>
          <Field label="TP"><input type="number" step="any" value={tp} onChange={(e) => setTp(e.target.value)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[10px] font-mono" /></Field>
        </div>

        {/* Risk % + Leverage */}
        <div className="grid grid-cols-2 gap-1.5">
          <Field label="Risk %"><input type="number" step="0.1" min="0" max="10" value={riskPct} onChange={(e) => setRiskPct(e.target.value)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[10px] font-mono" /></Field>
          <Field label="Lev"><input type="number" step="0.5" min="1" max="20" value={lev} onChange={(e) => setLev(e.target.value)} className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[10px] font-mono" /></Field>
        </div>

        {/* Scenario summary */}
        {calc ? (
          <div className="rounded-md border border-white/[0.05] bg-white/[0.015] p-2 grid grid-cols-3 gap-1.5 text-[9px] font-mono">
            <Stat label="$Risk"     value={formatUsd(calc.dollarRisk)} tone="rose" />
            <Stat label="$Reward"   value={calc.dollarReward > 0 ? formatUsd(calc.dollarReward) : "—"} tone="emerald" />
            <Stat label="R:R"       value={calc.rr > 0 ? calc.rr.toFixed(2) : "—"} tone={calc.rr >= 2 ? "emerald" : calc.rr >= 1 ? "amber" : "rose"} />
            <Stat label="Qty"       value={calc.qty > 0 ? calc.qty.toFixed(4) : "—"} tone="default" />
            <Stat label="Notional"  value={calc.notional > 0 ? formatUsd(calc.notional) : "—"} tone="default" />
            <Stat label="Margin %"  value={calc.margin > 0 ? `${calc.marginPct.toFixed(1)}%` : "—"} tone={calc.marginPct < 30 ? "default" : calc.marginPct < 60 ? "amber" : "rose"} />
            {calc.validS && !calc.stopOk && (
              <p className="col-span-3 text-[9px] italic text-rose-300 mt-1">Stop on the wrong side of entry for {side}.</p>
            )}
            {calc.validT && !calc.tpOk && (
              <p className="col-span-3 text-[9px] italic text-amber-300 mt-1">TP on the wrong side of entry for {side}.</p>
            )}
          </div>
        ) : (
          <p className="text-[10px] italic text-muted-foreground/40 text-center py-2">enter values to compute scenario</p>
        )}

        {/* Submit */}
        <button
          onClick={submit}
          disabled={!canSubmit}
          className={cn(
            "w-full py-1.5 rounded text-[10px] font-black uppercase tracking-widest border transition-colors",
            canSubmit
              ? side === "long"
                ? "bg-emerald-500/15 text-emerald-400 border-emerald-500/40 hover:bg-emerald-500/25"
                : "bg-rose-500/15 text-rose-400 border-rose-500/40 hover:bg-rose-500/25"
              : "bg-white/[0.02] text-muted-foreground/40 border-white/5 cursor-not-allowed"
          )}
        >
          {submitM.isPending ? "Submitting…" : `📋 Place ${side}`}
        </button>

        {/* Save plan + recall */}
        <div className="pt-2 border-t border-white/[0.04] space-y-1.5">
          <div className="flex gap-1">
            <input
              value={planName}
              onChange={(e) => setPlanName(e.target.value)}
              placeholder="Plan name…"
              className="flex-1 bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[10px] font-mono"
            />
            <button
              onClick={savePlan}
              className="px-2 py-1 rounded text-[9px] font-mono uppercase border border-cyan-400/30 bg-cyan-500/10 text-cyan-300 hover:bg-cyan-500/20"
            >
              💾 Save
            </button>
          </div>
          {saved.length > 0 && (
            <div className="space-y-0.5 max-h-24 overflow-y-auto">
              {saved.map((p) => (
                <div key={p.name} className="flex items-center gap-1 text-[9px] font-mono group">
                  <button
                    onClick={() => loadPlan(p)}
                    className="flex-1 px-2 py-0.5 rounded text-left hover:bg-white/[0.04] truncate"
                    title={`${p.side.toUpperCase()} @ ${p.entry} · SL ${p.stop || "–"} · TP ${p.tp || "–"}`}
                  >
                    <span className={cn("font-bold", p.side === "long" ? "text-emerald-300" : "text-rose-300")}>
                      {p.side === "long" ? "↑" : "↓"}
                    </span>{" "}
                    <span className="text-foreground/80">{p.name}</span>
                    <span className="text-muted-foreground/50 ml-1">@{p.entry}</span>
                  </button>
                  <button
                    onClick={() => deletePlan(p.name)}
                    className="px-1 text-muted-foreground/45 hover:text-rose-300 opacity-0 group-hover:opacity-100"
                    title="Delete"
                  >
                    ✕
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// ─── Helpers ───────────────────────────────────────────────────────────────

function loadPlans(symbol: string): SavedPlan[] {
  try {
    const raw = localStorage.getItem(PLANS_KEY_PREFIX + symbol.toUpperCase());
    return raw ? JSON.parse(raw) as SavedPlan[] : [];
  } catch { return []; }
}
function storePlans(symbol: string, plans: SavedPlan[]) {
  try { localStorage.setItem(PLANS_KEY_PREFIX + symbol.toUpperCase(), JSON.stringify(plans)); }
  catch { /* ignore */ }
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[8px] uppercase tracking-widest text-muted-foreground/45 mb-0.5 block">{label}</span>
      {children}
    </label>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone: "rose" | "emerald" | "amber" | "default" }) {
  const cls =
    tone === "rose"    ? "text-rose-300" :
    tone === "emerald" ? "text-emerald-300" :
    tone === "amber"   ? "text-amber-300" :
                         "text-foreground/85";
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/40 mb-0.5">{label}</p>
      <p className={cn("font-bold tabular-nums", cls)}>{value}</p>
    </div>
  );
}
