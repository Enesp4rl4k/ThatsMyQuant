// Operator notification preferences — sound + desktop notifications.
// Persisted in localStorage; subscribed by NotificationsBell + priceAlerts.

import { subscribeToNotifications, type Notification } from "./notifications";

export interface NotificationPrefs {
  sound: boolean;
  desktop: boolean;
  /** Severity filter — only fire sound/desktop when this severity is in the set. */
  severityFilter: Record<Notification["type"], boolean>;
}

const KEY = "tmq.notification.prefs";

const DEFAULT: NotificationPrefs = {
  sound: true,
  desktop: false, // requires permission grant; opt-in
  severityFilter: { error: true, warning: true, success: false, info: false },
};

function read(): NotificationPrefs {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return DEFAULT;
    const parsed = JSON.parse(raw) as Partial<NotificationPrefs>;
    return {
      ...DEFAULT,
      ...parsed,
      severityFilter: { ...DEFAULT.severityFilter, ...(parsed.severityFilter ?? {}) },
    };
  } catch {
    return DEFAULT;
  }
}

function write(prefs: NotificationPrefs): void {
  try {
    localStorage.setItem(KEY, JSON.stringify(prefs));
    listeners.forEach((fn) => fn(prefs));
  } catch {
    /* ignore */
  }
}

const listeners: Array<(prefs: NotificationPrefs) => void> = [];

export const notificationPrefsStore = {
  get: read,
  set: write,
  patch(patch: Partial<NotificationPrefs>): void {
    write({ ...read(), ...patch });
  },
  setSeverity(severity: Notification["type"], on: boolean): void {
    const cur = read();
    write({
      ...cur,
      severityFilter: { ...cur.severityFilter, [severity]: on },
    });
  },
  subscribe(fn: (prefs: NotificationPrefs) => void): () => void {
    listeners.push(fn);
    return () => {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    };
  },
};

// === Wire-up — once-per-session installer ===

let installed = false;
const FREQS: Record<Notification["type"], number> = {
  error: 320,
  warning: 480,
  success: 660,
  info: 540,
};

/**
 * Mounts the side-effects: when a notification fires, optionally play a beep
 * and show a desktop notification (if permission granted). Should be called
 * once at app start (we mount it from main.tsx via this module's import).
 */
export function installNotificationSideEffects(): () => void {
  if (installed) return () => {};
  installed = true;

  return subscribeToNotifications((n) => {
    const prefs = read();
    if (!prefs.severityFilter[n.type]) return;

    if (prefs.sound) {
      try {
        beep(FREQS[n.type], n.type === "error" ? 220 : 140);
      } catch {
        /* ignore audio context failures */
      }
    }

    if (prefs.desktop && typeof Notification !== "undefined" && Notification.permission === "granted") {
      try {
        new Notification(n.title, { body: n.body, silent: !prefs.sound });
      } catch {
        /* ignore */
      }
    }
  });
}

let audioCtx: AudioContext | null = null;
function beep(frequency: number, durationMs: number) {
  if (typeof window === "undefined") return;
  if (!audioCtx) {
    const Ctor = (window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext);
    if (!Ctor) return;
    audioCtx = new Ctor();
  }
  const ctx = audioCtx;
  if (ctx.state === "suspended") ctx.resume().catch(() => {});

  const osc = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.type = "sine";
  osc.frequency.value = frequency;
  gain.gain.value = 0.0001;
  osc.connect(gain);
  gain.connect(ctx.destination);
  const now = ctx.currentTime;
  // Quick fade-in/out so it doesn't click
  gain.gain.exponentialRampToValueAtTime(0.18, now + 0.012);
  gain.gain.exponentialRampToValueAtTime(0.0001, now + durationMs / 1000);
  osc.start(now);
  osc.stop(now + durationMs / 1000 + 0.02);
}

export async function requestDesktopPermission(): Promise<NotificationPermission> {
  if (typeof Notification === "undefined") return "denied";
  if (Notification.permission === "granted") return "granted";
  if (Notification.permission === "denied") return "denied";
  return Notification.requestPermission();
}
