import { useEffect, useMemo, useState } from "react";
import type { PageCustomizationApi } from "@/hooks/usePageCustomization";
import { cn } from "@/lib/utils";

interface Props {
  customization: PageCustomizationApi;
  /** Optional title shown in the drawer header. */
  pageLabel?: string;
}

/**
 * Bottom-of-page strip that opens a drawer for toggling individual panels.
 * Default template is preserved unless the operator explicitly opts out.
 *
 *   <CustomizeBar customization={c} pageLabel="Dashboard · Trading" />
 */
export function CustomizeBar({ customization, pageLabel }: Props) {
  const [open, setOpen] = useState(false);
  const { visibleCount, totalCount, isCustomized, widgets, isVisible, toggle, reset } =
    customization;

  // Esc closes drawer
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open]);

  // Group by category
  const grouped = useMemo(() => {
    const out: Record<string, typeof widgets> = {};
    for (const w of widgets) {
      const cat = w.category ?? "Panels";
      if (!out[cat]) out[cat] = [];
      out[cat].push(w);
    }
    return out;
  }, [widgets]);

  return (
    <>
      <div className="rounded-xl border border-white/5 bg-[#0D1117]/40 backdrop-blur-xl px-4 py-2.5 flex items-center justify-between gap-3 flex-wrap">
        <div className="flex items-center gap-3 text-[11px] text-muted-foreground/70">
          <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
            <path
              d="M3 4h10M3 8h7M3 12h10"
              stroke="currentColor"
              strokeWidth="1.4"
              strokeLinecap="round"
            />
            <circle cx="13" cy="8" r="1.2" fill="currentColor" />
          </svg>
          <span className="font-mono">
            {visibleCount} / {totalCount} panels visible
            {isCustomized && (
              <span className="ml-2 text-accent/70 italic">· customized</span>
            )}
          </span>
        </div>
        <div className="flex items-center gap-2">
          {isCustomized && (
            <button
              onClick={() => reset()}
              className="text-[10px] uppercase tracking-widest text-muted-foreground/50 hover:text-foreground transition-colors"
            >
              Reset to default
            </button>
          )}
          <button
            onClick={() => setOpen(true)}
            className="px-3 py-1 rounded-md text-[10px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors flex items-center gap-1.5"
          >
            <svg width="11" height="11" viewBox="0 0 16 16" fill="none">
              <circle cx="8" cy="8" r="1.5" fill="currentColor" />
              <circle cx="8" cy="3" r="1.5" fill="currentColor" />
              <circle cx="8" cy="13" r="1.5" fill="currentColor" />
            </svg>
            Customize
          </button>
        </div>
      </div>

      {/* Drawer */}
      {open && (
        <div
          role="dialog"
          aria-modal="true"
          aria-label="Customize page panels"
          className="fixed inset-0 z-[180] flex justify-end bg-black/40 backdrop-blur-sm animate-in fade-in duration-100"
          onClick={() => setOpen(false)}
        >
          <div
            onClick={(e) => e.stopPropagation()}
            className="w-full max-w-md h-full bg-[#0A0E14]/98 backdrop-blur-xl border-l border-white/10 shadow-2xl overflow-hidden flex flex-col animate-in slide-in-from-right duration-200"
          >
            {/* Header */}
            <div className="px-5 py-4 border-b border-white/5">
              <div className="flex items-baseline justify-between mb-1">
                <p className="text-[10px] font-black uppercase tracking-[0.3em] text-accent/80">
                  Customize Page
                </p>
                <button
                  onClick={() => setOpen(false)}
                  aria-label="Close customize drawer"
                  className="text-muted-foreground/50 hover:text-foreground"
                >
                  <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
                    <path d="M2 2l10 10M12 2L2 12" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
                  </svg>
                </button>
              </div>
              <h3 className="font-serif text-[20px] tracking-tight">
                {pageLabel ?? "This page"}
              </h3>
              <p className="text-[12px] font-serif italic text-muted-foreground/70 mt-1">
                Toggle individual panels off to declutter. The default template is
                always one click away — preferences are saved per device.
              </p>
              <div className="mt-3 flex items-center gap-2 text-[10px] font-mono">
                <span className="text-muted-foreground/60">
                  {visibleCount}/{totalCount} visible
                </span>
                {isCustomized && (
                  <button
                    onClick={() => reset()}
                    className="px-2 py-0.5 rounded text-[9px] uppercase tracking-widest border border-white/10 text-muted-foreground hover:text-foreground hover:bg-white/[0.05] transition-colors"
                  >
                    Reset to default
                  </button>
                )}
              </div>
            </div>

            {/* Widget list */}
            <div className="flex-1 overflow-y-auto">
              {Object.entries(grouped).map(([cat, items]) => (
                <div key={cat} className="border-b border-white/[0.04] last:border-0">
                  <div className="px-5 py-2 bg-white/[0.02]">
                    <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/50">
                      {cat}
                    </p>
                  </div>
                  {items.map((w) => {
                    const visible = isVisible(w.id);
                    return (
                      <div
                        key={w.id}
                        className={cn(
                          "px-5 py-3 flex items-start justify-between gap-3 hover:bg-white/[0.02] transition-colors",
                          !visible && "opacity-60"
                        )}
                      >
                        <div className="min-w-0 flex-1">
                          <div className="flex items-center gap-2">
                            <p className="text-[13px] font-bold">{w.label}</p>
                            {w.required && (
                              <span className="px-1.5 py-0.5 rounded text-[8px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/30">
                                Required
                              </span>
                            )}
                          </div>
                          <p className="text-[11px] font-serif italic text-muted-foreground/65 mt-0.5 leading-snug">
                            {w.description}
                          </p>
                        </div>
                        <button
                          role="switch"
                          aria-checked={visible}
                          aria-label={`${visible ? "Hide" : "Show"} ${w.label}`}
                          disabled={w.required}
                          onClick={() => toggle(w.id)}
                          className={cn(
                            "relative w-9 h-5 rounded-full transition-colors shrink-0 mt-1",
                            visible ? "bg-accent/40" : "bg-white/10",
                            w.required && "cursor-not-allowed opacity-50"
                          )}
                        >
                          <span
                            className={cn(
                              "absolute top-0.5 w-4 h-4 rounded-full transition-all",
                              visible ? "left-[18px] bg-accent" : "left-0.5 bg-muted-foreground/60"
                            )}
                          />
                        </button>
                      </div>
                    );
                  })}
                </div>
              ))}
            </div>

            {/* Footer */}
            <div className="px-5 py-3 border-t border-white/5 text-[10px] font-mono text-muted-foreground/50 flex items-center justify-between">
              <span>Esc to close</span>
              <span className="italic font-serif">preferences saved locally</span>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
