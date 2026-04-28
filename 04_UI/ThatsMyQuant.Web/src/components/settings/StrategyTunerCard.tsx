import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { settingsApi } from "@/api/settings";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";

/**
 * Live strategy parameter tuner. Reads current values via /api/settings (or
 * /api/settings/runtime overrides), allows the operator to nudge thresholds,
 * and PATCHes the runtime store. Changes apply on next config reload (which
 * happens automatically with reloadOnChange=true).
 *
 * Editable parameters mirror the WritableSettingsStore whitelist:
 *   • Pairs:  z-entry / z-exit / z-stop / min-correlation / hedge ratios / lookback
 *   • Funding: entry threshold (per Long/Short tier or global)
 *   • Toggles: PairsTrading, Funding, DirectionalScanner, FundingScout, PairsDiscovery
 *
 * Each row has a slider + numeric input + "Save" inline button. Nothing
 * persists until the user explicitly applies a row.
 */
export function StrategyTunerCard() {
  const qc = useQueryClient();

  const writableQ = useQuery({
    queryKey: ["settings", "writable"],
    queryFn:  settingsApi.writable,
    staleTime: 60_000,
  });
  const runtimeQ = useQuery({
    queryKey: ["settings", "runtime"],
    queryFn:  settingsApi.runtime,
    refetchInterval: 30_000,
  });

  const patchM = useMutation({
    mutationFn: settingsApi.patch,
    onSuccess: (res) => {
      const okKeys = Object.keys(res.accepted);
      const failed = Object.keys(res.rejected);
      if (okKeys.length > 0) {
        pushNotification({
          type: "success",
          title: `✓ Saved ${okKeys.length} setting${okKeys.length > 1 ? "s" : ""}`,
          body: okKeys.join(" · "),
        });
      }
      if (failed.length > 0) {
        pushNotification({
          type: "warning",
          title: "Some settings rejected",
          body: failed.map((k) => `${k}: ${res.rejected[k]}`).join(" · "),
        });
      }
      qc.invalidateQueries({ queryKey: ["settings"] });
    },
    onError: (err: unknown) => {
      pushNotification({
        type: "error",
        title: "Patch failed",
        body: err instanceof Error ? err.message : String(err),
      });
    },
  });

  // Strategy params we expose visually — synced from server on load
  const [pairs, setPairs] = useState<Partial<Record<string, number>>>({});
  const [pairsToggle, setPairsToggle] = useState(true);
  const [funding, setFunding] = useState<Partial<Record<string, number>>>({});
  const [fundingToggle, setFundingToggle] = useState(true);
  const [scoutToggle, setScoutToggle] = useState(true);
  const [discoveryToggle, setDiscoveryToggle] = useState(true);
  const [directionalToggle, setDirectionalToggle] = useState(false);

  // Hydrate from server-side overrides on first load
  useEffect(() => {
    if (!runtimeQ.data) return;
    const o = runtimeQ.data.overrides as Record<string, unknown>;
    setPairs({
      "Strategy:PairsTrading:ZScoreEntry":   numOrUndef(o["Strategy:PairsTrading:ZScoreEntry"]) ?? 2.0,
      "Strategy:PairsTrading:ZScoreExit":    numOrUndef(o["Strategy:PairsTrading:ZScoreExit"])  ?? 0.3,
      "Strategy:PairsTrading:ZScoreStop":    numOrUndef(o["Strategy:PairsTrading:ZScoreStop"])  ?? 3.0,
      "Strategy:PairsTrading:MinCorrelation":numOrUndef(o["Strategy:PairsTrading:MinCorrelation"]) ?? 0.30,
      "Strategy:PairsTrading:MaxHedgeRatio": numOrUndef(o["Strategy:PairsTrading:MaxHedgeRatio"])  ?? 15.0,
      "Strategy:PairsTrading:MinHedgeRatio": numOrUndef(o["Strategy:PairsTrading:MinHedgeRatio"])  ?? 0.10,
    });
    setFunding({
      "Funding:EntryThresholdRate":       numOrUndef(o["Funding:EntryThresholdRate"])       ?? 0.0001,
      "Funding:Long:EntryThresholdRate":  numOrUndef(o["Funding:Long:EntryThresholdRate"])  ?? 0.0001,
      "Funding:Short:EntryThresholdRate": numOrUndef(o["Funding:Short:EntryThresholdRate"]) ?? 0.0001,
    });
    setPairsToggle(boolOrDef(o["Strategy:PairsTrading:Enabled"], true));
    setFundingToggle(boolOrDef(o["Funding:Enabled"], true));
    setScoutToggle(boolOrDef(o["Funding:Scout:Enabled"], true));
    setDiscoveryToggle(boolOrDef(o["Pairs:Discovery:Enabled"], true));
    setDirectionalToggle(boolOrDef(o["Paper:DirectionalScannerEnabled"], false));
  }, [runtimeQ.data]);

  if (writableQ.isLoading) {
    return (
      <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
        <p className="text-[11px] italic font-serif text-muted-foreground/45">Loading writable schema…</p>
      </div>
    );
  }

  const allowed = new Set(writableQ.data?.keys.map((k) => k.key) ?? []);

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5 space-y-5">
      <div>
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-1">
          🎛 Strategy Tuner
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/70">
          Live-tune thresholds and toggles. Saves to runtime overrides; reload-on-change picks them up automatically — no service restart needed.
        </p>
      </div>

      {/* Pairs section */}
      <Section title="Pairs Trading" enabled={pairsToggle} onToggle={(v) => {
        setPairsToggle(v);
        patchM.mutate({ "Strategy:PairsTrading:Enabled": v });
      }} allowed={allowed.has("Strategy:PairsTrading:Enabled")}>
        <SliderRow label="Z-Entry"   keyName="Strategy:PairsTrading:ZScoreEntry" min={0.5} max={5.0} step={0.05} value={pairs["Strategy:PairsTrading:ZScoreEntry"]   ?? 2.0}  unit="σ" onSave={(v) => patchM.mutate({ "Strategy:PairsTrading:ZScoreEntry": v })}  onChange={(v) => setPairs({ ...pairs, "Strategy:PairsTrading:ZScoreEntry": v })} />
        <SliderRow label="Z-Exit"    keyName="Strategy:PairsTrading:ZScoreExit"  min={0.05} max={2.0} step={0.05} value={pairs["Strategy:PairsTrading:ZScoreExit"]    ?? 0.3} unit="σ" onSave={(v) => patchM.mutate({ "Strategy:PairsTrading:ZScoreExit": v })}   onChange={(v) => setPairs({ ...pairs, "Strategy:PairsTrading:ZScoreExit": v })} />
        <SliderRow label="Z-Stop"    keyName="Strategy:PairsTrading:ZScoreStop"  min={1.0} max={6.0} step={0.05} value={pairs["Strategy:PairsTrading:ZScoreStop"]    ?? 3.0} unit="σ" onSave={(v) => patchM.mutate({ "Strategy:PairsTrading:ZScoreStop": v })}   onChange={(v) => setPairs({ ...pairs, "Strategy:PairsTrading:ZScoreStop": v })} />
        <SliderRow label="Min ρ"      keyName="Strategy:PairsTrading:MinCorrelation" min={0.0} max={1.0} step={0.01} value={pairs["Strategy:PairsTrading:MinCorrelation"] ?? 0.30} unit=""  onSave={(v) => patchM.mutate({ "Strategy:PairsTrading:MinCorrelation": v })} onChange={(v) => setPairs({ ...pairs, "Strategy:PairsTrading:MinCorrelation": v })} />
        <SliderRow label="Max β"      keyName="Strategy:PairsTrading:MaxHedgeRatio"  min={1.0} max={30.0} step={0.5} value={pairs["Strategy:PairsTrading:MaxHedgeRatio"]  ?? 15.0} unit="" onSave={(v) => patchM.mutate({ "Strategy:PairsTrading:MaxHedgeRatio": v })}  onChange={(v) => setPairs({ ...pairs, "Strategy:PairsTrading:MaxHedgeRatio": v })} />
        <SliderRow label="Min β"      keyName="Strategy:PairsTrading:MinHedgeRatio"  min={0.01} max={1.0} step={0.01} value={pairs["Strategy:PairsTrading:MinHedgeRatio"]  ?? 0.10} unit="" onSave={(v) => patchM.mutate({ "Strategy:PairsTrading:MinHedgeRatio": v })}  onChange={(v) => setPairs({ ...pairs, "Strategy:PairsTrading:MinHedgeRatio": v })} />
      </Section>

      {/* Funding section */}
      <Section title="Funding Alpha" enabled={fundingToggle} onToggle={(v) => { setFundingToggle(v); patchM.mutate({ "Funding:Enabled": v }); }} allowed={allowed.has("Funding:Enabled")}>
        <SliderRow label="Threshold (global)" keyName="Funding:EntryThresholdRate" min={0.00001} max={0.01} step={0.00005} value={funding["Funding:EntryThresholdRate"] ?? 0.0001} unit=" rate" formatter={(v) => `${(v * 10000).toFixed(2)} bp`} onSave={(v) => patchM.mutate({ "Funding:EntryThresholdRate": v })} onChange={(v) => setFunding({ ...funding, "Funding:EntryThresholdRate": v })} />
        <SliderRow label="Threshold (Long)"   keyName="Funding:Long:EntryThresholdRate"  min={0.00001} max={0.01} step={0.00005} value={funding["Funding:Long:EntryThresholdRate"] ?? 0.0001} unit=" rate" formatter={(v) => `${(v * 10000).toFixed(2)} bp`} onSave={(v) => patchM.mutate({ "Funding:Long:EntryThresholdRate": v })} onChange={(v) => setFunding({ ...funding, "Funding:Long:EntryThresholdRate": v })} />
        <SliderRow label="Threshold (Short)"  keyName="Funding:Short:EntryThresholdRate" min={0.00001} max={0.01} step={0.00005} value={funding["Funding:Short:EntryThresholdRate"] ?? 0.0001} unit=" rate" formatter={(v) => `${(v * 10000).toFixed(2)} bp`} onSave={(v) => patchM.mutate({ "Funding:Short:EntryThresholdRate": v })} onChange={(v) => setFunding({ ...funding, "Funding:Short:EntryThresholdRate": v })} />
      </Section>

      {/* Toggles */}
      <Section title="Scout / Discovery / Directional" enabled={true} onToggle={undefined} allowed={true}>
        <ToggleRow label="Funding Scout"      keyName="Funding:Scout:Enabled"            value={scoutToggle} onChange={(v) => { setScoutToggle(v); patchM.mutate({ "Funding:Scout:Enabled": v }); }} />
        <ToggleRow label="Pairs Discovery"    keyName="Pairs:Discovery:Enabled"          value={discoveryToggle} onChange={(v) => { setDiscoveryToggle(v); patchM.mutate({ "Pairs:Discovery:Enabled": v }); }} />
        <ToggleRow label="Directional Scanner (Paper)" keyName="Paper:DirectionalScannerEnabled" value={directionalToggle} onChange={(v) => { setDirectionalToggle(v); patchM.mutate({ "Paper:DirectionalScannerEnabled": v }); }} dangerOn />
      </Section>

      <p className="text-[9px] font-mono italic text-muted-foreground/45 leading-relaxed">
        Saved overrides land in <code>{runtimeQ.data?.path}</code> · runtime store auto-reloads · no service restart needed.
        Override-of-record beats appsettings.json baseline.
      </p>
    </div>
  );
}

