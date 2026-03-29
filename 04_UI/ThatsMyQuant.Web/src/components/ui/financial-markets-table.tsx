"use client";

import React, { useState, useEffect, useRef, useMemo, memo } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { useTheme } from "next-themes";
import * as signalR from "@microsoft/signalr";
import { cn } from "@/lib/utils";
import { Plus, Search, Globe, X } from "lucide-react";

// Types
export interface MarketIndex {
  id: string;
  name: string;
  country: string;
  countryCode: string;
  ytdReturn: number;
  pltmEps: number | null;
  divYield: number;
  marketCap: number;
  volume: number;
  chartData: number[];
  price: number;
  dailyChange: number;
  dailyChangePercent: number;
}

interface FinancialTableProps {
  title?: string;
  className?: string;
}

// MEMOIZED SPARKLINE
const Sparkline = memo(({ data, isDark, id }: { data: number[], isDark: boolean, id: string }) => {
  const { points, areaPoints, color, gradientId } = useMemo(() => {
    if (data.length < 2) return { points: "", areaPoints: "", color: "", gradientId: "" };
    
    const min = Math.min(...data);
    const max = Math.max(...data);
    const range = max - min || 1;
    const width = 80;
    const height = 32;
    
    const pts = data.map((value, index) => {
      const x = (index / (data.length - 1)) * width;
      const y = height - ((value - min) / range) * height;
      return `${x},${y}`;
    }).join(' ');

    const area = `${pts} ${width},${height} 0,${height}`;
    const clr = isDark ? "#22c55e" : "#16a34a";
    const gId = `sparkline-gradient-${id.replace(/[^a-zA-Z0-9]/g, '')}`; 

    return { points: pts, areaPoints: area, color: clr, gradientId: gId };
  }, [data, isDark, id]);

  if (!points) return <div className="w-16 h-4 bg-muted/10 rounded" />;

  return (
    <div className="w-20 h-8 relative group/spark">
      <svg width={80} height={32} className="overflow-visible filter drop-shadow-[0_0_4px_rgba(34,197,94,0.2)]">
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor={color} stopOpacity="0.3" />
            <stop offset="100%" stopColor={color} stopOpacity="0" />
          </linearGradient>
        </defs>
        <motion.polygon
          points={areaPoints}
          fill={`url(#${gradientId})`}
          initial={{ opacity: 0 }}
          animate={{ opacity: 0.5 }}
        />
        <motion.polyline
          points={points}
          fill="none"
          stroke={color}
          strokeWidth="2"
          initial={{ pathLength: 0 }}
          animate={{ pathLength: 1 }}
          transition={{ duration: 1 }}
        />
      </svg>
    </div>
  );
});

Sparkline.displayName = "Sparkline";

