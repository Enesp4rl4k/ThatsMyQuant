import { cn } from "@/lib/utils";

interface StatCardProps {
  label: string;
  value: string;
  subValue?: string;
  accent?: "neutral" | "positive" | "negative" | "info";
}

const ACCENT_MAP: Record<NonNullable<StatCardProps["accent"]>, string> = {
  neutral: "text-foreground",
  positive: "text-emerald-400",
  negative: "text-rose-400",
  info: "text-accent",
};

export function StatCard({ label, value, subValue, accent = "neutral" }: StatCardProps) {
  return (
    <div className="group relative rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl px-5 py-4 overflow-hidden">
      <div className="absolute top-0 left-0 w-full h-px bg-gradient-to-r from-transparent via-accent/30 to-transparent opacity-50 group-hover:opacity-100 transition-opacity" />
      <p className="text-[9px] font-black uppercase tracking-[0.3em] text-muted-foreground/50 mb-2">
        {label}
      </p>
      <p className={cn("text-2xl font-mono font-bold tracking-tight leading-none", ACCENT_MAP[accent])}>
        {value}
      </p>
      {subValue && (
        <p className="text-[10px] font-medium text-muted-foreground/60 mt-2">{subValue}</p>
      )}
    </div>
  );
}
