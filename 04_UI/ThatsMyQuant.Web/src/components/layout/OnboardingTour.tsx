import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { cn } from "@/lib/utils";

const STORAGE_KEY = "tmq.onboarding.completed";

interface Step {
  title: string;
  body: string;
  cta?: { label: string; to: string };
}

const STEPS: Step[] = [
  {
    title: "Welcome to ThatsMyQuant.",
    body:
      "A quantitative trading console — paper-grade by default, live-ready when you flip the switch. Five minutes to get oriented; you'll be productive on day one.",
  },
  {
    title: "Dashboard — your daily glance",
    body:
      "Open every morning. The Live Risk banner up top tells you the book's exposure in one row. The Daily Brief reads the tape in three paragraphs. Tabs (Risk / Performance / Operations) live below — only switch when you need depth.",
    cta: { label: "Open Dashboard", to: "/" },
  },
  {
    title: "Markets — the trade terminal",
    body:
      "Watchlist on the left, candles in the middle, bot status on the right. Press ⌘K to switch symbols instantly. Pin a symbol with the ○ button to open a floating mini-chart that follows you between pages.",
    cta: { label: "Open Markets", to: "/markets" },
  },
  {
    title: "Pro Mode — the bot's home",
    body:
      "All bot controls live here — single source of truth. ARM to start scanning, DISARM to pause, KILL switch to flatten everything. The Topbar's Quick Actions menu has the panic buttons too.",
    cta: { label: "Open Pro Mode", to: "/pro-mode" },
  },
  {
    title: "Keyboard mastery",
    body:
      "Press ? anytime for the full shortcut overlay. G then a letter (D/M/T/A/P/F/H/U/S) jumps between pages. R refreshes data. ⌘K is the universal symbol switcher.",
  },
];

/**
 * One-time onboarding modal. Shows on first visit; "Skip" or "Done" sets a
 * localStorage flag so it never re-appears. Operator can reopen via the
 * "Restart tour" link inside Settings.
 */
export function OnboardingTour() {
  const [open, setOpen] = useState(false);
  const [step, setStep] = useState(0);

  useEffect(() => {
    try {
      if (!localStorage.getItem(STORAGE_KEY)) {
        // Defer 800ms so first paint settles before the modal jumps in
        const t = setTimeout(() => setOpen(true), 800);
        return () => clearTimeout(t);
      }
    } catch {
      /* ignore */
    }
  }, []);

  // Listen for manual restart event
  useEffect(() => {
    const handler = () => {
      setStep(0);
      setOpen(true);
    };
    window.addEventListener("tmq:restart-onboarding", handler);
    return () => window.removeEventListener("tmq:restart-onboarding", handler);
  }, []);

  const finish = () => {
    try {
      localStorage.setItem(STORAGE_KEY, new Date().toISOString());
    } catch {
      /* ignore */
    }
    setOpen(false);
  };

  if (!open) return null;
  const current = STEPS[step];
  const isLast = step === STEPS.length - 1;

  return (
    <div
      role="dialog"
      aria-modal="true"
      className="fixed inset-0 z-[220] flex items-center justify-center p-6 bg-black/60 backdrop-blur-sm"
    >
      <div className="w-full max-w-md rounded-xl border border-accent/20 bg-[#0A0E14]/95 backdrop-blur-xl shadow-paper-lg overflow-hidden">
        {/* Step indicator */}
        <div className="px-5 pt-4 flex items-center justify-between">
          <div className="flex items-center gap-1">
            {STEPS.map((_, i) => (
              <span
                key={i}
                className={cn(
                  "h-1 rounded-full transition-all",
                  i === step ? "w-8 bg-accent" : i < step ? "w-4 bg-accent/50" : "w-4 bg-white/10"
                )}
              />
            ))}
          </div>
          <button
            onClick={finish}
            className="text-[10px] uppercase tracking-widest text-muted-foreground/50 hover:text-foreground transition-colors"
          >
            Skip
          </button>
        </div>

        {/* Content */}
        <div className="px-6 pt-6 pb-2 min-h-[180px]">
          <p className="text-[10px] uppercase tracking-[0.3em] text-accent/70 font-bold mb-2">
            Step {step + 1} of {STEPS.length}
          </p>
          <h3 className="font-serif text-[24px] leading-tight tracking-tight mb-3">
            {current.title}
          </h3>
          <p className="font-serif text-[14px] leading-relaxed text-foreground/80">
            {current.body}
          </p>
          {current.cta && (
            <Link
              to={current.cta.to}
              onClick={finish}
              className="mt-4 inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors text-[11px] font-black uppercase tracking-widest"
            >
              {current.cta.label}
              <svg width="11" height="11" viewBox="0 0 16 16" fill="none">
                <path
                  d="M3 8h10M9 4l4 4-4 4"
                  stroke="currentColor"
                  strokeWidth="1.4"
                  strokeLinecap="round"
                />
              </svg>
            </Link>
          )}
        </div>

        {/* Footer */}
        <div className="px-5 py-3 border-t border-white/5 flex items-center justify-between">
          <button
            onClick={() => setStep((s) => Math.max(0, s - 1))}
            disabled={step === 0}
            className={cn(
              "text-[11px] font-mono uppercase tracking-widest transition-colors",
              step === 0
                ? "text-muted-foreground/20 cursor-not-allowed"
                : "text-muted-foreground/60 hover:text-foreground"
            )}
          >
            ← Back
          </button>
          <button
            onClick={() => (isLast ? finish() : setStep((s) => s + 1))}
            className="px-3 py-1.5 rounded-md bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors text-[11px] font-black uppercase tracking-widest"
          >
            {isLast ? "Done" : "Next →"}
          </button>
        </div>
      </div>
    </div>
  );
}

/** Helper for Settings page to trigger a re-tour. */
export function restartOnboarding() {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* ignore */
  }
  window.dispatchEvent(new Event("tmq:restart-onboarding"));
}
