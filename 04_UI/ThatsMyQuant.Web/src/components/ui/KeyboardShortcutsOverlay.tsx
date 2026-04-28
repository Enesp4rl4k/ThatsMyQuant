import { useEffect } from "react";
import { SHORTCUTS, type Shortcut } from "@/hooks/useGlobalHotkeys";
import { cn } from "@/lib/utils";

export function KeyboardShortcutsOverlay({
  open,
  onClose,
}: {
  open: boolean;
  onClose: () => void;
}) {
  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  const grouped = SHORTCUTS.reduce<Record<string, Shortcut[]>>((acc, s) => {
    (acc[s.category] ||= []).push(s);
    return acc;
  }, {});

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="kbd-overlay-title"
      className="fixed inset-0 z-[200] flex items-center justify-center p-6 bg-black/60 backdrop-blur-sm animate-in fade-in duration-150"
      onClick={onClose}
    >
      <div
        className="w-full max-w-lg rounded-xl border border-white/10 bg-[#0A0E14]/95 backdrop-blur-xl shadow-2xl overflow-hidden"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-3 border-b border-white/5">
          <div>
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70">
              Keyboard Shortcuts
            </p>
            <p id="kbd-overlay-title" className="text-sm font-bold mt-0.5">
              Quick Reference
            </p>
          </div>
          <button
            onClick={onClose}
            aria-label="Close shortcuts overlay"
            className="text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.05] w-8 h-8 rounded-md flex items-center justify-center transition-colors"
          >
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
              <path d="M2 2l10 10M12 2L2 12" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            </svg>
          </button>
        </div>

        <div className="p-5 space-y-5 max-h-[70vh] overflow-y-auto">
          {Object.entries(grouped).map(([category, shortcuts]) => (
            <div key={category}>
              <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50 mb-2">
                {category}
              </p>
              <ul className="space-y-1.5">
                {shortcuts.map((s) => (
                  <li
                    key={s.key}
                    className="flex items-center justify-between gap-3 text-xs"
                  >
                    <span className="text-muted-foreground/80">{s.description}</span>
                    <KeyChord chord={s.key} />
                  </li>
                ))}
              </ul>
            </div>
          ))}
          <p className="pt-3 border-t border-white/5 text-[10px] text-muted-foreground/40 leading-relaxed">
            Tip: Most shortcuts are inactive while you&apos;re typing in an input. Press{" "}
            <kbd className="px-1.5 py-0.5 rounded text-[9px] bg-white/5 border border-white/10 font-mono">
              Esc
            </kbd>{" "}
            to close this overlay.
          </p>
        </div>
      </div>
    </div>
  );
}

function KeyChord({ chord }: { chord: string }) {
  const parts = chord.split(/\s+then\s+/i);
  return (
    <span className="flex items-center gap-1 shrink-0">
      {parts.map((p, i) => (
        <span key={i} className="flex items-center gap-1">
          <kbd
            className={cn(
              "px-2 py-0.5 rounded text-[10px] font-mono font-bold border",
              "bg-white/[0.04] border-white/10 text-foreground/90"
            )}
          >
            {p.trim()}
          </kbd>
          {i < parts.length - 1 && (
            <span className="text-[9px] text-muted-foreground/40">then</span>
          )}
        </span>
      ))}
    </span>
  );
}
