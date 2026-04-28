import { useEffect, useState } from "react";
import { cn } from "@/lib/utils";
import { SyntheticBadge } from "@/components/ui/SyntheticBadge";

/**
 * Latency snapshot. Backend doesn't expose dedicated metrics yet, so this
 * card runs lightweight client-side probes — measures the round-trip time
 * to /api/portfolio (controller path), then reports p50/p95/last samples.
 *
 * When real metrics ship via /api/observability or Prometheus, swap the
 * source — the UI shape stays the same.
 */
const PROBE_PATH = "/api/portfolio";
const SAMPLE_WINDOW = 30;

interface Sample {
  ts: number;
  ms: number;
  ok: boolean;
}

export function LatencyDashboard() {
  const [samples, setSamples] = useState<Sample[]>([]);

  useEffect(() => {
    let mounted = true;
    const baseUrl =
      (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? "http://localhost:5266";

    async function probe() {
      const start = performance.now();
      let ok = true;
      try {
        const res = await fetch(`${baseUrl}${PROBE_PATH}`, { method: "GET" });
        ok = res.ok;
      } catch {
        ok = false;
      }
      const ms = performance.now() - start;
      if (mounted) {
        setSamples((prev) => [...prev.slice(-(SAMPLE_WINDOW - 1)), { ts: Date.now(), ms, ok }]);
      }
    }

    probe();
    const id = setInterval(probe, 6_000);
    return () => {
      mounted = false;
      clearInterval(id);
    };
  }, []);

  const stats = (() => {
    if (samples.length === 0) return null;
    const sorted = [...samples].map((s) => s.ms).sort((a, b) => a - b);
    const p = (frac: number) => sorted[Math.floor(sorted.length * frac)] ?? 0;
    const p50 = p(0.5);
    const p95 = p(0.95);
    const max = sorted[sorted.length - 1];
    const last = samples[samples.length - 1];
    const errors = samples.filter((s) => !s.ok).length;
    return { p50, p95, max, last, errors, count: samples.length };
  })();

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      <div className="flex items-baseline justify-between mb-3">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
              API Latency
            </p>
            <SyntheticBadge source="client-side" />
          </div>
          <p className="text-[11px] font-serif italic text-muted-foreground/60 mt-0.5">
            browser-measured RTT to {PROBE_PATH} · {samples.length} samples
          </p>
        </div>
        {stats && (
          <span
            className={cn(
              "text-[10px] font-mono uppercase tracking-widest",
              stats.errors === 0
                ? "text-emerald-400"
                : stats.errors / stats.count < 0.1
                  ? "text-amber-400"
                  : "text-rose-400"
            )}
          >
            {stats.errors === 0
              ? "0 errors"
              : `${stats.errors}/${stats.count} errors`}
          </span>
        )}
      </div>

      {!stats ? (
        <p className="text-[11px] italic font-serif text-muted-foreground/50 text-center py-4">
          — collecting samples —
        </p>
      ) : (
        <>
          <div className="grid grid-cols-4 gap-3">
            <Stat label="Last" value={`${stats.last.ms.toFixed(0)}ms`} tone={tone(stats.last.ms)} />
            <Stat label="p50" value={`${stats.p50.toFixed(0)}ms`} tone={tone(stats.p50)} />
            <Stat label="p95" value={`${stats.p95.toFixed(0)}ms`} tone={tone(stats.p95)} />
            <Stat label="Max" value={`${stats.max.toFixed(0)}ms`} tone={tone(stats.max)} />
          </div>

          {/* Sparkline of recent samples */}
          <div className="mt-3 pt-3 border-t border-white/5">
            <div className="flex items-end gap-0.5 h-12">
              {samples.map((s, i) => {
                const max = Math.max(...samples.map((x) => x.ms), 1);
                const h = (s.ms / max) * 100;
                return (
                  <div
                    key={i}
                    className={cn(
                      "flex-1 rounded-sm",
                      !s.ok
                        ? "bg-rose-400/60"
                        : s.ms < 100
                          ? "bg-emerald-400/60"
                          : s.ms < 300
                            ? "bg-amber-400/60"
                            : "bg-rose-400/60"
                    )}
                    style={{ height: `${Math.max(2, h)}%` }}
                    title={`${s.ms.toFixed(0)}ms`}
                  />
                );
              })}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function tone(ms: number): "pos" | "warn" | "neg" {
  if (ms < 100) return "pos";
  if (ms < 300) return "warn";
  return "neg";
}

function Stat({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone: "pos" | "warn" | "neg";
}) {
  return (
    <div>
      <p className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/45 mb-0.5">
        {label}
      </p>
      <p
        className={cn(
          "font-mono font-black tabular-nums text-[15px] leading-none",
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
