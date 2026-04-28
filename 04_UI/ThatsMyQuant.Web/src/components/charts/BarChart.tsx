interface BarChartProps {
  bins: { binStart: number; binEnd: number; count: number }[];
  width?: number;
  height?: number;
  barColor?: string;
  highlightBelow?: number;
  highlightColor?: string;
}

export function BarChart({
  bins,
  width = 800,
  height = 200,
  barColor = "rgba(30, 58, 138, 0.7)",
  highlightBelow,
  highlightColor = "rgba(244, 63, 94, 0.85)",
}: BarChartProps) {
  if (bins.length === 0) {
    return (
      <div className="flex items-center justify-center text-xs text-muted-foreground" style={{ width, height }}>
        No data
      </div>
    );
  }
  const padding = { top: 10, right: 12, bottom: 24, left: 36 };
  const innerW = width - padding.left - padding.right;
  const innerH = height - padding.top - padding.bottom;
  const maxCount = Math.max(...bins.map((b) => b.count), 1);
  const barWidth = innerW / bins.length;

  return (
    <svg width={width} height={height} role="img">
      {/* y-axis ticks */}
      {[0, 0.25, 0.5, 0.75, 1].map((t, i) => {
        const y = padding.top + innerH * (1 - t);
        return (
          <g key={i}>
            <line x1={padding.left} y1={y} x2={padding.left + innerW} y2={y} stroke="rgba(255,255,255,0.06)" strokeWidth={0.5} />
            <text x={padding.left - 6} y={y + 3} textAnchor="end" fontSize={9} fill="rgba(255,255,255,0.5)" fontFamily="monospace">
              {Math.round(maxCount * t)}
            </text>
          </g>
        );
      })}
      {bins.map((b, i) => {
        const h = (b.count / maxCount) * innerH;
        const x = padding.left + i * barWidth + 1;
        const y = padding.top + innerH - h;
        const center = (b.binStart + b.binEnd) / 2;
        const color = highlightBelow != null && center < highlightBelow ? highlightColor : barColor;
        return (
          <g key={i}>
            <rect x={x} y={y} width={Math.max(0, barWidth - 2)} height={h} fill={color} rx={1} />
            {(i === 0 || i === bins.length - 1) && (
              <text
                x={x + barWidth / 2}
                y={padding.top + innerH + 12}
                textAnchor="middle"
                fontSize={9}
                fill="rgba(255,255,255,0.5)"
                fontFamily="monospace"
              >
                {(center * 100).toFixed(1)}%
              </text>
            )}
          </g>
        );
      })}
    </svg>
  );
}
