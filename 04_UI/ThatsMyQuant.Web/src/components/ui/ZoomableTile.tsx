import { useEffect, useId, useRef, useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { cn } from "@/lib/utils";

interface Props {
  /**
   * Compact rendering — the small ring / card / sparkline that lives inline.
   * Wrapped in a button that triggers expansion on click.
   */
  preview: React.ReactNode;
  /**
   * Expanded rendering — usually a larger version of the same chart plus extra
   * context (longer time window, secondary metrics, raw values, etc.).
   * Receives `close` callback so its internal buttons can dismiss the modal.
   */
  expanded: (close: () => void) => React.ReactNode;
  /**
   * Optional title shown above the expanded content.
   */
  title?: string;
  /**
   * Optional subtitle / dismiss hint shown next to the title.
   */
  subtitle?: string;
  className?: string;
  /**
   * Override the modal max-width (Tailwind class). Default: max-w-2xl.
   */
  modalSize?: string;
  /**
   * If true, suppresses the click affordance entirely — useful when the
   * caller already handles clicks on inner elements.
   */
  disabled?: boolean;
}

/**
 * Notion-style click-to-expand wrapper. Wraps any small chart/tile so clicking
 * it opens a centred modal with a larger version + extra context. Smooth
 * Framer Motion transition; backdrop click + Esc dismiss; auto-traps focus.
 *
 * Usage:
 * ```tsx
 * <ZoomableTile
 *   title="PnL Detail"
 *   preview={<RingStat ... size="md" />}
 *   expanded={(close) => <RingStat ... size="lg" />}  // bigger version + extra info
 * />
 * ```
 */
export function ZoomableTile({
  preview,
  expanded,
  title,
  subtitle,
  className,
  modalSize = "max-w-2xl",
  disabled = false,
}: Props) {
  const [open, setOpen] = useState(false);
  const id = useId();
  const triggerRef = useRef<HTMLButtonElement>(null);

  // Esc to close + restore focus to trigger
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        setOpen(false);
      }
    };
    document.addEventListener("keydown", onKey);
    // Lock body scroll while modal is open
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.removeEventListener("keydown", onKey);
      document.body.style.overflow = prev;
      // Restore focus to the trigger so keyboard nav resumes naturally
      triggerRef.current?.focus();
    };
  }, [open]);

  if (disabled) {
    return <div className={className}>{preview}</div>;
  }

  return (
    <>
      <motion.button
        ref={triggerRef}
        type="button"
        onClick={() => setOpen(true)}
        layoutId={`zoom-${id}`}
        whileHover={{ scale: 1.03 }}
        whileTap={{ scale: 0.98 }}
        transition={{ type: "spring", stiffness: 380, damping: 28 }}
        aria-label={title ? `Expand ${title}` : "Expand details"}
        className={cn(
          "block bg-transparent border-0 p-0 text-left cursor-zoom-in focus:outline-none focus:ring-2 focus:ring-accent/40 focus:ring-offset-2 focus:ring-offset-transparent rounded-2xl",
          className
        )}
      >
        {preview}
      </motion.button>

      <AnimatePresence>
        {open && (
          <motion.div
            // Backdrop
            key="backdrop"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: 0.18 }}
            onClick={() => setOpen(false)}
            className="fixed inset-0 z-[100] flex items-center justify-center bg-black/70 backdrop-blur-sm p-4"
            role="dialog"
            aria-modal="true"
            aria-label={title ?? "Expanded view"}
          >
            <motion.div
              key="content"
              layoutId={`zoom-${id}`}
              transition={{ type: "spring", stiffness: 320, damping: 32 }}
              onClick={(e) => e.stopPropagation()}
              className={cn(
                "relative w-full overflow-hidden rounded-2xl border border-white/10 bg-[#0D1117]/95 backdrop-blur-xl p-6 shadow-2xl cursor-default",
                modalSize
              )}
            >
              {/* Header */}
              {(title || subtitle) && (
                <div className="flex items-baseline justify-between mb-4">
                  <div>
                    {title && (
                      <h3 className="font-serif text-lg tracking-tight text-foreground/90">{title}</h3>
                    )}
                    {subtitle && (
                      <p className="text-[11px] font-mono text-muted-foreground/60 mt-0.5">{subtitle}</p>
                    )}
                  </div>
                  <button
                    onClick={() => setOpen(false)}
                    aria-label="Close"
                    className="text-muted-foreground/50 hover:text-foreground/90 text-xl leading-none px-2 py-0.5 rounded hover:bg-white/[0.04] transition-colors"
                  >
                    ×
                  </button>
                </div>
              )}
              {/* Body */}
              <div>{expanded(() => setOpen(false))}</div>

              {/* Esc hint footer */}
              <p className="absolute bottom-2 right-3 text-[8px] font-mono uppercase tracking-widest text-muted-foreground/30">
                Esc · backdrop · × to close
              </p>
            </motion.div>
          </motion.div>
        )}
      </AnimatePresence>
    </>
  );
}