// ─── Row primitives ────────────────────────────────────────────────────────

function Section({
  title, enabled, onToggle, allowed, children,
}: {
  title: string;
  enabled: boolean;
  onToggle?: (v: boolean) => void;
  allowed: boolean;
  children: React.ReactNode;
}) {
  return (
    <div className={cn("rounded-xl border border-white/[0.05] bg-white/[0.015] p-3 space-y-2", !enabled && onToggle && "opacity-60")}>
      <div className="flex items-center justify-between mb-1.5">
        <p className="text-[10px] font-black uppercase tracking-[0.25em] text-accent/75">{title}</p>
        {onToggle && allowed && (
          <button
            onClick={() => onToggle(!enabled)}
            className={cn(
              "px-2.5 py-0.5 rounded-full text-[9px] font-mono font-black uppercase tracking-widest border",
              enabled
                ? "border-emerald-400/40 bg-emerald-500/15 text-emerald-300"
                : "border-rose-400/40 bg-rose-500/10 text-rose-300"
            )}
          >
            {enabled ? "● Enabled" : "○ Disabled"}
          </button>
        )}
      </div>
      <div className={!enabled && onToggle ? "pointer-events-none" : ""}>{children}</div>
    </div>
  );
}

function SliderRow({
  label, keyName, min, max, step, value, unit = "", formatter, onChange, onSave,
}: {
  label: string;
  keyName: string;
  min: number;
  max: number;
  step: number;
  value: number;
  unit?: string;
  formatter?: (v: number) => string;
  onChange: (v: number) => void;
  onSave: (v: number) => void;
}) {
  const display = formatter ? formatter(value) : `${value.toFixed(step < 0.01 ? 5 : step < 0.1 ? 3 : 2)}${unit}`;
  return (
    <div className="grid grid-cols-[120px_1fr_80px_60px] items-center gap-2">
      <span className="text-[10px] font-mono text-muted-foreground/70" title={keyName}>{label}</span>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={(e) => onChange(parseFloat(e.target.value))}
        className="accent-accent"
      />
      <span className="text-[11px] font-mono font-bold tabular-nums text-foreground/80 text-right">{display}</span>
      <button
        onClick={() => onSave(value)}
        className="px-2 py-0.5 rounded text-[9px] font-mono uppercase tracking-widest border border-accent/30 bg-accent/10 text-accent hover:bg-accent/20"
      >
        Save
      </button>
    </div>
  );
}

