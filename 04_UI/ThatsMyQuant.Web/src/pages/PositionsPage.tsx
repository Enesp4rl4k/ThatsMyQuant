import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardApi } from "@/api/dashboard";
import { PositionsTable } from "@/components/dashboard/PositionsTable";
import { PositionDetailModal } from "@/components/positions/PositionDetailModal";
import type { Position } from "@/api/types";

export function PositionsPage() {
  const [selected, setSelected] = useState<Position | null>(null);

  const positionsQ = useQuery({
    queryKey: ["positions"],
    queryFn: () => dashboardApi.positions(),
    refetchInterval: 3_000,
  });

  return (
    <div className="space-y-8">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Live Risk
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Open Positions{" "}
          <span className="text-muted-foreground/30 font-light text-lg font-normal">
            — click row to inspect
          </span>
        </p>
      </div>

      <PositionsTable
        positions={positionsQ.data ?? []}
        isLoading={positionsQ.isLoading}
        onRowClick={setSelected}
      />

      {selected && (
        <PositionDetailModal
          position={selected}
          onClose={() => setSelected(null)}
        />
      )}
    </div>
  );
}
