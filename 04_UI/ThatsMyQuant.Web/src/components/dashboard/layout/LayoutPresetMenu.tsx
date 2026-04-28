import { useEffect, useRef, useState } from "react";
import type { DashboardTab } from "./DashboardTabs";
import { cn } from "@/lib/utils";

export interface LayoutPreset {
  id: string;
  name: string;
  description: string;
  defaultTab: DashboardTab;
  /** Display priority for tabs — first listed is the most-emphasised. */
  tabOrder: DashboardTab[];
}

export const PRESETS: LayoutPreset[] = [
  {
    id: "trader",
    name: "Trader",
    description: "Trading-first · live book + signals on top",
    defaultTab: "trading",
    tabOrder: ["trading", "performance", "risk", "ops"],
  },
  {
    id: "risk-officer",
    name: "Risk Officer",
    description: "Risk-first · VaR / drawdown / stress always visible",
    defaultTab: "risk",
    tabOrder: ["risk", "ops", "trading", "performance"],
  },
  {
    id: "pm",
    name: "PM",
    description: "Portfolio Manager · performance + strategy rotation",
    defaultTab: "performance",
    tabOrder: ["performance", "risk", "trading", "ops"],
  },
  {
    id: "operator",
    name: "Operator",
    description: "Ops-first · service health, queues, latency",
    defaultTab: "ops",
    tabOrder: ["ops", "trading", "risk", "performance"],
  },
];

interface Props {
  current: string;
  onApply: (preset: LayoutPreset) => void;
}

export function LayoutPresetMenu({ current, onApply }: Props) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, [open]);

  const active = PRESETS.find((p) => p.id === current) ?? PRESETS[0];

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((v) => !v)}
        className="flex items-center gap-2 px-2.5 py-1 rounded-md text-[10px] font-mono text-muted-foreground/70 border border-white/10 bg-white/[0.02] hover:bg-white/[0.05] hover:text-foreground transition-colors"
        title="Choose dashboard role preset"
      >
        <svg width="11" height="11" viewBox="0 0 16 16" fill="none">
          <path
            d="M2 4h12M2 8h12M2 12h7"
            stroke="currentColor"
            strokeWidth="1.4"
            strokeLinecap="round"
          />
        </svg>
        <span>Layout · {active.name}</span>
      </button>

      {open && (
        <div className="absolute right-0 top-9 w-72 rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-paper-lg overflow-hidden z-50">
          <div className="px-3 py-2 border-b border-white/5">
            <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/60">
              Role Presets
            </p>
            <p className="text-[10px] font-serif italic text-muted-foreground/50 mt-0.5">
              tab order tuned to a daily workflow
            </p>
          </div>
          <ul>
            {PRESETS.map((p) => (
              <li key={p.id}>
                <button
                  onClick={() => {
                    onApply(p);
                    setOpen(false);
                  }}
                  className={cn(
                    "w-full px-3 py-2.5 text-left flex items-start gap-2 hover:bg-white/[0.03] transition-colors border-b border-white/[0.04] last:border-0",
                    current === p.id && "bg-accent/[0.08]"
                  )}
                >
                  <span
                    className={cn(
                      "mt-1 w-1.5 h-1.5 rounded-full shrink-0",
                      current === p.id ? "bg-accent" : "bg-muted-foreground/30"
                    )}
                  />
                  <div className="min-w-0 flex-1">
                    <p className="font-bold text-[12px]">{p.name}</p>
                    <p className="text-[10px] font-serif italic text-muted-foreground/60 leading-snug">
                      {p.description}
                    </p>
                    <p className="text-[9px] font-mono text-muted-foreground/40 mt-1">
                      {p.tabOrder.join(" · ")}
                    </p>
                  </div>
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
