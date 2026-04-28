interface ScatterProps {
  points: { x: number; y: number; pnl: number; label?: string }[];
  width?: number;
  height?: number;
  xLabel?: string;
  yLabel?: string;
}

export function Scatter({
  points,
  width = 800,
  height = 280,
  xLabel = "MFE %",
  yLabel = "MAE %",
}: ScatterProps) {
  if (points.length === 0) {
    return (
      <div className="flex items-center justify-center text-xs text-muted-foreground" style={{ width, height }}>
        No data
      </div>
    );
  }

  const padding = { top: 16, right: 12, bottom: 30, left: 48 };
  const innerW = width - padding.left - padding.right;
  const innerH = height - padding.top - padding.bottom;
  const xMin = Math.min(...points.map((p) => p.x), 0);
  const xMax = Math.max(...points.map((p) => p.x), 0);
  const yMin = Math.min(...points.map((p) => p.y), 0);
  const yMax = Math.max(...points.map((p) => p.y), 0);
  const xRange = xMax - xMin || 1;
  const yRange = yMax - yMin || 1;

  const xOf = (v: number) => padding.left + ((v - xMin) / xRange) * innerW;
  const yOf = (v: number) => padding.top + innerH - ((v - yMin) / yRange) * innerH;

  return (
    <svg width={width} height={height} role="img">
      {/* axes */}
      <line x1={padding.left} y1={padding.top} x2={padding.left} y2={padding.top + innerH} stroke="rgba(255,255,255,0.15)" strokeWidth={0.6} />
      <line x1={padding.left} y1={padding.top + innerH} x2={padding.left + innerW} y2={padding.top + innerH} stroke="rgba(255,255,255,0.15)" strokeWidth={0.6} />
      {/* axis labels */}
      <text x={padding.left + innerW / 2} y={height - 6} textAnchor="middle" fontSize={10} fill="rgba(255,255,255,0.6)" fontFamily="monospace">
        {xLabel}
      </text>
      <text
        x={-(padding.top + innerH / 2)}
        y={14}
        transform={`rotate(-90)`}
        textAnchor="middle"
        fontSize={10}
        fill="rgba(255,255,255,0.6)"
        fontFamily="monospace"
      >
        {yLabel}
      </text>
      {/* dots */}
      {points.map((p, i) => {
        const cx = xOf(p.x);
        const cy = yOf(p.y);
        const fill = p.pnl > 0 ? "rgba(52, 211, 153, 0.85)" : "rgba(244, 63, 94, 0.85)";
        return (
          <circle key={i} cx={cx} cy={cy} r={3} fill={fill} stroke="rgba(0,0,0,0.6)" strokeWidth={0.4}>
            <title>{p.label ?? ""} pnl={p.pnl.toFixed(2)} mfe={p.x.toFixed(2)}% mae={p.y.toFixed(2)}%</title>
          </circle>
        );
      })}
    </svg>
  );
}
