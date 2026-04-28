import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { cn } from "@/lib/utils";
import { formatUsdCompact } from "@/lib/format";

type Status = "ACTIVE" | "DEMO" | "AUTH FAILED" | "NO CREDS" | "OFFLINE";

interface VisualState {
  label: Status;
  dotClass: string;
  textClass: string;
  borderClass: string;
}

const STATE_MAP: Record<Status, VisualState> = {
  ACTIVE: {
    label: "ACTIVE",
    dotClass: "bg-emerald-400 shadow-[0_0_8px_rgba(52,211,153,0.7)]",
    textClass: "text-emerald-400",
    borderClass: "border-emerald-400/30",
  },
  DEMO: {
    label: "DEMO",
    dotClass: "bg-amber-400 shadow-[0_0_8px_rgba(251,191,36,0.6)]",
    textClass: "text-amber-400",
    borderClass: "border-amber-400/30",
  },
  "AUTH FAILED": {
    label: "AUTH FAILED",
    dotClass: "bg-rose-500 shadow-[0_0_8px_rgba(244,63,94,0.7)]",
    textClass: "text-rose-400",
    borderClass: "border-rose-500/30",
  },
  "NO CREDS": {
    label: "NO CREDS",
    dotClass: "bg-zinc-500",
    textClass: "text-zinc-400",
    borderClass: "border-zinc-500/30",
  },
  OFFLINE: {
    label: "OFFLINE",
    dotClass: "bg-rose-600",
    textClass: "text-rose-400",
    borderClass: "border-rose-600/30",
  },
};

export function ExchangeHealthBadge() {
  const { data, isError } = useQuery({
    queryKey: ["exchange-health"],
    queryFn: dashboardApi.exchangeHealth,
    refetchInterval: 20_000,
  });

  let status: Status = "OFFLINE";
  if (!isError && data) {
    if (!data.credentialsConfigured) status = "NO CREDS";
    else if (!data.isConnected) status = "AUTH FAILED";
    else if (data.isDemoMode) status = "DEMO";
    else status = "ACTIVE";
  }

  const visual = STATE_MAP[status];
  const balanceText = data?.balance != null ? formatUsdCompact(data.balance) : null;

  return (
    <div
      className={cn(
        "flex items-center gap-3 px-4 py-2 rounded-lg border bg-[#0D1117]/60 backdrop-blur",
        visual.borderClass
      )}
    >
      <div className="flex items-center gap-2">
        <div className={cn("w-2 h-2 rounded-full", visual.dotClass)} />
        <span className={cn("text-[10px] font-black uppercase tracking-[0.2em]", visual.textClass)}>
          {visual.label}
        </span>
      </div>
      <div className="w-px h-4 bg-white/5" />
      <div className="flex flex-col leading-none">
        <span className="text-[8px] font-black text-muted-foreground/40 uppercase tracking-widest">
          Venue
        </span>
        <span className="text-xs font-bold text-foreground mt-0.5">{data?.venue ?? "—"}</span>
      </div>
      {balanceText && (
        <>
          <div className="w-px h-4 bg-white/5" />
          <div className="flex flex-col leading-none">
            <span className="text-[8px] font-black text-muted-foreground/40 uppercase tracking-widest">
              Balance
            </span>
            <span className="text-xs font-mono font-bold text-foreground mt-0.5">{balanceText}</span>
          </div>
        </>
      )}
    </div>
  );
}
