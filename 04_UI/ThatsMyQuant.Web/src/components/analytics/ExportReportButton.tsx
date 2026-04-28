import { cn } from "@/lib/utils";

/**
 * Triggers `window.print()`. The print stylesheet in `index.css` strips chrome
 * (sidebar, topbar, anything tagged `data-print="hide"`) and forces white-on-black
 * → black-on-white so the result reads cleanly as a PDF (Save as PDF in the print dialog).
 */
export function ExportReportButton({
  className,
  label = "Export Report",
}: {
  className?: string;
  label?: string;
}) {
  return (
    <button
      data-print="hide"
      onClick={() => window.print()}
      className={cn(
        "flex items-center gap-2 px-3 py-1.5 rounded-md text-[10px] font-black uppercase tracking-widest",
        "bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors",
        className
      )}
      title="Open the print dialog (Save as PDF for a portable report)"
    >
      <svg width="12" height="12" viewBox="0 0 16 16" fill="none">
        <path
          d="M4 13H3a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-1M4 6V3a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v3M4 10h8v4H4z"
          stroke="currentColor"
          strokeWidth="1.4"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
      {label}
    </button>
  );
}
