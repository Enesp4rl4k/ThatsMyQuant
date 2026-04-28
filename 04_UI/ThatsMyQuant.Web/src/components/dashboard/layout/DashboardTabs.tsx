import { cn } from "@/lib/utils";

export type DashboardTab = "trading" | "risk" | "performance" | "ops";

const TABS: { id: DashboardTab; label: string; eyebrow: string; tone: string }[] = [
  { id: "trading", label: "Trading", eyebrow: "live book", tone: "text-accent" },
  { id: "risk", label: "Risk", eyebrow: "exposure & limits", tone: "text-rose-400" },
  { id: "performance", label: "Performance", eyebrow: "edges & periods", tone: "text-emerald-400" },
  { id: "ops", label: "Operations", eyebrow: "system health", tone: "text-amber-400" },
];

interface Props {
  active: DashboardTab;
  onChange: (tab: DashboardTab) => void;
}

export function DashboardTabs({ active, onChange }: Props) {
  return (
    <div className="border-b border-white/5 flex items-center gap-0 overflow-x-auto" role="tablist">
      {TABS.map((t) => (
        <button
          key={t.id}
          role="tab"
          aria-selected={active === t.id}
          onClick={() => onChange(t.id)}
          className={cn(
            "relative px-5 py-3 flex flex-col items-start gap-0 transition-colors min-w-[120px]",
            active === t.id
              ? "text-foreground"
              : "text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.02]"
          )}
        >
          <span
            className={cn(
              "text-[8px] font-black uppercase tracking-[0.3em]",
              active === t.id ? t.tone : "text-muted-foreground/40"
            )}
          >
            {t.eyebrow}
          </span>
          <span className="font-serif text-[18px] tracking-tight leading-none mt-1">
            {t.label}
          </span>
          {active === t.id && (
            <span
              className={cn(
                "absolute bottom-0 left-0 right-0 h-0.5",
                t.tone.replace("text-", "bg-")
              )}
            />
          )}
        </button>
      ))}
    </div>
  );
}

export const DASHBOARD_TABS = TABS;
