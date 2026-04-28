import { useEffect, useState } from "react";
import {
  notificationPrefsStore,
  requestDesktopPermission,
  type NotificationPrefs,
} from "@/lib/notificationPrefs";
import type { Notification } from "@/lib/notifications";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";

const SEVERITIES: { key: Notification["type"]; label: string; tone: string }[] = [
  { key: "error", label: "Error", tone: "text-rose-400" },
  { key: "warning", label: "Warning", tone: "text-amber-400" },
  { key: "success", label: "Success", tone: "text-emerald-400" },
  { key: "info", label: "Info", tone: "text-accent" },
];

export function NotificationPrefsCard() {
  const [prefs, setPrefs] = useState<NotificationPrefs>(() => notificationPrefsStore.get());
  const [perm, setPerm] = useState<NotificationPermission>(
    typeof Notification !== "undefined" ? Notification.permission : "denied"
  );

  useEffect(() => notificationPrefsStore.subscribe(setPrefs), []);

  const onEnableDesktop = async () => {
    const result = await requestDesktopPermission();
    setPerm(result);
    if (result === "granted") {
      notificationPrefsStore.patch({ desktop: true });
    }
  };

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl overflow-hidden">
      <div className="px-4 py-2.5 border-b border-white/5">
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          Alert Channels
        </p>
        <p className="text-[11px] font-serif italic text-muted-foreground/60">
          how operator alerts reach you
        </p>
      </div>

      <div className="p-4 space-y-3">
        {/* Sound */}
        <ToggleRow
          label="Sound"
          description="Play a short beep when an alert fires"
          on={prefs.sound}
          onChange={(v) => notificationPrefsStore.patch({ sound: v })}
        />

        {/* Desktop */}
        <div className="flex items-start justify-between gap-3 pb-3 border-b border-white/5">
          <div className="min-w-0">
            <p className="text-[12px] font-bold">Desktop notifications</p>
            <p className="text-[10px] font-mono italic text-muted-foreground/60">
              {perm === "granted"
                ? "permission granted · OS notifications active"
                : perm === "denied"
                  ? "permission denied · re-enable in browser settings"
                  : "permission not granted yet"}
            </p>
          </div>
          {perm === "granted" ? (
            <Toggle
              on={prefs.desktop}
              onChange={(v) => notificationPrefsStore.patch({ desktop: v })}
              label="Desktop notifications"
            />
          ) : (
            <button
              onClick={onEnableDesktop}
              disabled={perm === "denied"}
              className="px-2.5 py-1 rounded text-[9px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/40 hover:bg-accent/25 transition-colors disabled:opacity-40"
            >
              Enable
            </button>
          )}
        </div>

        {/* Severity filter */}
        <div>
          <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
            Trigger on severity
          </p>
          <div className="grid grid-cols-2 gap-1.5">
            {SEVERITIES.map((s) => (
              <button
                key={s.key}
                onClick={() => notificationPrefsStore.setSeverity(s.key, !prefs.severityFilter[s.key])}
                aria-pressed={prefs.severityFilter[s.key]}
                className={cn(
                  "flex items-center gap-2 px-2 py-1.5 rounded border transition-colors text-left",
                  prefs.severityFilter[s.key]
                    ? "border-white/20 bg-white/[0.04]"
                    : "border-white/5 bg-transparent text-muted-foreground/50"
                )}
              >
                <span
                  className={cn(
                    "w-2 h-2 rounded-full",
                    prefs.severityFilter[s.key] ? s.tone.replace("text-", "bg-") : "bg-muted-foreground/20"
                  )}
                />
                <span className={cn("text-[10px] font-mono uppercase tracking-widest", prefs.severityFilter[s.key] ? s.tone : "")}>
                  {s.label}
                </span>
              </button>
            ))}
          </div>
        </div>

        {/* Test */}
        <div className="pt-3 border-t border-white/5">
          <button
            onClick={() =>
              pushNotification({
                type: "warning",
                title: "🔔 Test alert",
                body: "If you heard a beep / saw an OS notification, your channels are working.",
              })
            }
            className="w-full py-1.5 rounded text-[10px] font-mono uppercase tracking-widest border border-white/10 bg-white/[0.02] text-muted-foreground hover:text-foreground hover:bg-white/[0.05] transition-colors"
          >
            Send test alert
          </button>
        </div>
      </div>
    </div>
  );
}

function ToggleRow({
  label,
  description,
  on,
  onChange,
}: {
  label: string;
  description: string;
  on: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <div className="flex items-start justify-between gap-3 pb-3 border-b border-white/5">
      <div className="min-w-0">
        <p className="text-[12px] font-bold">{label}</p>
        <p className="text-[10px] font-mono italic text-muted-foreground/60">{description}</p>
      </div>
      <Toggle on={on} onChange={onChange} label={label} />
    </div>
  );
}

function Toggle({
  on,
  onChange,
  label,
}: {
  on: boolean;
  onChange: (v: boolean) => void;
  label: string;
}) {
  return (
    <button
      role="switch"
      aria-checked={on}
      aria-label={label}
      onClick={() => onChange(!on)}
      className={cn(
        "relative w-9 h-5 rounded-full transition-colors shrink-0",
        on ? "bg-accent/40" : "bg-white/10"
      )}
    >
      <span
        className={cn(
          "absolute top-0.5 w-4 h-4 rounded-full transition-all",
          on ? "left-[18px] bg-accent" : "left-0.5 bg-muted-foreground/60"
        )}
      />
    </button>
  );
}
