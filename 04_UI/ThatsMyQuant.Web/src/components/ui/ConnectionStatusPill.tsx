import { useState, useEffect } from "react";
import { cn } from "@/lib/utils";
import { getConnectionState, onConnectionStateChange, type ConnectionState } from "@/lib/signalr";

const LABEL: Record<ConnectionState, string> = {
  connecting: "CONNECTING",
  connected: "LIVE",
  reconnecting: "RECONNECTING",
  disconnected: "OFFLINE",
};

const TONE: Record<ConnectionState, string> = {
  connecting: "text-accent/60 border-accent/20 bg-accent/[0.06]",
  connected: "text-emerald-400 border-emerald-500/20 bg-emerald-500/[0.06]",
  reconnecting: "text-amber-400 border-amber-500/20 bg-amber-500/[0.06]",
  disconnected: "text-rose-400 border-rose-500/20 bg-rose-500/[0.06]",
};

const DOT_TONE: Record<ConnectionState, string> = {
  connecting: "bg-accent/60 animate-pulse",
  connected: "bg-emerald-400 animate-pulse",
  reconnecting: "bg-amber-400 animate-[pulse_0.8s_ease-in-out_infinite]",
  disconnected: "bg-rose-400",
};

export function ConnectionStatusPill() {
  const [state, setState] = useState<ConnectionState>(getConnectionState);

  useEffect(() => {
    return onConnectionStateChange(setState);
  }, []);

  return (
    <div
      className={cn(
        "hidden sm:flex items-center gap-1.5 px-2.5 py-1 rounded-full border text-[9px] font-black uppercase tracking-widest transition-colors duration-500",
        TONE[state]
      )}
      title={`SignalR: ${state}`}
    >
      <span className={cn("w-1.5 h-1.5 rounded-full", DOT_TONE[state])} />
      {LABEL[state]}
    </div>
  );
}
