import { useEffect, useState } from "react";
import { useTheme } from "next-themes";
import { cn } from "@/lib/utils";

/**
 * 3-way segmented theme toggle: **Dim** (warm dark, default) · **Noir** (pure
 * black, Notion-template aesthetic) · **Light** (paper).
 *
 * State managed by next-themes; persisted to localStorage. Returns a placeholder
 * div until mounted to avoid hydration flash.
 */

const OPTIONS = [
  { id: "dark",  label: "Dim",   title: "Dim — warm dark" },
  { id: "noir",  label: "Noir",  title: "Noir — pure-black trader mode" },
  { id: "light", label: "Light", title: "Light — paper" },
] as const;

export function ThemeToggle({ className }: { className?: string }) {
  const { theme, setTheme } = useTheme();
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);

  if (!mounted) {
    return <div className={cn("w-[160px] h-7", className)} aria-hidden />;
  }

  const active = theme ?? "dark";

  return (
    <div
      role="radiogroup"
      aria-label="Theme"
      className={cn(
        "hidden sm:inline-flex items-center gap-0.5 rounded-full p-0.5 border border-white/10 bg-white/[0.02]",
        className
      )}
    >
      {OPTIONS.map((opt) => {
        const isActive = active === opt.id;
        return (
          <button
            key={opt.id}
            role="radio"
            aria-checked={isActive}
            title={opt.title}
            onClick={() => setTheme(opt.id)}
            className={cn(
              "px-2.5 py-0.5 rounded-full text-[10px] font-mono uppercase tracking-widest transition-colors",
              isActive
                ? "bg-foreground/90 text-background font-bold"
                : "text-muted-foreground/60 hover:text-foreground"
            )}
          >
            {opt.label}
          </button>
        );
      })}
    </div>
  );
}
