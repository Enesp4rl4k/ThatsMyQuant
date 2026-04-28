import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { marketsApi } from "@/api/markets";
import { cn } from "@/lib/utils";
import { formatUsd } from "@/lib/format";
import { pushNotification } from "@/lib/notifications";

interface Props {
  symbol: string;
}

type Side = "long" | "short";
type OrderType = "market" | "limit" | "stop";

/**
 * Position-sizing & risk calculator. Computes:
 *  - quantity from $-risk × stop-distance
 *  - notional / margin used (given leverage)
 *  - $-reward at TP, R:R ratio
 *  - "submit" pushes a paper-mode notification (real send would require a
 *    paper signal endpoint; left as a hook for that integration)
 */
export function OrderEntryPanel({ symbol }: Props) {
  const portfolioQ = useQuery({
    queryKey: ["portfolio"],
    queryFn: () => dashboardApi.portfolio(),
    refetchInterval: 5_000,
  });

  const symbolsQ = useQuery({
    queryKey: ["markets", "symbols"],
    queryFn: () => marketsApi.symbols(),
    refetchInterval: 15_000,
  });

  const equity = portfolioQ.data?.totalEquity ?? 0;
  const lastPrice = symbolsQ.data?.find((s) => s.symbol === symbol)?.lastPrice ?? 0;

  const [side, setSide] = useState<Side>("long");
  const [orderType, setOrderType] = useState<OrderType>("limit");
  const [riskPctText, setRiskPctText] = useState("0.5");
  const [leverageText, setLeverageText] = useState("3");
  const [entryText, setEntryText] = useState("");
  const [stopText, setStopText] = useState("");
  const [tpText, setTpText] = useState("");

  // When symbol or last price changes, reset entry to last price (only if blank)
  useEffect(() => {
    if (lastPrice > 0 && !entryText) {
      setEntryText(lastPrice.toFixed(lastPrice >= 1 ? 2 : 5));
    }
  }, [lastPrice, entryText]);

  useEffect(() => {
    setEntryText("");
    setStopText("");
    setTpText("");
  }, [symbol]);

  // Listen for "prefill-order" events fired by BotDiscoveriesPanel etc.
  // When a discovery row is cherry-picked, pre-populate side/entry/SL/TP so
  // the operator can click Submit immediately without re-typing.
  useEffect(() => {
    function onPrefill(e: Event) {
      const detail = (e as CustomEvent<{
        symbol?: string;
        side?: "long" | "short";
        entry?: number;
        stop?: number;
        tp?: number;
        riskPct?: number;
      }>).detail;
      if (!detail) return;
      // Only fill if it's our symbol (parent decides whether to switch first)
      if (detail.symbol && detail.symbol.toUpperCase() !== symbol.toUpperCase()) return;
      if (detail.side) setSide(detail.side);
      if (detail.entry && detail.entry > 0) setEntryText(detail.entry.toString());
      if (detail.stop  && detail.stop  > 0) setStopText(detail.stop.toString());
      if (detail.tp    && detail.tp    > 0) setTpText(detail.tp.toString());
      if (detail.riskPct && detail.riskPct > 0) setRiskPctText(String(detail.riskPct));
      pushNotification({
        type: "info",
        title: `🎯 Pre-filled · ${symbol}`,
        body: `Discovery suggestion loaded. Review fields then click Place ${detail.side ?? "..."}.`,
      });
    }
    document.addEventListener("tmq:prefill-order", onPrefill);
    return () => document.removeEventListener("tmq:prefill-order", onPrefill);
  }, [symbol]);

  const calc = useMemo(() => {
    const entry = parseFloat(entryText);
    const stop = parseFloat(stopText);
    const tp = parseFloat(tpText);
    const riskPct = Math.max(0, parseFloat(riskPctText) || 0);
    const leverage = Math.max(1, parseFloat(leverageText) || 1);

    const isLong = side === "long";
    const validEntry = Number.isFinite(entry) && entry > 0;
    const validStop = Number.isFinite(stop) && stop > 0;
    const validTp = Number.isFinite(tp) && tp > 0;

    if (!validEntry || equity <= 0) return null;

    const dollarRisk = (equity * riskPct) / 100;
    const stopDist = validStop ? Math.abs(entry - stop) : 0;
    const stopDistPct = validStop ? (stopDist / entry) * 100 : 0;
    const stopDirOk = validStop && (isLong ? stop < entry : stop > entry);

    let quantity = 0;
    let notional = 0;
    if (validStop && stopDist > 0 && stopDirOk) {
      quantity = dollarRisk / stopDist;
      notional = quantity * entry;
    }

    const margin = leverage > 0 ? notional / leverage : 0;
    const marginPct = equity > 0 ? (margin / equity) * 100 : 0;

    const tpDist = validTp ? Math.abs(tp - entry) : 0;
    const dollarReward = quantity * tpDist;
    const tpDirOk = validTp && (isLong ? tp > entry : tp < entry);
    const rr = stopDist > 0 && tpDist > 0 ? tpDist / stopDist : 0;

    return {
      entry,
      stop,
      tp,
      isLong,
      validStop,
      validTp,
      stopDirOk,
      tpDirOk,
      dollarRisk,
      stopDist,
      stopDistPct,
      quantity,
      notional,
      margin,
      marginPct,
      dollarReward,
      rr,
    };
  }, [entryText, stopText, tpText, riskPctText, leverageText, equity, side]);

  const qc = useQueryClient();
  const submitM = useMutation({
    mutationFn: marketsApi.submitOrder,
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ["signals"] });
      qc.invalidateQueries({ queryKey: ["positions"] });
      qc.invalidateQueries({ queryKey: ["trades"] });
      if (res.accepted) {
        pushNotification({
          type: "success",
          title: `📋 Order accepted · ${symbol} ${side.toUpperCase()}`,
          body: `Signal ${res.signalId.slice(0, 8)}… · status: ${res.status}`,
        });
      } else {
        pushNotification({
          type: "warning",
          title: `🚫 Order rejected · ${symbol}`,
          body: res.reasoning || res.status,
        });
      }
    },
    onError: (err: unknown) => {
      pushNotification({
        type: "error",
        title: `❌ Order submit failed · ${symbol}`,
        body: err instanceof Error ? err.message : String(err),
      });
    },
  });

  const submit = () => {
    if (!calc || !calc.validStop || !calc.stopDirOk || calc.quantity <= 0) return;
    submitM.mutate({
      symbol,
      side,
      orderType,
      quantity: Number(calc.quantity.toFixed(6)),
      entry: calc.entry,
      stopLoss: calc.validStop ? calc.stop : undefined,
      takeProfit: calc.validTp ? calc.tp : undefined,
      leverage: parseFloat(leverageText) || undefined,
      riskPct: parseFloat(riskPctText) || 0,
    });
  };

  const canSubmit = !!calc && calc.validStop && calc.stopDirOk && calc.quantity > 0 && !submitM.isPending;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Order Entry
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/60">
            position-sizing &amp; risk calculator · {symbol}
          </p>
        </div>
        <span className="text-[9px] uppercase tracking-widest text-amber-400/70 px-1.5 py-0.5 rounded bg-amber-500/10 border border-amber-500/20">
          PAPER
        </span>
      </div>

      <div className="p-4 space-y-3">
        {/* Side toggle */}
        <div className="grid grid-cols-2 gap-1.5">
          <button
            onClick={() => setSide("long")}
            aria-pressed={side === "long"}
            className={cn(
              "py-1.5 rounded text-[10px] font-black uppercase tracking-widest border transition-colors",
              side === "long"
                ? "bg-emerald-500/15 text-emerald-400 border-emerald-500/40"
                : "border-white/10 text-muted-foreground hover:bg-white/[0.03]"
            )}
          >
            ↑ Long
          </button>
          <button
            onClick={() => setSide("short")}
            aria-pressed={side === "short"}
            className={cn(
              "py-1.5 rounded text-[10px] font-black uppercase tracking-widest border transition-colors",
              side === "short"
                ? "bg-rose-500/15 text-rose-400 border-rose-500/40"
                : "border-white/10 text-muted-foreground hover:bg-white/[0.03]"
            )}
          >
            ↓ Short
          </button>
        </div>

        {/* Order type */}
        <div className="flex items-center gap-1">
          {(["market", "limit", "stop"] as OrderType[]).map((t) => (
            <button
              key={t}
              onClick={() => setOrderType(t)}
              aria-pressed={orderType === t}
              className={cn(
                "flex-1 py-1 rounded text-[9px] font-mono uppercase tracking-widest border transition-colors",
                orderType === t
                  ? "bg-accent/15 text-accent border-accent/30"
                  : "border-white/10 text-muted-foreground/60 hover:text-foreground"
              )}
            >
              {t}
            </button>
          ))}
        </div>

        {/* Inputs */}
        <Field label="Entry">
          <input
            type="number"
            step="any"
            value={entryText}
            onChange={(e) => setEntryText(e.target.value)}
            placeholder={lastPrice ? lastPrice.toString() : "0.00"}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
        </Field>

        <div className="grid grid-cols-2 gap-2">
          <Field label="Stop loss">
            <input
              type="number"
              step="any"
              value={stopText}
              onChange={(e) => setStopText(e.target.value)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
          <Field label="Take profit">
            <input
              type="number"
              step="any"
              value={tpText}
              onChange={(e) => setTpText(e.target.value)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
        </div>

        <div className="grid grid-cols-2 gap-2">
          <Field label="Risk %">
            <input
              type="number"
              step="0.1"
              min="0"
              max="10"
              value={riskPctText}
              onChange={(e) => setRiskPctText(e.target.value)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
          <Field label="Leverage">
            <input
              type="number"
              step="0.5"
              min="1"
              max="20"
              value={leverageText}
              onChange={(e) => setLeverageText(e.target.value)}
              className="w-full bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
            />
          </Field>
        </div>

        {/* Calculated metrics */}
        <div className="pt-3 border-t border-white/5">
          {!calc ? (
            <p className="text-[10px] italic font-serif text-muted-foreground/50">
              — enter values to compute size —
            </p>
          ) : (
            <div className="grid grid-cols-2 gap-2 text-[11px] font-mono">
              <Stat label="$ Risk" value={formatUsd(calc.dollarRisk)} />
              <Stat
                label="Stop Δ"
                value={
                  calc.validStop
                    ? `${calc.stopDistPct.toFixed(2)}%`
                    : "—"
                }
                tone={calc.validStop && !calc.stopDirOk ? "neg" : "neutral"}
              />
              <Stat
                label="Quantity"
                value={calc.quantity > 0 ? calc.quantity.toFixed(4) : "—"}
              />
              <Stat
                label="Notional"
                value={calc.notional > 0 ? formatUsd(calc.notional) : "—"}
              />
              <Stat
                label="Margin"
                value={calc.margin > 0 ? `${formatUsd(calc.margin)} (${calc.marginPct.toFixed(1)}%)` : "—"}
              />
              <Stat
                label="R:R"
                value={calc.rr > 0 ? `${calc.rr.toFixed(2)}` : "—"}
                tone={calc.rr >= 2 ? "pos" : calc.rr >= 1 ? "neutral" : "neg"}
              />
              {calc.dollarReward > 0 && (
                <Stat
                  label="$ Reward"
                  value={formatUsd(calc.dollarReward)}
                  tone="pos"
                />
              )}
            </div>
          )}

          {calc && calc.validStop && !calc.stopDirOk && (
            <p className="mt-2 text-[10px] italic font-serif text-rose-300">
              Stop is on the wrong side of entry for a {side} position.
            </p>
          )}
          {calc && calc.validTp && !calc.tpDirOk && (
            <p className="mt-1 text-[10px] italic font-serif text-amber-300">
              TP is on the wrong side of entry for a {side} position.
            </p>
          )}
        </div>

        {/* Submit */}
        <button
          onClick={submit}
          disabled={!canSubmit}
          className={cn(
            "w-full py-2 rounded text-[11px] font-black uppercase tracking-widest border transition-colors",
            canSubmit
              ? side === "long"
                ? "bg-emerald-500/15 text-emerald-400 border-emerald-500/40 hover:bg-emerald-500/25"
                : "bg-rose-500/15 text-rose-400 border-rose-500/40 hover:bg-rose-500/25"
              : "bg-white/[0.02] text-muted-foreground/40 border-white/5 cursor-not-allowed"
          )}
        >
          {submitM.isPending ? "Submitting…" : orderType === "market" ? "Place market" : `Place ${orderType}`} · {side}
        </button>

        <p className="text-[9px] font-mono italic text-muted-foreground/40 text-center">
          submits via /api/markets/orders → ProcessSignalCommand
        </p>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/50 mb-0.5 block">
        {label}
      </span>
      {children}
    </label>
  );
}

function Stat({
  label,
  value,
  tone = "neutral",
}: {
  label: string;
  value: string;
  tone?: "pos" | "neg" | "neutral";
}) {
  return (
    <div>
      <p className="text-[8px] uppercase tracking-widest text-muted-foreground/50 mb-0.5">{label}</p>
      <p
        className={cn(
          "tabular-nums font-bold",
          tone === "pos" && "text-emerald-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}
