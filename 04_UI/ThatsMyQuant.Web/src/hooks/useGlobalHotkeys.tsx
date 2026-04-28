import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";

/** Single source of truth for the keyboard shortcut palette. */
export interface Shortcut {
  key: string;
  description: string;
  category: "Navigation" | "Action" | "Help";
}

export const SHORTCUTS: Shortcut[] = [
  { key: "G then D", description: "Go to Dashboard", category: "Navigation" },
  { key: "G then M", description: "Go to Markets", category: "Navigation" },
  { key: "G then T", description: "Go to Trade Journal", category: "Navigation" },
  { key: "G then A", description: "Go to Alpha Portfolio", category: "Navigation" },
  { key: "G then P", description: "Go to Pro Mode", category: "Navigation" },
  { key: "G then F", description: "Go to Funding Monitor", category: "Navigation" },
  { key: "G then H", description: "Go to System Health", category: "Navigation" },
  { key: "G then U", description: "Go to Audit Log", category: "Navigation" },
  { key: "G then S", description: "Go to Settings", category: "Navigation" },
  { key: "R", description: "Refresh all data (refetch queries)", category: "Action" },
  { key: "/ ", description: "Focus the first form field", category: "Action" },
  { key: "?", description: "Show this keyboard shortcuts overlay", category: "Help" },
  { key: "Esc", description: "Close any open dialog / overlay", category: "Help" },
];

const NAV_MAP: Record<string, string> = {
  d: "/",
  m: "/markets",
  t: "/trades",
  a: "/alpha",
  p: "/pro-mode",
  f: "/funding",
  h: "/health",
  u: "/audit",
  s: "/settings",
};

/** Returns whether the active element is a typing target (input/textarea/select/contenteditable). */
function isTypingTarget(): boolean {
  const el = document.activeElement as HTMLElement | null;
  if (!el) return false;
  const tag = el.tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
  if (el.isContentEditable) return true;
  return false;
}

/**
 * Mounts global key listeners. Returns the openShortcuts state + close fn so
 * the AppShell can render the overlay.
 */
export function useGlobalHotkeys() {
  const navigate = useNavigate();
  const qc = useQueryClient();
  const [showHelp, setShowHelp] = useState(false);
  const [pendingG, setPendingG] = useState(false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      // ignore while typing — let normal text input behave
      if (isTypingTarget()) return;
      if (e.metaKey || e.ctrlKey || e.altKey) return;

      const k = e.key.toLowerCase();

      // Esc closes the help overlay (also handled inside the overlay itself)
      if (e.key === "Escape") {
        if (showHelp) {
          setShowHelp(false);
          e.preventDefault();
        }
        if (pendingG) setPendingG(false);
        return;
      }

      // ? opens the help overlay (Shift+/ on most layouts)
      if (e.key === "?") {
        e.preventDefault();
        setShowHelp((v) => !v);
        return;
      }

      // G-prefix navigation chord
      if (pendingG) {
        const target = NAV_MAP[k];
        setPendingG(false);
        if (target) {
          e.preventDefault();
          navigate(target);
        }
        return;
      }

      if (k === "g") {
        setPendingG(true);
        // Auto-clear the pending state after 1.5s if no follow-up
        setTimeout(() => setPendingG((p) => (p ? false : p)), 1500);
        return;
      }

      // R refreshes all queries
      if (k === "r") {
        e.preventDefault();
        qc.invalidateQueries();
        return;
      }

      // / focuses the first input on the page
      if (e.key === "/") {
        const first = document.querySelector<HTMLElement>(
          "input:not([type=hidden]):not([disabled]), select:not([disabled]), textarea:not([disabled])"
        );
        if (first) {
          e.preventDefault();
          first.focus();
        }
      }
    };

    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [navigate, qc, pendingG, showHelp]);

  return {
    showHelp,
    openHelp: () => setShowHelp(true),
    closeHelp: () => setShowHelp(false),
    pendingG,
  };
}