function ToggleRow({
  label, keyName, value, onChange, dangerOn,
}: {
  label: string;
  keyName: string;
  value: boolean;
  onChange: (v: boolean) => void;
  dangerOn?: boolean;
}) {
  return (
    <div className="flex items-center justify-between">
      <span className="text-[10px] font-mono text-muted-foreground/70" title={keyName}>{label}</span>
      <button
        onClick={() => onChange(!value)}
        className={cn(
          "px-2 py-0.5 rounded-full text-[9px] font-mono font-black uppercase tracking-widest border",
          value
            ? dangerOn
              ? "border-amber-400/40 bg-amber-500/15 text-amber-300"
              : "border-emerald-400/40 bg-emerald-500/15 text-emerald-300"
            : "border-white/10 bg-white/[0.03] text-muted-foreground/55"
        )}
      >
        {value ? "● ON" : "○ OFF"}
      </button>
    </div>
  );
}

function numOrUndef(v: unknown): number | undefined {
  if (typeof v === "number") return v;
  if (typeof v === "string" && v.length > 0) {
    const n = parseFloat(v);
    return Number.isFinite(n) ? n : undefined;
  }
  return undefined;
}

function boolOrDef(v: unknown, def: boolean): boolean {
  if (typeof v === "boolean") return v;
  if (typeof v === "string") return v === "true" || v === "1";
  return def;
}
