import { useEffect, useRef, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";

interface Props {
  /** Widths in pixels for the side columns; centre is `flex-1`. Persisted by `storageKey`. */
  initial: { left: number; right: number };
  min?: { left: number; right: number };
  max?: { left: number; right: number };
  storageKey: string;
  left: ReactNode;
  center: ReactNode;
  right: ReactNode;
}

/**
 * 3-column resizable row — left | drag-handle | center | drag-handle | right.
 * Drag a handle to resize the adjacent column; widths persist to localStorage
 * under `storageKey`. Below `lg` breakpoint we collapse to a stacked layout
 * (handles disabled).
 */
export function ResizableRow({
  initial,
  min = { left: 200, right: 220 },
  max = { left: 480, right: 520 },
  storageKey,
  left,
  center,
  right,
}: Props) {
  const [widths, setWidths] = useState<{ left: number; right: number }>(() => {
    try {
      const raw = localStorage.getItem(storageKey);
      if (raw) {
        const p = JSON.parse(raw) as { left: number; right: number };
        if (typeof p.left === "number" && typeof p.right === "number") return p;
      }
    } catch {
      /* ignore */
    }
    return initial;
  });

  const dragRef = useRef<{
    side: "left" | "right";
    startX: number;
    startWidth: number;
  } | null>(null);

  useEffect(() => {
    try {
      localStorage.setItem(storageKey, JSON.stringify(widths));
    } catch {
      /* ignore */
    }
  }, [widths, storageKey]);

  useEffect(() => {
    function onMove(e: MouseEvent) {
      const d = dragRef.current;
      if (!d) return;
      const dx = e.clientX - d.startX;
      if (d.side === "left") {
        const next = Math.max(min.left, Math.min(max.left, d.startWidth + dx));
        setWidths((w) => ({ ...w, left: next }));
      } else {
        const next = Math.max(min.right, Math.min(max.right, d.startWidth - dx));
        setWidths((w) => ({ ...w, right: next }));
      }
    }
    function onUp() {
      dragRef.current = null;
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
    }
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
  }, [min, max]);

  const startDrag = (side: "left" | "right") => (e: React.MouseEvent) => {
    e.preventDefault();
    dragRef.current = {
      side,
      startX: e.clientX,
      startWidth: side === "left" ? widths.left : widths.right,
    };
    document.body.style.cursor = "col-resize";
    document.body.style.userSelect = "none";
  };

  return (
    <div className="block lg:flex gap-3 lg:gap-0 min-h-[400px]">
      {/* Stacked on mobile/tablet (< lg) */}
      <div className="lg:hidden space-y-3 w-full">
        {left}
        {center}
        {right}
      </div>

      {/* Resizable row at lg and up */}
      <div
        className="hidden lg:flex gap-0 w-full"
        style={{ minHeight: 0 }}
      >
        <div style={{ width: widths.left, flexShrink: 0 }}>{left}</div>
        <Splitter onMouseDown={startDrag("left")} />
        <div className="flex-1 min-w-0">{center}</div>
        <Splitter onMouseDown={startDrag("right")} />
        <div style={{ width: widths.right, flexShrink: 0 }}>{right}</div>
      </div>
    </div>
  );
}

function Splitter({ onMouseDown }: { onMouseDown: (e: React.MouseEvent) => void }) {
  return (
    <div
      onMouseDown={onMouseDown}
      role="separator"
      aria-orientation="vertical"
      className={cn(
        "shrink-0 w-3 cursor-col-resize flex items-center justify-center group",
        "before:content-[''] before:w-px before:h-full before:bg-white/5",
        "hover:before:bg-accent/50 active:before:bg-accent transition-colors"
      )}
    >
      <span className="opacity-0 group-hover:opacity-100 text-[8px] font-mono text-accent/60 transition-opacity">
        ⋮
      </span>
    </div>
  );
}
