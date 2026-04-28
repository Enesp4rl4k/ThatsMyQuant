import { cn } from "@/lib/utils";

/**
 * Claude-style page header. Use as the top of any page:
 *
 *   <PageHeader
 *     eyebrow="Validated Edges"
 *     title="Alpha Portfolio"
 *     tagline="A 6-position Kelly portfolio combining 3 cointegrated pairs and 3 funding-alpha legs."
 *   >
 *     <ExportReportButton />
 *   </PageHeader>
 *
 * Children render to the right (action area).
 */
export function PageHeader({
  eyebrow,
  title,
  tagline,
  children,
  className,
}: {
  eyebrow?: string;
  title: string;
  tagline?: string;
  children?: React.ReactNode;
  className?: string;
}) {
  return (
    <header
      className={cn(
        "flex items-start justify-between gap-6 flex-wrap pb-2",
        className
      )}
    >
      <div className="min-w-0 flex-1">
        {eyebrow && (
          <p className="text-[10px] uppercase tracking-[0.28em] text-accent font-medium mb-2">
            {eyebrow}
          </p>
        )}
        <h1 className="font-serif text-[34px] leading-[1.1] tracking-tight text-foreground">
          {title}
        </h1>
        {tagline && (
          <p className="font-serif italic text-[15px] text-muted-foreground/80 mt-2 max-w-2xl leading-relaxed">
            {tagline}
          </p>
        )}
      </div>
      {children && <div className="shrink-0 flex items-center gap-2">{children}</div>}
    </header>
  );
}
