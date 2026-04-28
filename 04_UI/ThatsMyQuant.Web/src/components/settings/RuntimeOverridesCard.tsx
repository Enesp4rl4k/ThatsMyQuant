import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { settingsApi } from "@/api/settings";
import { cn } from "@/lib/utils";

export function RuntimeOverridesCard() {
  const qc = useQueryClient();
  const schemaQ = useQuery({ queryKey: ["settings", "writable"], queryFn: settingsApi.writable, staleTime: 300_000 });
  const runtimeQ = useQuery({ queryKey: ["settings", "runtime"], queryFn: settingsApi.runtime, staleTime: 60_000 });

  const [edits, setEdits] = useState<Record<string, string>>({});
  const [feedback, setFeedback] = useState<{ ok?: number; rejected?: Record<string, string> } | null>(null);

  useEffect(() => { setFeedback(null); }, [runtimeQ.data]);

  const m = useMutation({
    mutationFn: (body: Record<string, unknown>) => settingsApi.patch(body),
    onSuccess: (data) => {
      setFeedback({ ok: data.applied, rejected: data.rejected });
      setEdits({});
      qc.invalidateQueries({ queryKey: ["settings"] });
    },
    onError: (err: Error) => setFeedback({ rejected: { _: err.message } }),
  });

  if (schemaQ.isLoading) {
    return <Card><p className="text-xs text-muted-foreground/60">Loading writable schema…</p></Card>;
  }
  if (!schemaQ.data) {
    return <Card><p className="text-xs text-rose-400">Failed to load writable settings.</p></Card>;
  }

  const overrides = runtimeQ.data?.overrides ?? {};

  const submit = () => {
    const body: Record<string, unknown> = {};
    for (const [key, raw] of Object.entries(edits)) {
      const spec = schemaQ.data.keys.find((k) => k.key === key);
      if (!spec) continue;
      if (raw === "") continue;
      if (spec.kind === "bool") body[key] = raw === "true";
      else if (spec.kind === "int") body[key] = parseInt(raw, 10);
      else if (spec.kind === "number") body[key] = parseFloat(raw);
      else body[key] = raw;
    }
    if (Object.keys(body).length === 0) return;
    m.mutate(body);
  };

  return (
    <Card>
      <div className="flex items-start justify-between mb-3">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
            07 · Runtime Tuning
          </p>
          <p className="text-sm font-bold">Live Overrides</p>
          <p className="text-[10px] text-muted-foreground/50 mt-1 leading-relaxed">
            Persisted to <code className="text-accent/70">appsettings.runtime.json</code>. Picked up by{" "}
            <code className="text-accent/70">IOptionsMonitor</code> consumers without restart. Process-local —
            QuantEngine.Worker still needs its own override mechanism.
          </p>
        </div>
        <button
          disabled={m.isPending || Object.keys(edits).length === 0}
          onClick={submit}
          className={cn(
            "px-3 py-1.5 rounded-md text-[10px] font-black uppercase tracking-widest transition-colors",
            m.isPending || Object.keys(edits).length === 0
              ? "bg-white/5 text-muted-foreground/30 cursor-not-allowed"
              : "bg-accent/20 text-accent hover:bg-accent/30 border border-accent/30"
          )}
        >
          {m.isPending ? "Saving…" : "Apply"}
        </button>
      </div>

      <div className="space-y-2">
        {schemaQ.data.keys.map((spec) => {
          const current = overrides[spec.key];
          const editing = edits[spec.key];
          const value = editing !== undefined ? editing : current !== undefined ? String(current) : "";
          const dirty = editing !== undefined && editing !== (current !== undefined ? String(current) : "");

          return (
            <div key={spec.key} className="flex items-center gap-3 text-xs">
              <code className="flex-1 text-[10px] text-muted-foreground/70 font-mono truncate" title={spec.key}>
                {spec.key}
              </code>
              <span className="text-[9px] uppercase tracking-widest text-muted-foreground/40 w-14 text-right">
                {spec.kind}
                {spec.min !== null && spec.max !== null && (
                  <span className="block text-[8px]">
                    {spec.min}–{spec.max}
                  </span>
                )}
              </span>
              {spec.kind === "bool" ? (
                <select
                  value={value}
                  onChange={(e) =>
                    setEdits((p) => ({ ...p, [spec.key]: e.target.value }))
                  }
                  className={cn(
                    "w-32 bg-[#070A0F] border rounded px-2 py-1 text-[11px] font-mono",
                    dirty ? "border-accent/60" : "border-white/10"
                  )}
                >
                  <option value="">—</option>
                  <option value="true">true</option>
                  <option value="false">false</option>
                </select>
              ) : (
                <input
                  type={spec.kind === "string" ? "text" : "number"}
                  step={spec.kind === "number" ? "0.01" : "1"}
                  min={spec.min ?? undefined}
                  max={spec.max ?? undefined}
                  value={value}
                  placeholder="(default)"
                  onChange={(e) =>
                    setEdits((p) => ({ ...p, [spec.key]: e.target.value }))
                  }
                  className={cn(
                    "w-32 bg-[#070A0F] border rounded px-2 py-1 text-[11px] font-mono text-right",
                    dirty ? "border-accent/60" : "border-white/10"
                  )}
                />
              )}
            </div>
          );
        })}
      </div>

      {feedback && (
        <div className="mt-3 text-[10px] space-y-1">
          {feedback.ok !== undefined && feedback.ok > 0 && (
            <p className="text-emerald-400">✓ Applied {feedback.ok} setting(s).</p>
          )}
          {feedback.rejected && Object.keys(feedback.rejected).length > 0 && (
            <div className="text-rose-400">
              {Object.entries(feedback.rejected).map(([k, v]) => (
                <p key={k}>
                  ✗ {k}: {v}
                </p>
              ))}
            </div>
          )}
        </div>
      )}
    </Card>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
      {children}
    </div>
  );
}
