import { useQuery } from "@tanstack/react-query";
import { promotionApi } from "@/api/promotion";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";
import { SkeletonCard } from "@/components/ui/Skeleton";

export function MlStatusCard() {
  const q = useQuery({
    queryKey: ["ml", "status"],
    queryFn: promotionApi.mlStatus,
    refetchInterval: 30_000,
  });

  const s = q.data;

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="mb-3">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          ML Pipeline
        </p>
        <p className="text-sm font-bold">
          Trade Outcome Model{" "}
          <span className="text-muted-foreground/40 font-light text-xs">
            — directional-only sampling (pairs/funding excluded)
          </span>
        </p>
      </div>

      {q.isLoading ? (
        <SkeletonCard className="border-0 bg-transparent p-0" />
      ) : !s ? (
        <p className="text-xs text-rose-400">Failed to load ML status.</p>
      ) : (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3 text-[11px]">
          <Stat
            label="Active Model"
            value={s.hasActiveModel ? "Loaded" : "Not Trained"}
            tone={s.hasActiveModel ? "pos" : "neutral"}
          />
          <Stat
            label="Samples"
            value={`${s.totalSamples}`}
            sub={`${s.positiveSamples}W · ${s.negativeSamples}L`}
            tone={s.totalSamples >= 50 ? "pos" : "neutral"}
          />
          <Stat
            label="Class Balance"
            value={`${(s.classBalance * 100).toFixed(0)}%`}
            sub={s.isTrainable ? "trainable" : "skewed"}
            tone={s.isTrainable ? "pos" : "warn"}
          />
          <Stat
            label="Symbols"
            value={s.uniqueSymbols.toString()}
            sub={
              s.latestSampleUtc && new Date(s.latestSampleUtc).getFullYear() > 2000
                ? `last ${formatDateTime(s.latestSampleUtc)}`
                : "no samples yet"
            }
          />
          <div className="col-span-2 md:col-span-4 mt-1 pt-3 border-t border-white/5 text-[10px] text-muted-foreground/50">
            <p className="font-mono">
              Sampled tags:{" "}
              {s.directionalStrategyTags.map((t) => (
                <span
                  key={t}
                  className="inline-block px-1.5 py-0.5 mr-1 mb-1 rounded bg-white/5 text-foreground/70"
                >
                  {t}
                </span>
              ))}
            </p>
            <p className="mt-1.5">
              <span className="text-muted-foreground/40">Artifact:</span>{" "}
              <code className="text-accent/70">{s.artifactPath}</code>
            </p>
          </div>
        </div>
      )}
    </div>
  );
}

function Stat({
  label,
  value,
  sub,
  tone = "neutral",
}: {
  label: string;
  value: string;
  sub?: string;
  tone?: "pos" | "warn" | "neg" | "neutral";
}) {
  return (
    <div>
      <p className="text-[9px] uppercase tracking-widest text-muted-foreground/40 mb-0.5">{label}</p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-sm",
          tone === "pos" && "text-emerald-400",
          tone === "warn" && "text-amber-400",
          tone === "neg" && "text-rose-400"
        )}
      >
        {value}
      </p>
      {sub && <p className="text-[9px] text-muted-foreground/40">{sub}</p>}
    </div>
  );
}
