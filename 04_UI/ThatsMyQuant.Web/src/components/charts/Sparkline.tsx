interface SparklineProps {
  values: number[];
  width?: number;
  height?: number;
  stroke?: string;
  strokeWidth?: number;
  fill?: string;
  zeroLine?: boolean;
}

export function Sparkline({
  values,
  width = 80,
  height = 24,
  stroke = "rgba(30, 58, 138, 0.9)",
  strokeWidth = 1.2,
  fill = "none",
  zeroLine = false,
}: SparklineProps) {
  if (!values || values.length < 2) {
    return (
      <svg width={width} height={height} className="opacity-30">
        <line x1={0} y1={height / 2} x2={width} y2={height / 2} stroke="currentColor" strokeWidth={0.5} strokeDasharray="2 2" />
      </svg>
    );
  }

  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min || 1;
  const stepX = width / (values.length - 1);
  const points = values
    .map((v, i) => `${(i * stepX).toFixed(2)},${(height - ((v - min) / range) * height).toFixed(2)}`)
    .join(" ");

  let zeroY: number | null = null;
  if (zeroLine && min < 0 && max > 0) {
    zeroY = height - ((0 - min) / range) * height;
  }

  return (
    <svg width={width} height={height}>
      {zeroY != null && (
        <line x1={0} y1={zeroY} x2={width} y2={zeroY} stroke="rgba(31,30,27,0.18)" className="dark:stroke-[rgba(255,250,235,0.2)]" strokeWidth={0.5} strokeDasharray="2 2" />
      )}
      <polyline points={points} stroke={stroke} strokeWidth={strokeWidth} fill={fill} strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}
