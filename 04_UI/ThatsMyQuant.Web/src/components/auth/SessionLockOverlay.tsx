import { useIdleTimeout } from "@/hooks/useIdleTimeout";
import { useAuth } from "@/hooks/useAuth";

/**
 * Auto-lock countdown modal. Mirrors WPF SessionLockDialog: appears at
 * (timeout − warningLead) and gives the user `secondsRemaining` to confirm
 * "Stay logged in" or be auto-logged-out. Mounted once at the AppShell level.
 */
export function SessionLockOverlay() {
  const { showWarning, secondsRemaining, stayLoggedIn } = useIdleTimeout({
    timeoutSeconds: 15 * 60, // 15 min
    warningLeadSeconds: 60,
  });
  const { logout } = useAuth();

  if (!showWarning) return null;

  return (
    <div
      className="fixed inset-0 z-[200] flex items-center justify-center bg-black/80 backdrop-blur-sm"
      role="alertdialog"
      aria-labelledby="idle-title"
      aria-describedby="idle-desc"
    >
      <div className="w-full max-w-sm rounded-2xl border border-amber-400/30 bg-[#0A0E14] shadow-2xl p-6">
        <div className="flex items-center gap-3 mb-3">
          <span className="inline-block w-2.5 h-2.5 rounded-full bg-amber-400 animate-pulse" />
          <h3 id="idle-title" className="text-sm font-black uppercase tracking-widest text-amber-400">
            Session about to lock
          </h3>
        </div>
        <p id="idle-desc" className="text-xs text-muted-foreground/80 leading-relaxed mb-5">
          We haven't seen any activity for a while. For your safety we'll log you out in{" "}
          <b className="text-amber-400 font-mono">{secondsRemaining}s</b> unless you confirm
          you're still here.
        </p>
        <div className="flex items-center gap-3">
          <button
            onClick={stayLoggedIn}
            className="flex-1 px-4 py-2 rounded-lg bg-accent text-accent-foreground text-xs font-bold uppercase tracking-wider hover:opacity-90 transition-opacity"
            autoFocus
          >
            Stay logged in
          </button>
          <button
            onClick={logout}
            className="px-4 py-2 rounded-lg border border-white/10 text-xs font-bold uppercase tracking-wider text-muted-foreground hover:text-foreground hover:border-white/20 transition-colors"
          >
            Logout
          </button>
        </div>
      </div>
    </div>
  );
}
