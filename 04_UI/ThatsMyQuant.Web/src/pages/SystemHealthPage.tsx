import { useQuery } from "@tanstack/react-query";
import { systemHealthApi, type ServiceHealth, type ServiceState, type ExchangeHealthSummary } from "@/api/systemHealth";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";
import { SkeletonCard } from "@/components/ui/Skeleton";

const STATE_TONE: Record<ServiceState, string> = {
  healthy: "border-emerald-500/30 bg-emerald-500/[0.04]",
  stale: "border-amber-500/30 bg-amber-500/[0.04]",
  down: "border-rose-500/30 bg-rose-500/[0.04]",
};

const STATE_PILL: Record<ServiceState, string> = {
  healthy: "bg-emerald-400/15 text-emerald-300",
  stale: "bg-amber-400/15 text-amber-300",
  down: "bg-rose-400/15 text-rose-300",
};

const STATE_DOT: Record<ServiceState, string> = {
  healthy: "bg-emerald-400 animate-pulse",
  stale: "bg-amber-400",
  down: "bg-rose-400",
};

const STATE_LABEL: Record<ServiceState, string> = {
  healthy: "HEALTHY",
  stale: "STALE",
  down: "DOWN",
};

export function SystemHealthPage() {
  const q = useQuery({
    queryKey: ["system", "health"],
    queryFn: systemHealthApi.get,
    refetchInterval: 5_000,
  });

  if (q.isLoading) {
    return (
      <div className="space-y-6">
        <div className="grid md:grid-cols-3 gap-4">
          <SkeletonCard />
          <SkeletonCard />
          <SkeletonCard />
        </div>
      </div>
    );
  }

  if (!q.data) {
    return (
      <div className="p-6 text-sm text-rose-400">Failed to load system health.</div>
    );
  }

  const { summary, services, exchange } = q.data;

  return (
    <div className="space-y-8">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Live Service Topology
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          System Health{" "}
          <span className="text-muted-foreground/30 font-light text-base">
            — refresh 5s · heartbeat threshold 60s
          </span>
        </p>
      </div>

      {/* Summary stat row */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <SummaryCard label="Total Services" value={summary.totalServices.toString()} tone="neutral" />
        <SummaryCard
          label="Healthy"
          value={summary.healthyCount.toString()}
          tone={summary.healthyCount === summary.totalServices ? "pos" : "neutral"}
        />
        <SummaryCard
          label="Stale"
          value={summary.staleCount.toString()}
          tone={summary.staleCount > 0 ? "warn" : "neutral"}
        />
        <SummaryCard
          label="Down"
          value={summary.downCount.toString()}
          tone={summary.downCount > 0 ? "neg" : "neutral"}
        />
      </div>

      {/* Service cards */}
      <div className="space-y-3">
        <p className="text-[10px] font-black uppercase tracking-widest text-muted-foreground/60">
          Services ({services.length})
        </p>
        {services.length === 0 ? (
          <p className="rounded-xl border border-white/5 bg-[#0D1117]/60 p-8 text-center text-xs text-muted-foreground">
            No service heartbeats yet — wait ~30s after worker startup.
          </p>
        ) : (
          <div className="grid md:grid-cols-2 gap-3">
            {services.map((s) => (
              <ServiceCard key={s.serviceName} svc={s} />
            ))}
          </div>
        )}
      </div>

      {/* Exchange tile */}
      {exchange && <ExchangeCard exchange={exchange} />}
    </div>
  );
}

function ServiceCard({ svc: s }: { svc: ServiceHealth }) {
  return (
    <div className={cn("rounded-xl border p-4", STATE_TONE[s.state])}>
      <div className="flex items-start justify-between mb-3">
        <div className="min-w-0 flex-1">
          <p className="font-bold text-sm truncate">{s.serviceName}</p>
          <p className="text-[10px] text-muted-foreground/50 font-mono mt-0.5">
            v{s.version || "—"} · pid {s.processId} · {s.machineName}
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          <span className={cn("w-1.5 h-1.5 rounded-full", STATE_DOT[s.state])} />
          <span
            className={cn(
              "px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest",
              STATE_PILL[s.state]
            )}
          >
            {STATE_LABEL[s.state]}
          </span>
        </div>
      </div>
      <div className="grid grid-cols-3 gap-3 text-[10px]">
        <Stat label="Last Seen" value={`${Math.round(s.ageSeconds)}s ago`} />
        <Stat label="Uptime" value={formatUptime(s.uptimeSeconds)} />
        <Stat label="Memory" value={`${s.memoryMb.toFixed(0)} MB`} />
      </div>
      <p className="text-[9px] text-muted-foreground/30 font-mono mt-2 truncate">
        Started {formatDateTime(s.startedUtc)} · {s.status}
      </p>
    </div>
  );
}

function ExchangeCard({ exchange: x }: { exchange: ExchangeHealthSummary }) {
  const tone = x.isConnected
    ? "border-emerald-500/30 bg-emerald-500/[0.04]"
    : x.credentialsConfigured
    ? "border-rose-500/30 bg-rose-500/[0.04]"
    : "border-amber-500/30 bg-amber-500/[0.04]";

  const label = !x.credentialsConfigured
    ? "NO CREDS"
    : x.isConnected
    ? x.isDemoMode
      ? "DEMO ACTIVE"
      : "LIVE ACTIVE"
    : "DISCONNECTED";

  return (
    <div className="space-y-3">
      <p className="text-[10px] font-black uppercase tracking-widest text-muted-foreground/60">
        Exchange
      </p>
      <div className={cn("rounded-xl border p-4", tone)}>
        <div className="flex items-start justify-between">
          <div>
            <p className="font-bold text-sm">{x.venue || "Unknown"}</p>
            <p className="text-[10px] text-muted-foreground/50 font-mono mt-0.5">
              {x.lastSuccessUtc ? `Last OK ${formatDateTime(x.lastSuccessUtc)}` : "No success yet"}
            </p>
          </div>
          <span
            className={cn(
              "px-2 py-1 rounded text-[9px] font-black uppercase tracking-widest",
              x.isConnected ? "bg-emerald-400/15 text-emerald-300" : "bg-rose-400/15 text-rose-300"
            )}
          >
            {label}
          </span>
        </div>
        {x.lastError && (
          <p className="mt-3 text-[10px] font-mono text-rose-300/70 break-all">
            {x.lastError}
          </p>
        )}
        {x.consecutiveFailures > 0 && (
          <p className="mt-1 text-[10px] text-amber-300/70">
            {x.consecutiveFailures} consecutive failure{x.consecutiveFailures !== 1 ? "s" : ""}
          </p>
        )}
      </div>
    </div>
  );
}

function SummaryCard({ label, value, tone }: { label: string; value: string; tone: "pos" | "warn" | "neg" | "neutral" }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-4">
      <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/40">{label}</p>
      <p
        className={cn(
          "text-3xl font-black font-mono tabular-nums mt-1",
          tone === "pos" && "text-emerald-400",
          tone === "warn" && "text-amber-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/40">{label}</p>
      <p className="font-mono font-bold tabular-nums mt-0.5 truncate">{value}</p>
    </div>
  );
}

function formatUptime(sec: number): string {
  if (sec < 60) return `${sec}s`;
  if (sec < 3600) return `${Math.floor(sec / 60)}m`;
  if (sec < 86_400) return `${Math.floor(sec / 3600)}h ${Math.floor((sec % 3600) / 60)}m`;
  return `${Math.floor(sec / 86_400)}d ${Math.floor((sec % 86_400) / 3600)}h`;
}
