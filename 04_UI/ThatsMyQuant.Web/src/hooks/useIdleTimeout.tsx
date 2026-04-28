import { useEffect, useRef, useState, useCallback } from "react";
import { useAuth } from "@/hooks/useAuth";

export interface IdleTimeoutOptions {
  /** Seconds of inactivity before lock fires. Default: 15 min. */
  timeoutSeconds?: number;
  /** Seconds before timeout to surface the warning modal. Default: 60s. */
  warningLeadSeconds?: number;
  /** Set false to disable (e.g. development). Default: true. */
  enabled?: boolean;
}

const ACTIVITY_EVENTS = ["mousemove", "mousedown", "keydown", "touchstart", "wheel"] as const;
const STORAGE_KEY = "tmq.lastActivity";

/**
 * Auto-lock after inactivity. Mirrors WPF Dashboard's IdleTimeoutService:
 *   - 1s tick checks Date.now() − lastActivity
 *   - At (timeout − warning), `showWarning` flips true → modal asks "stay?"
 *   - If timeout elapses unanswered, calls `logout()` and emits an audit log
 *   - Any user input resets the timer (and dismisses the warning)
 *   - Cross-tab sync via `storage` event so activity in one tab keeps all alive
 *
 * Only active when auth is enabled AND user is logged in.
 */
export function useIdleTimeout(opts: IdleTimeoutOptions = {}) {
  const {
    timeoutSeconds = 900,
    warningLeadSeconds = 60,
    enabled = true,
  } = opts;
  const { authEnabled, user, logout } = useAuth();
  const [showWarning, setShowWarning] = useState(false);
  const [secondsRemaining, setSecondsRemaining] = useState(warningLeadSeconds);
  const lastActivityRef = useRef<number>(Date.now());

  const active = enabled && authEnabled === true && !!user;

  const resetActivity = useCallback(() => {
    const now = Date.now();
    lastActivityRef.current = now;
    try {
      localStorage.setItem(STORAGE_KEY, String(now));
    } catch {
      /* ignore quota / private mode */
    }
    if (showWarning) setShowWarning(false);
  }, [showWarning]);

  // Track input activity (debounced via single-frame ref)
  useEffect(() => {
    if (!active) return;
    const onActivity = () => {
      lastActivityRef.current = Date.now();
      try { localStorage.setItem(STORAGE_KEY, String(lastActivityRef.current)); } catch {}
    };
    for (const ev of ACTIVITY_EVENTS) {
      window.addEventListener(ev, onActivity, { passive: true });
    }
    return () => {
      for (const ev of ACTIVITY_EVENTS) window.removeEventListener(ev, onActivity);
    };
  }, [active]);

  // Cross-tab sync — if another tab updates lastActivity, adopt it
  useEffect(() => {
    if (!active) return;
    const onStorage = (e: StorageEvent) => {
      if (e.key !== STORAGE_KEY || !e.newValue) return;
      const v = Number(e.newValue);
      if (!Number.isNaN(v) && v > lastActivityRef.current) {
        lastActivityRef.current = v;
        if (showWarning) setShowWarning(false);
      }
    };
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, [active, showWarning]);

  // 1s tick — checks the clock
  useEffect(() => {
    if (!active) {
      setShowWarning(false);
      return;
    }
    const id = window.setInterval(() => {
      const elapsed = (Date.now() - lastActivityRef.current) / 1000;
      const remaining = timeoutSeconds - elapsed;
      if (remaining <= 0) {
        // Audit + logout
        try {
          // Best-effort audit ping; non-blocking, no-op if endpoint absent
          fetch("/api/audit/events", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              source: "Web/IdleTimeout",
              severity: "Info",
              message: `Session locked after ${timeoutSeconds}s idle`,
            }),
          }).catch(() => {});
        } catch {
          /* ignore */
        }
        setShowWarning(false);
        logout();
      } else if (remaining <= warningLeadSeconds) {
        setShowWarning(true);
        setSecondsRemaining(Math.max(0, Math.ceil(remaining)));
      } else if (showWarning) {
        setShowWarning(false);
      }
    }, 1000);
    return () => window.clearInterval(id);
  }, [active, timeoutSeconds, warningLeadSeconds, logout, showWarning]);

  return { showWarning, secondsRemaining, stayLoggedIn: resetActivity };
}
