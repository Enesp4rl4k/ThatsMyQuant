import { useQuery } from "@tanstack/react-query";
import { dashboardIntelApi } from "@/api/dashboardIntel";
import { formatUsd, pnlColor } from "@/lib/format";
import { cn } from "@/lib/utils";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";

/**
 * Operator brief — 3-paragraph editorial summary of book state. Updates every
 * 30 minutes from server-side template (no LLM); paragraph 1 = overnight,
 * paragraph 2 = today's outlook, paragraph 3 = recommended actions.
 */
export function DailyBriefCard() {
  const q = useQuery({
    queryKey: ["dashboard", "daily-brief"],
    queryFn: dashboardIntelApi.dailyBrief,
    refetchInterval: 30 * 60_000,
  });

  const b = q.data;
  const pnl24hPositive = b ? b.pnl24h >= 0 : true;

  return (
    <div className="rounded-xl border border-accent/20 bg-gradient-to-br from-accent/[0.04] to-transparent backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5 flex items-baseline justify-between">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/80">
              Operator Brief · Daily
            </p>
            <SyntheticBadge source="deterministic" />
          </div>
          <p className="text-[12px] font-serif italic text-muted-foreground/70 mt-0.5">
            template-generated read · refreshes every 30 minutes · no LLM
          </p>
        </div>
        {b && (
          <div className="hidden md:flex items-center gap-3 text-[10px] font-mono">
            <Pill label="24h trades" value={`${b.closedTrades24h}`} />
            <Pill
              label="24h P&L"
              value={`${pnl24hPositive ? "+" : ""}${formatUsd(b.pnl24h)}`}
              toneClass={pnlColor(b.pnl24h)}
            />
            <Pill label="Open" value={b.openPositions.toString()} />
          </div>
        )}
      </div>

      <div className="p-5">
        {q.isLoading || !b ? (
          <p className="text-xs italic font-serif text-muted-foreground/60">
            — analyst is reading the tape —
          </p>
        ) : (
          <div className="space-y-4">
            <Section heading="Overnight" body={b.overnight} />
            <Section heading="Today's outlook" body={b.outlook} />
            <Section heading="Recommendation" body={b.recommendation} />

            {b.actions.length > 0 && (
              <div className="pt-3 border-t border-white/5">
                <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
                  Action items
                </p>
                <ul className="space-y-1.5">
                  {b.actions.map((a, i) => (
                    <li
                      key={i}
                      className="text-[12px] font-serif italic text-foreground/85 flex items-start gap-2 leading-relaxed"
                    >
                      <span className="shrink-0 text-accent/70 font-bold mt-0.5">→</span>
                      <span>{a}</span>
                    </li>
                  ))}
                </ul>
              </div>
            )}

            <p className="text-[10px] font-mono italic text-muted-foreground/40 pt-3 border-t border-white/5">
              generated {new Date(b.generatedUtc).toLocaleString()} · refreshes 30m
            </p>
          </div>
        )}
      </div>
    </div>
  );
}

function Section({ heading, body }: { heading: string; body: string }) {
  return (
    <div>
      <p className="text-[9px] font-black uppercase tracking-widest text-accent/80 mb-1.5">
        {heading}
      </p>
      <p className="font-serif text-[14px] leading-[1.65] text-foreground/95 first-letter:font-bold first-letter:text-[18px]">
        {body}
      </p>
    </div>
  );
}

function Pill({
  label,
  value,
  toneClass,
}: {
  label: string;
  value: string;
  toneClass?: string;
}) {
  return (
    <span className="flex items-baseline gap-1">
      <span className="text-[8px] uppercase tracking-widest text-muted-foreground/50">
        {label}
      </span>
      <span className={cn("tabular-nums font-bold", toneClass)}>{value}</span>
    </span>
  );
}
