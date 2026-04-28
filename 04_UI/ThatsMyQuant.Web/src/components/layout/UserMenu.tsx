import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/hooks/useAuth";
import { cn } from "@/lib/utils";

const ROLE_TONE: Record<string, string> = {
  Admin: "bg-rose-400/15 text-rose-300 border-rose-400/20",
  Operator: "bg-accent/15 text-accent border-accent/20",
  ReadOnly: "bg-muted-foreground/15 text-muted-foreground border-white/10",
};

export function UserMenu() {
  const { user, role, logout, authEnabled } = useAuth();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, [open]);

  // Auth disabled: nothing to show
  if (!authEnabled || !user) return null;

  const initials = (user.username || user.email || "?").slice(0, 2).toUpperCase();

  return (
    <div ref={ref} className="relative">
      <button
        onClick={() => setOpen((v) => !v)}
        className="flex items-center gap-2 px-1.5 py-1 rounded-md text-muted-foreground/80 hover:text-foreground hover:bg-white/[0.04] transition-colors"
        title={`${user.email} · ${role}`}
      >
        <span className="w-7 h-7 rounded-full bg-gradient-to-br from-accent/30 to-indigo-500/30 border border-white/10 flex items-center justify-center text-[10px] font-black tracking-tight text-foreground">
          {initials}
        </span>
        <span className="hidden md:inline text-[10px] font-mono opacity-70">{user.username}</span>
      </button>

      {open && (
        <div className="absolute right-0 top-10 w-64 rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-2xl z-50 overflow-hidden">
          <div className="px-4 py-3 border-b border-white/5">
            <p className="text-sm font-bold truncate">{user.username}</p>
            <p className="text-[10px] text-muted-foreground/60 font-mono truncate">{user.email}</p>
            {role && (
              <span
                className={cn(
                  "inline-block mt-2 px-2 py-0.5 rounded text-[9px] font-black uppercase tracking-widest border",
                  ROLE_TONE[role] ?? ROLE_TONE.ReadOnly
                )}
              >
                {role}
              </span>
            )}
          </div>
          <button
            onClick={() => {
              setOpen(false);
              logout();
            }}
            className="w-full px-4 py-3 text-left text-xs hover:bg-rose-500/10 text-rose-300 transition-colors flex items-center gap-2"
          >
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
              <path
                d="M6 2H3a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h3M11 11l3-3-3-3M14 8H6"
                stroke="currentColor"
                strokeWidth="1.4"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
            Sign out
          </button>
        </div>
      )}
    </div>
  );
}
