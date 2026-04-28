import { useEffect, useRef, useState } from "react";
import { workspaceStore, type MarketsWorkspace } from "@/lib/workspaces";
import { cn } from "@/lib/utils";
import { formatDateTime } from "@/lib/format";

interface Props {
  current: Omit<MarketsWorkspace, "id" | "name" | "createdUtc">;
  onLoad: (ws: MarketsWorkspace) => void;
}

export function WorkspaceMenu({ current, onLoad }: Props) {
  const [open, setOpen] = useState(false);
  const [list, setList] = useState<MarketsWorkspace[]>(() => workspaceStore.list());
  const [name, setName] = useState("");
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => workspaceStore.subscribe(setList), []);

  useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, [open]);

  const save = () => {
    workspaceStore.save(name || `Workspace ${list.length + 1}`, current);
    setName("");
  };

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((v) => !v)}
        className="flex items-center gap-1.5 px-2.5 py-1 rounded-md text-[10px] font-mono text-muted-foreground/70 border border-white/10 bg-white/[0.02] hover:bg-white/[0.05] hover:text-foreground transition-colors"
        title="Save / load named workspace layouts"
      >
        <svg width="11" height="11" viewBox="0 0 16 16" fill="none">
          <path
            d="M2 4a2 2 0 0 1 2-2h4l2 2h4a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V4z"
            stroke="currentColor"
            strokeWidth="1.4"
            fill="none"
          />
        </svg>
        Workspaces
        {list.length > 0 && (
          <span className="text-accent/70 font-bold">· {list.length}</span>
        )}
      </button>

      {open && (
        <div className="absolute right-0 top-9 w-72 rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-paper-lg overflow-hidden z-50">
          <div className="px-3 py-2 border-b border-white/5">
            <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/60 mb-1.5">
              Save current
            </p>
            <div className="flex items-center gap-1.5">
              <input
                type="text"
                value={name}
                onChange={(e) => setName(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && save()}
                placeholder="Workspace name…"
                className="flex-1 bg-[#070A0F] border border-white/10 rounded px-2 py-1 text-[11px] font-mono"
              />
              <button
                onClick={save}
                className="px-2.5 py-1 rounded text-[9px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/40 hover:bg-accent/25 transition-colors"
              >
                Save
              </button>
            </div>
            <p className="text-[9px] font-mono text-muted-foreground/40 italic mt-1.5">
              {current.layoutMode} · {current.pinned.length} pinned · {current.indicators.length} indicators
            </p>
          </div>

          <div className="max-h-72 overflow-y-auto">
            {list.length === 0 ? (
              <p className="p-4 text-center text-xs italic font-serif text-muted-foreground/60">
                — no saved workspaces —
              </p>
            ) : (
              list.map((ws) => (
                <div
                  key={ws.id}
                  className="px-3 py-2 border-b border-white/[0.04] hover:bg-white/[0.02] flex items-center justify-between gap-2"
                >
                  <button
                    onClick={() => {
                      onLoad(ws);
                      setOpen(false);
                    }}
                    className="flex-1 min-w-0 text-left"
                  >
                    <p className="text-[12px] font-bold truncate">{ws.name}</p>
                    <p className="text-[9px] font-mono text-muted-foreground/50 italic truncate">
                      {ws.selected} · {ws.layoutMode} · saved {formatDateTime(ws.createdUtc)}
                    </p>
                  </button>
                  <button
                    onClick={() => workspaceStore.remove(ws.id)}
                    aria-label={`Delete ${ws.name}`}
                    className={cn(
                      "shrink-0 w-6 h-6 rounded flex items-center justify-center",
                      "text-muted-foreground/40 hover:text-rose-400 hover:bg-rose-500/10 transition-colors"
                    )}
                  >
                    <svg width="10" height="10" viewBox="0 0 10 10" fill="none">
                      <path d="M2 2l6 6M8 2L2 8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
                    </svg>
                  </button>
                </div>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  );
}