// MEMOIZED MARKET ROW
const MarketRow = memo(({ 
  index, 
  isSelected, 
  isDark, 
  onSelect, 
  onDelete,
  formatCurrency,
  formatLargeNumber,
  formatPercentage,
  getPerformanceColor,
  rowVariants
}: { 
  index: MarketIndex;
  isSelected: boolean;
  isDark: boolean;
  onSelect: (id: string) => void;
  onDelete: (id: string) => void;
  formatCurrency: (n: number) => string;
  formatLargeNumber: (n: number, u: string) => string;
  formatPercentage: (n: number) => string;
  getPerformanceColor: (n: number) => any;
  rowVariants: any;
}) => {
  return (
    <motion.div
      layout
      initial="hidden"
      animate="visible"
      exit={{ opacity: 0, scale: 0.95 }}
      variants={rowVariants}
      onClick={() => onSelect(index.id)}
      className={cn(
        "px-8 py-4 cursor-pointer group transition-all duration-200 grid grid-cols-[250px_100px_100px_100px_100px_100px_80px_100px_120px_40px] gap-4 items-center relative overflow-hidden",
        isSelected ? "bg-accent/10 border-l-2 border-accent" : "hover:bg-accent/5 border-l-2 border-transparent"
      )}
    >
      <div className="absolute inset-0 pointer-events-none opacity-0 group-hover:opacity-10 transition-opacity">
         <div className="w-full h-full bg-[linear-gradient(to_bottom,transparent_0%,rgba(255,255,255,0.1)_50%,transparent_100%)] animate-[scanline_4s_linear_infinite]" />
      </div>
      <div className="flex items-center gap-3">
        <div className="w-8 h-8 rounded-full bg-accent/20 flex items-center justify-center font-bold text-accent text-xs">
          {index.id.substring(0, 2)}
        </div>
        <div>
          <div className="font-bold text-foreground/90">{index.name}</div>
          <div className="text-[10px] text-muted-foreground uppercase font-black tracking-tighter opacity-50">{index.id}</div>
        </div>
      </div>

      <div className={cn("text-xs font-bold", getPerformanceColor(index.ytdReturn).textColor)}>
        {formatPercentage(index.ytdReturn)}
      </div>

      <div className="text-xs font-mono opacity-80">{index.pltmEps?.toFixed(2) ?? "—"}</div>
      <div className="text-xs font-bold text-orange-500/80">{formatPercentage(index.divYield)}</div>
      <div className="text-xs font-bold opacity-80">{formatLargeNumber(index.marketCap, "B")}</div>
      <div className="text-xs font-bold opacity-80">{formatLargeNumber(index.volume, "M")}</div>

      <div><Sparkline data={index.chartData} isDark={isDark} id={index.id} /></div>

      <div className="font-mono font-bold text-sm tracking-tighter">
        {index.price !== 0 ? formatCurrency(index.price) : "Connecting..."}
      </div>

      <div className="flex items-center justify-end gap-2 pr-4">
        <span className={cn("text-xs font-bold", getPerformanceColor(index.dailyChange).textColor)}>
          {index.dailyChange >= 0 ? "+" : ""}{index.dailyChange.toFixed(2)}
        </span>
        <div className={cn("px-2 py-0.5 rounded text-[10px] font-black border", getPerformanceColor(index.dailyChangePercent).bgColor, getPerformanceColor(index.dailyChangePercent).borderColor, getPerformanceColor(index.dailyChangePercent).textColor)}>
          {formatPercentage(index.dailyChangePercent)}
        </div>
      </div>

      <div className="flex justify-center">
        <button 
          onClick={(e) => {
            e.stopPropagation();
            onDelete(index.id);
          }}
          className="p-1 hover:text-red-500 transition-colors opacity-0 group-hover:opacity-100"
        >
          <X className="w-4 h-4" />
        </button>
      </div>
    </motion.div>
  );
});

MarketRow.displayName = "MarketRow";

const defaultIndices: MarketIndex[] = [
  {
    id: "BTC-USDT",
    name: "Bitcoin / Tether",
    country: "Global",
    countryCode: "UN",
    ytdReturn: 62.4,
    pltmEps: null,
    divYield: 0,
    marketCap: 1200000000000,
    volume: 35000000000,
    chartData: Array.from({ length: 20 }, () => 60000 + Math.random() * 5000),
    price: 64231.50,
    dailyChange: 1245.30,
    dailyChangePercent: 1.98
  },
  {
    id: "ETH-USDT",
    name: "Ethereum / Tether",
    country: "Global",
    countryCode: "UN",
    ytdReturn: 45.2,
    pltmEps: null,
    divYield: 0,
    marketCap: 400000000000,
    volume: 15000000000,
    chartData: Array.from({ length: 20 }, () => 3000 + Math.random() * 500),
    price: 3452.12,
    dailyChange: -42.15,
    dailyChangePercent: -1.21
  },
  {
    id: "SOL-USDT",
    name: "Solana / Tether",
    country: "Global",
    countryCode: "UN",
    ytdReturn: 124.8,
    pltmEps: null,
    divYield: 0,
    marketCap: 65000000000,
    volume: 4000000000,
    chartData: Array.from({ length: 20 }, () => 140 + Math.random() * 20),
    price: 152.45,
    dailyChange: 8.35,
    dailyChangePercent: 5.82
  }
];

