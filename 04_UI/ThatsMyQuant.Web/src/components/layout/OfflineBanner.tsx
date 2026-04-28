import { useEffect, useState } from "react";
import { apiBaseUrl } from "@/api/client";
import { cn } from "@/lib/utils";

/**
 * Visible banner shown when the Listener.Api backend stops responding.
 * Probes the public health endpoint every 8s; if 3 consecutive failures
 * occur, fires the banner with restart instructions.
 */
const PROBE_INTERVAL = 8_000;
const FAIL_THRESHOLD = 3;

export function OfflineBanner() {
  const [offline, setOffline] = useState(false);
  const [lastError, setLastError] = useState<string | null>(null);

  useEffect(() => {
    let mounted = true;
    let consecutiveFails = 0;

    async function probe() {
      try {
        const ctl = new AbortController();
        const tm = setTimeout(() => ctl.abort(), 4_000);
        const res = await fetch(`${apiBaseUrl}/api/auth/config`, {
          method: "GET",
          signal: ctl.signal,
        });
        clearTimeout(tm);
        if (!mounted) return;
        if (res.ok) {
          consecutiveFails = 0;
          setOffline(false);
          setLastError(null);
        } else {
          consecutiveFails++;
          if (consecutiveFails >= FAIL_THRESHOLD) {
            setOffline(true);
            setLastError(`HTTP ${res.status}`);
          }
        }
      } catch (e) {
        if (!mounted) return;
        consecutiveFails++;
        if (consecutiveFails >= FAIL_THRESHOLD) {
          setOffline(true);
          setLastError(e instanceof Error ? e.message : "fetch failed");
        }
      }
    }

    probe();
    const id = setInterval(probe, PROBE_INTERVAL);
    return () => {
      mounted = false;
      clearInterval(id);
    };
  }, []);

  if (!offline) return null;

  return (
    <div
      className={cn(
        "relative px-4 py-2 border-b-2 border-rose-500/50 bg-rose-500/[0.08] backdrop-blur-xl",
        "flex items-center gap-3 text-[12px] text-rose-300"
      )}
      role="alert"
    >
      <span className="flex items-center gap-1.5 shrink-0">
        <span className="w-2 h-2 rounded-full bg-rose-500 animate-pulse" />
        <span className="font-black uppercase tracking-widest text-[10px] text-rose-400">
          Backend offline
        </span>
      </span>
      <span className="font-serif italic flex-1 min-w-0 truncate">
        Listener.Api is not responding ({lastError ?? "no response"}). Most cards will show stale or
        empty data — start the backend at{" "}
        <code className="text-rose-200 font-mono not-italic">{apiBaseUrl}</code> and this banner
        will clear automatically.
      </span>
      <code className="hidden md:inline text-[10px] text-rose-200/70 font-mono shrink-0">
        cd 01_Services/Listener.Api && dotnet run
      </code>
    </div>
  );
}
