import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi, type SignalSnapshot } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";

const STATUS_COLOR: Record<string, string> = {
  Approved: "text-emerald-400 bg-emerald-500/10 border-emerald-500/30",
  Executed: "text-emerald-400 bg-emerald-500/10 border-emerald-500/30",
  Filled: "text-emerald-400 bg-emerald-500/10 border-emerald-500/30",
  Pending: "text-accent bg-accent/10 border-accent/30",
  Queued: "text-accent bg-accent/10 border-accent/30",
  Rejected: "text-rose-400 bg-rose-500/10 border-rose-500/30",
  Failed: "text-rose-400 bg-rose-500/10 border-rose-500/30",
  Cancelled: "text-amber-400 bg-amber-500/10 border-amber-500/30",
  Expired: "text-amber-400 bg-amber-500/10 border-amber-500/30",
};

export function LastSignalCard() {
  const q = useQuery({
    queryKey: ["signals", "recent", 25],
    queryFn: () => dashboardApi.recentSignals(25),
    refetchInterval: 8_000,
  });

  const [expanded, setExpanded] = useState<string | null>(null);

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-5 py-3 border-b border-white/5 flex items-center justify-between">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
          Signal Stream — Last 25
        </p>
        {q.isFetching && (
          <span className="text-[9px] uppercase tracking-widest text-accent/60 animate-pulse">live</span>
        )}
      </div>

      {q.isLoading ? (
        <p className="p-6 text-xs text-muted-foreground/50">Loading signals…</p>
      ) : !q.data || q.data.length === 0 ? (
        <p className="p-6 text-xs text-muted-foreground/50">
          No signals received yet. Webhook → /webhook → Listener.Api populates this stream.
        </p>
      ) : (
        <div className="max-h-96 overflow-y-auto">
          {q.data.map((s) => (
            <SignalRow
              key={s.id}
              signal={s}
              expanded={expanded === s.id}
              onToggle={() => setExpanded(expanded === s.id ? null : s.id)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function SignalRow({
  signal: s,
  expanded,
  onToggle,
}: {
  signal: SignalSnapshot;
  expanded: boolean;
  onToggle: () => void;
}) {
  const statusClass = STATUS_COLOR[s.status] ?? "text-muted-foreground bg-white/5 border-white/10";

  return (
    <div className="border-b border-white/[0.03] last:border-0">
      <button
        onClick={onToggle}
        className="w-full grid grid-cols-12 gap-2 px-5 py-2.5 text-xs hover:bg-white/[0.02] transition-colors text-left"
      >
        <span className="col-span-2 font-mono text-[10px] text-muted-foreground/60">
          {formatDateTime(s.timestamp)}
        </span>
        <span className="col-span-2 font-bold">{s.symbol}</span>
        <span
          className={cn(
            "col-span-1 px-1.5 py-0.5 rounded text-[9px] font-black tracking-wider text-center",
            s.side === "Buy"
              ? "bg-emerald-400/10 text-emerald-400"
              : "bg-rose-400/10 text-rose-400"
          )}
        >
          {s.side.toUpperCase()}
        </span>
        <span className="col-span-2 text-[10px] uppercase tracking-wider text-muted-foreground/70">
          {s.strategyTag}
        </span>
        <span className="col-span-1 text-right font-mono text-muted-foreground">
          {s.confidence}%
        </span>
        <span className="col-span-2 text-right font-mono text-muted-foreground/70 text-[10px]">
          {s.regimeTag}
        </span>
        <span
          className={cn(
            "col-span-2 inline-block px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest text-center border self-center justify-self-end",
            statusClass
          )}
        >
          {s.status}
        </span>
      </button>
      {expanded && (
        <div className="px-5 pb-3 pt-1 grid grid-cols-2 gap-x-6 gap-y-1.5 text-[10px] text-muted-foreground/70 bg-black/20">
          <Detail label="Source" value={s.source || "—"} />
          <Detail label="Exchange" value={s.exchange || "—"} />
          <Detail label="Price" value={s.price.toString()} />
          <Detail label="Quantity" value={s.quantity.toString()} />
          <Detail label="Reason Flags" value={s.reasonFlags || "None"} />
          <Detail label="Signal ID" value={s.id.slice(0, 8)} mono />
          {s.reasoning && (
            <div className="col-span-2 mt-1 pt-2 border-t border-white/5">
              <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1">
                Reasoning
              </p>
              <p className="text-[11px] text-foreground/80 leading-relaxed">{s.reasoning}</p>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function Detail({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex items-center justify-between gap-3">
      <span className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/40">
        {label}
      </span>
      <span className={cn("text-[10px] truncate", mono ? "font-mono" : "")}>{value}</span>
    </div>
  );
}
