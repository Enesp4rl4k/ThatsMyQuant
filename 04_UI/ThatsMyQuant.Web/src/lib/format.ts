const usdFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const compactUsdFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  notation: "compact",
  maximumFractionDigits: 1,
});

const percentFormatter = new Intl.NumberFormat("en-US", {
  style: "percent",
  minimumFractionDigits: 1,
  maximumFractionDigits: 2,
});

const decimalFormatter = new Intl.NumberFormat("en-US", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
});

export function formatUsd(value: number): string {
  if (!Number.isFinite(value)) return "—";
  return usdFormatter.format(value);
}

export function formatUsdCompact(value: number): string {
  if (!Number.isFinite(value)) return "—";
  return compactUsdFormatter.format(value);
}

export function formatPercent(fraction: number, fractionDigits = 2): string {
  if (!Number.isFinite(fraction)) return "—";
  return new Intl.NumberFormat("en-US", {
    style: "percent",
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: fractionDigits,
  }).format(fraction);
}

export function formatPercentLoose(fraction: number): string {
  if (!Number.isFinite(fraction)) return "—";
  return percentFormatter.format(fraction);
}

export function formatDecimal(value: number): string {
  if (!Number.isFinite(value)) return "—";
  return decimalFormatter.format(value);
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "—";
  return date.toLocaleString();
}

export function pnlColor(value: number): string {
  if (!Number.isFinite(value) || value === 0) return "text-muted-foreground";
  return value > 0 ? "text-emerald-400" : "text-rose-400";
}
