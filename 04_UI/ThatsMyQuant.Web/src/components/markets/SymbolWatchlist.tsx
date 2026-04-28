import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { marketsApi, type MarketSymbol } from "@/api/markets";
import { Sparkline } from "@/components/charts/Sparkline";
import { cn } from "@/lib/utils";
import { watchlistGroupStore, type WatchlistGroup } from "@/lib/watchlistGroups";

interface Props {
  selected: string;
  onSelect: (symbol: string) => void;
  pinned: Set<string>;
  onTogglePin: (symbol: string) => void;
}

type SortKey = "default" | "change-desc" | "change-asc" | "name";
const ALL_GROUP_ID = "__all__";

export function SymbolWatchlist({ selected, onSelect, pinned, onTogglePin }: Props) {
  const q = useQuery({
    queryKey: ["markets", "symbols"],
    queryFn: () => marketsApi.symbols(),
    refetchInterval: 15_000,
  });

  const [filter, setFilter] = useState("");
  const [sort, setSort] = useState<SortKey>("default");
  const [activeGroupId, setActiveGroupId] = useState<string>(() => {
    try {
      return localStorage.getItem("tmq.watchlist.activeGroup") || ALL_GROUP_ID;
    } catch {
      return ALL_GROUP_ID;
    }
  });
  const [groups, setGroups] = useState<WatchlistGroup[]>(() => watchlistGroupStore.list());
  const [editMode, setEditMode] = useState(false);

  useEffect(() => watchlistGroupStore.subscribe(setGroups), []);

  useEffect(() => {
    try {
      localStorage.setItem("tmq.watchlist.activeGroup", activeGroupId);
    } catch {
      /* ignore */
    }
  }, [activeGroupId]);

  const activeGroup = groups.find((g) => g.id === activeGroupId);
  const groupSymbolSet = activeGroup
    ? new Set(activeGroup.symbols.map((s) => s.toUpperCase()))
    : null;

  const symbols = useMemo(() => {
    let list = q.data ?? [];

    // Group filtering. If a group is active and we're not editing,
    // show only the group's symbols. While editing, show ALL so we can toggle.
    if (groupSymbolSet && !editMode) {
      list = list.filter((s) => groupSymbolSet.has(s.symbol));
    }

    const filtered = filter
      ? list.filter((s) => s.symbol.toLowerCase().includes(filter.toLowerCase()))
      : list;

    switch (sort) {
      case "change-desc":
        return [...filtered].sort((a, b) => b.changePct24h - a.changePct24h);
      case "change-asc":
        return [...filtered].sort((a, b) => a.changePct24h - b.changePct24h);
      case "name":
        return [...filtered].sort((a, b) => a.symbol.localeCompare(b.symbol));
      default:
        return filtered;
    }
  }, [q.data, filter, sort, groupSymbolSet, editMode]);

  const onAddGroup = () => {
    const name = prompt("Group name?", "New Group");
    if (!name) return;
    const ws = watchlistGroupStore.upsert({ name, symbols: [] });
    setActiveGroupId(ws.id);
    setEditMode(true);
  };

  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl flex flex-col overflow-hidden h-full">
      <div className="px-4 py-3 border-b border-white/5">
        <div className="flex items-center justify-between">
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            Markets
          </p>
          {activeGroupId !== ALL_GROUP_ID && (
            <button
              onClick={() => setEditMode((v) => !v)}
              className={cn(
                "text-[9px] uppercase tracking-widest transition-colors",
                editMode ? "text-accent" : "text-muted-foreground/50 hover:text-foreground"
              )}
            >
              {editMode ? "done" : "edit"}
            </button>
          )}
        </div>
        <p className="text-sm font-bold">
          Watchlist{" "}
          <span className="text-muted-foreground/50 font-light text-xs">
            — {symbols.length}
            {editMode && groupSymbolSet ? ` · ${groupSymbolSet.size} in group` : ""}
          </span>
        </p>

        {/* Group tabs */}
        <div className="mt-2.5 flex items-center gap-1 flex-wrap">
          <button
            onClick={() => {
              setActiveGroupId(ALL_GROUP_ID);
              setEditMode(false);
            }}
            className={cn(
              "px-2 py-0.5 rounded text-[10px] font-mono uppercase tracking-wider border transition-colors",
              activeGroupId === ALL_GROUP_ID
                ? "bg-accent/15 text-accent border-accent/30"
                : "border-white/10 text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.03]"
            )}
          >
            All
          </button>
          {groups.map((g) => (
            <button
              key={g.id}
              onClick={() => {
                if (activeGroupId === g.id) {
                  // Allow rename via double-click pattern
                  return;
                }
                setActiveGroupId(g.id);
                setEditMode(false);
              }}
              onDoubleClick={() => {
                const next = prompt("Rename group", g.name);
                if (next && next.trim()) watchlistGroupStore.rename(g.id, next);
              }}
              title={`${g.name} · ${g.symbols.length} symbols · double-click to rename`}
              className={cn(
                "px-2 py-0.5 rounded text-[10px] font-mono uppercase tracking-wider border transition-colors",
                activeGroupId === g.id
                  ? "bg-accent/15 text-accent border-accent/30"
                  : "border-white/10 text-muted-foreground/60 hover:text-foreground hover:bg-white/[0.03]"
              )}
            >
              {g.name}
              <span className="ml-1 text-muted-foreground/40">{g.symbols.length}</span>
            </button>
          ))}
          <button
            onClick={onAddGroup}
            title="Add custom group"
            className="px-1.5 py-0.5 rounded text-[10px] font-mono text-muted-foreground/40 hover:text-accent hover:bg-white/[0.03] transition-colors"
          >
            +
          </button>
        </div>

        <div className="mt-2 flex items-center gap-2">
          <input
            type="search"
            placeholder="Filter…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            aria-label="Filter symbols"
            className="flex-1 bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[11px] font-mono"
          />
          <select
            value={sort}
            onChange={(e) => setSort(e.target.value as SortKey)}
            aria-label="Sort symbols"
            className="bg-[#070A0F] border border-white/10 rounded px-2 py-1.5 text-[10px] font-mono"
          >
            <option value="default">Default</option>
            <option value="change-desc">% Δ ↓</option>
            <option value="change-asc">% Δ ↑</option>
            <option value="name">A-Z</option>
          </select>
        </div>

        {editMode && activeGroup && (
          <div className="mt-2 flex items-center justify-between">
            <p className="text-[9px] font-mono italic text-muted-foreground/60">
              click ☆ to add/remove from {activeGroup.name}
            </p>
            <button
              onClick={() => {
                if (confirm(`Delete "${activeGroup.name}"?`)) {
                  watchlistGroupStore.remove(activeGroup.id);
                  setActiveGroupId(ALL_GROUP_ID);
                  setEditMode(false);
                }
              }}
              className="text-[9px] uppercase tracking-widest text-muted-foreground/50 hover:text-rose-400 transition-colors"
            >
              delete group
            </button>
          </div>
        )}
      </div>

      <div className="flex-1 overflow-y-auto">
        {q.isLoading ? (
          <p className="p-8 text-center text-xs italic font-serif text-muted-foreground">
            — loading symbols —
          </p>
        ) : symbols.length === 0 ? (
          <p className="p-8 text-center text-xs italic font-serif text-muted-foreground">
            {activeGroup && activeGroup.symbols.length === 0
              ? `— "${activeGroup.name}" is empty · turn on edit and ☆ symbols —`
              : "— no matches —"}
          </p>
        ) : (
          symbols.map((s) => (
            <Row
              key={s.symbol}
              symbol={s}
              selected={s.symbol === selected}
              pinned={pinned.has(s.symbol)}
              inGroup={groupSymbolSet?.has(s.symbol) ?? false}
              editMode={editMode && activeGroup != null}
              onSelect={() => onSelect(s.symbol)}
              onTogglePin={() => onTogglePin(s.symbol)}
              onToggleGroup={() => activeGroup && watchlistGroupStore.toggleSymbol(activeGroup.id, s.symbol)}
            />
          ))
        )}
      </div>
    </div>
  );
}