export function FinancialTable({
  title = "Index",
  className = ""
}: FinancialTableProps = {}) {
  const [indices, setIndices] = useState<MarketIndex[]>(defaultIndices);
  const [selectedIndex, setSelectedIndex] = useState<string | null>("BTC-USDT");
  const [newSymbol, setNewSymbol] = useState("");
  const [highIntensityMode, setHighIntensityMode] = useState(false);
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const { theme } = useTheme();
  const isDark = theme === "dark";

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5000/market-hub")
      .withAutomaticReconnect()
      .build();

    connectionRef.current = connection;

    connection.on("ReceiveTick", (tick: { symbol: string, price: number }) => {
      setIndices(prev => prev.map(idx => {
        if (idx.id === tick.symbol) {
          const newPrice = tick.price;
          const oldPrice = idx.price;
          const diff = newPrice - oldPrice;
          const newChartData = [...idx.chartData.slice(1), newPrice];
          
          return {
            ...idx,
            price: newPrice,
            dailyChange: idx.dailyChange + (oldPrice !== 0 ? diff : 0),
            dailyChangePercent: oldPrice !== 0 ? ((newPrice - (idx.price - idx.dailyChange)) / (idx.price - idx.dailyChange)) * 100 : 0,
            chartData: newChartData
          };
        }
        return idx;
      }));
    });

    connection.start().then(() => {
      indices.forEach(idx => connection.invoke("SubscribeToMarket", idx.id));
    }).catch(err => console.error("SignalR Error", err));

    return () => { connection.stop(); };
  }, []);

  const handleAddSymbol = async (e: React.FormEvent) => {
    e.preventDefault();
    const symbol = newSymbol.trim().toUpperCase();
    if (!symbol || indices.find(idx => idx.id === symbol)) return;
    const formattedSymbol = symbol.includes("-") ? symbol : `${symbol}-USDT`;

    const newIndex: MarketIndex = {
      id: formattedSymbol, name: `${symbol} / Tether`, country: "Global", countryCode: "UN",
      ytdReturn: 0, pltmEps: null, divYield: 0, marketCap: 0, volume: 0,
      chartData: Array.from({ length: 20 }, () => 0), price: 0, dailyChange: 0, dailyChangePercent: 0
    };

    setIndices(prev => [...prev, newIndex]);
    setNewSymbol("");
    if (connectionRef.current?.state === signalR.HubConnectionState.Connected) {
      await connectionRef.current.invoke("SubscribeToMarket", formattedSymbol);
    }
  };

  const handleDeleteSymbol = async (id: string) => {
    setIndices(prev => prev.filter(idx => idx.id !== id));
    if (connectionRef.current?.state === signalR.HubConnectionState.Connected) {
      await connectionRef.current.invoke("UnsubscribeFromMarket", id);
    }
    if (selectedIndex === id) setSelectedIndex(null);
  };

  const formatCurrency = (amount: number) => `$${amount.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const formatLargeNumber = (amount: number, unit: string) => {
    if (amount >= 1000000000) return `${(amount / 1000000000).toFixed(1)}B`;
    if (amount >= 1000000) return `${(amount / 1000000).toFixed(1)}M`;
    return `${amount.toFixed(1)}${unit}`;
  };
  const formatPercentage = (value: number) => `${value >= 0 ? "+" : ""}${value.toFixed(2)}%`;

  const getPerformanceColor = (value: number) => {
    const isPositive = value >= 0;
    return {
      textColor: isPositive ? (isDark ? "text-green-400" : "text-green-600") : (isDark ? "text-red-400" : "text-red-600"),
      bgColor: isPositive ? (isDark ? "bg-green-500/10" : "bg-green-50") : (isDark ? "bg-red-500/10" : "bg-red-50"),
      borderColor: isPositive ? (isDark ? "border-green-500/30" : "border-green-200") : (isDark ? "border-red-500/30" : "border-red-200")
    };
  };

  const rowVariants = {
    hidden: { opacity: 0, y: 10 },
    visible: { opacity: 1, y: 0, transition: { type: "spring" as const, stiffness: 400, damping: 30 } }
  };

  return (
    <div className={cn("w-full max-w-7xl mx-auto space-y-4", className)}>
      <div className="flex items-center justify-between px-2">
        <h2 className="text-xl font-black tracking-tight text-foreground flex items-center gap-2">
          <Globe className="w-5 h-5 text-accent" />
          MARKET SYMBOL REGISTRY
        </h2>
        
        <div className="flex items-center gap-6">
          <button 
            onClick={() => setHighIntensityMode(!highIntensityMode)}
            className={cn(
              "flex items-center gap-2 px-3 py-1.5 rounded-full border text-[10px] font-black tracking-widest uppercase transition-all",
              highIntensityMode 
                ? "bg-orange-500/20 border-orange-500 text-orange-400 shadow-[0_0_10px_rgba(249,115,22,0.3)]" 
                : "bg-muted/10 border-border/50 text-muted-foreground hover:border-accent/50"
            )}
          >
            <div className={cn("w-1.5 h-1.5 rounded-full", highIntensityMode ? "bg-orange-500 animate-pulse" : "bg-muted-foreground")} />
            {highIntensityMode ? "High-Intensity Active" : "Normal Mode"}
          </button>

          <form onSubmit={handleAddSymbol} className="flex items-center gap-2">
            <div className="relative group">
              <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground/50 transition-colors group-focus-within:text-accent" />
              <input 
                type="text" placeholder="Add symbol..." value={newSymbol}
                onChange={(e) => setNewSymbol(e.target.value)}
                className="pl-9 pr-4 py-2 bg-muted/20 border border-border/50 rounded-lg text-xs font-bold focus:outline-none focus:ring-1 focus:ring-accent/50 focus:border-accent transition-all w-48"
              />
            </div>
            <button type="submit" className="p-2 bg-accent text-accent-foreground rounded-lg hover:opacity-90 transition-opacity active:scale-95">
              <Plus className="w-5 h-5" />
            </button>
          </form>
        </div>
      </div>

      <div className="bg-background border border-border/50 rounded-2xl overflow-hidden shadow-2xl shadow-black/20">
        <div className="overflow-x-auto">
          <div className="min-w-[1100px]">
            <div className="px-8 py-4 text-xs font-bold text-muted-foreground/60 uppercase tracking-widest bg-muted/5 border-b border-border/10 grid grid-cols-[250px_100px_100px_100px_100px_100px_80px_100px_120px_40px] gap-4">
              <div>{title}</div>
              <div>YTD Return</div>
              <div>P/LTM EPS</div>
              <div>Div yield</div>
              <div>Mkt cap</div>
              <div>Volume</div>
              <div>Trend</div>
              <div>Price</div>
              <div className="text-right">Performance</div>
              <div></div>
            </div>

            <motion.div initial="hidden" animate="visible" transition={highIntensityMode ? { duration: 0 } : { staggerChildren: 0.05 }}>
              <AnimatePresence mode="popLayout" initial={false}>
                {indices.map((index) => (
                  <MarketRow 
                    key={index.id}
                    index={index}
                    isSelected={selectedIndex === index.id}
                    isDark={isDark}
                    onSelect={setSelectedIndex}
                    onDelete={handleDeleteSymbol}
                    formatCurrency={formatCurrency}
                    formatLargeNumber={formatLargeNumber}
                    formatPercentage={formatPercentage}
                    getPerformanceColor={getPerformanceColor}
                    rowVariants={highIntensityMode ? { hidden: { opacity: 1 }, visible: { opacity: 1 } } : rowVariants}
                  />
                ))}
              </AnimatePresence>
            </motion.div>
          </div>
        </div>
      </div>
    </div>
  );
}