function Row({
  symbol: s,
  selected,
  pinned,
  inGroup,
  editMode,
  onSelect,
  onTogglePin,
  onToggleGroup,
}: {
  symbol: MarketSymbol;
  selected: boolean;
  pinned: boolean;
  inGroup: boolean;
  editMode: boolean;
  onSelect: () => void;
  onTogglePin: () => void;
  onToggleGroup: () => void;
}) {
  const positive = s.changePct24h >= 0;
  const sparkColor = positive ? "rgba(45, 106, 79, 0.85)" : "rgba(181, 54, 75, 0.85)";

  return (
    <div
      onClick={onSelect}
      className={cn(
        "px-4 py-2.5 border-b border-white/[0.04] cursor-pointer transition-colors flex items-center gap-2",
        selected
          ? "bg-accent/10 border-l-2 border-l-accent"
          : "hover:bg-white/[0.03] border-l-2 border-l-transparent"
      )}
    >
      {editMode ? (
        <button
          onClick={(e) => {
            e.stopPropagation();
            onToggleGroup();
          }}
          title={inGroup ? "Remove from group" : "Add to group"}
          aria-label={inGroup ? `Remove ${s.symbol} from group` : `Add ${s.symbol} to group`}
          className={cn(
            "shrink-0 w-5 h-5 rounded flex items-center justify-center text-[12px] transition-colors",
            inGroup
              ? "text-amber-400"
              : "text-muted-foreground/30 hover:text-amber-400"
          )}
        >
          {inGroup ? "★" : "☆"}
        </button>
      ) : (
        <button
          onClick={(e) => {
            e.stopPropagation();
            onTogglePin();
          }}
          title={pinned ? "Unpin (close mini chart)" : "Pin (open mini chart)"}
          aria-label={pinned ? `Unpin ${s.symbol}` : `Pin ${s.symbol}`}
          className={cn(
            "shrink-0 w-5 h-5 rounded flex items-center justify-center text-[9px] transition-colors",
            pinned
              ? "bg-accent/20 text-accent"
              : "text-muted-foreground/30 hover:text-accent hover:bg-white/[0.05]"
          )}
        >
          {pinned ? "●" : "○"}
        </button>
      )}

      <div className="min-w-0 flex-1">
        <p className="font-bold text-[12px] tracking-tight">{s.symbol}</p>
        <p
          className={cn(
            "text-[10px] font-mono tabular-nums leading-tight",
            positive ? "text-emerald-400" : "text-rose-400"
          )}
        >
          {positive ? "+" : ""}
          {s.changePct24h.toFixed(2)}%
        </p>
      </div>

      <div className="shrink-0 hidden sm:block">
        <Sparkline
          values={s.sparkline}
          width={48}
          height={20}
          stroke={sparkColor}
          strokeWidth={1.2}
        />
      </div>

      <div className="shrink-0 text-right min-w-[64px]">
        <p className="text-[11px] font-mono tabular-nums font-bold">
          {s.lastPrice >= 1000
            ? s.lastPrice.toLocaleString(undefined, { maximumFractionDigits: 1 })
            : s.lastPrice >= 1
              ? s.lastPrice.toFixed(2)
              : s.lastPrice.toFixed(5)}
        </p>
      </div>
    </div>
  );
}
