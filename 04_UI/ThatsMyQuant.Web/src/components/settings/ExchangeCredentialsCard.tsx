import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { settingsApi } from "@/api/settings";
import { pushNotification } from "@/lib/notifications";
import { cn } from "@/lib/utils";

/**
 * Exchange-credentials editor for the Settings page.  Lets the operator paste
 * their OKX (live + demo) and Binance API credentials directly in the browser
 * — the backend DPAPI-encrypts them under the running user's profile and
 * persists to <c>exchange_credentials.dpapi</c>.
 *
 * Status row at the top is read-only: it shows whether each slot is currently
 * configured.  The form NEVER displays existing secrets — they're write-only.
 */
export function ExchangeCredentialsCard() {
  const qc = useQueryClient();

  const statusQ = useQuery({
    queryKey: ["settings", "credentials-status"],
    queryFn:  settingsApi.credentialsStatus,
    refetchInterval: 60_000,
  });

  const saveM = useMutation({
    mutationFn: settingsApi.saveCredentials,
    onSuccess: () => {
      pushNotification({
        type: "success",
        title: "Credentials saved",
        body:  "DPAPI-encrypted on disk. Restart Listener.Api + Worker to activate.",
      });
      qc.invalidateQueries({ queryKey: ["settings", "credentials-status"] });
      // Clear the form on success
      setOkx({ key: "", secret: "", pass: "" });
      setOkxDemo({ key: "", secret: "", pass: "" });
      setBnc({ key: "", secret: "" });
    },
    onError: (err: unknown) => {
      pushNotification({
        type: "error",
        title: "Save failed",
        body:  err instanceof Error ? err.message : String(err),
      });
    },
  });

  const [tab, setTab] = useState<"okx-demo" | "okx-live" | "binance">("okx-demo");
  const [okx, setOkx] = useState({ key: "", secret: "", pass: "" });
  const [okxDemo, setOkxDemo] = useState({ key: "", secret: "", pass: "" });
  const [bnc, setBnc] = useState({ key: "", secret: "" });
  const [setDemoMode, setSetDemoMode] = useState<boolean | null>(null);

  const status = statusQ.data;
  const submit = () => {
    const payload: Parameters<typeof settingsApi.saveCredentials>[0] = {};
    if (tab === "okx-live") {
      payload.okxApiKey      = okx.key.trim();
      payload.okxSecretKey   = okx.secret.trim();
      payload.okxPassphrase  = okx.pass.trim();
      if (setDemoMode === false) payload.setDemoMode = false;
    } else if (tab === "okx-demo") {
      payload.okxDemoApiKey      = okxDemo.key.trim();
      payload.okxDemoSecretKey   = okxDemo.secret.trim();
      payload.okxDemoPassphrase  = okxDemo.pass.trim();
      if (setDemoMode === true) payload.setDemoMode = true;
    } else {
      payload.binanceApiKey    = bnc.key.trim();
      payload.binanceApiSecret = bnc.secret.trim();
    }

    // Validate at least one slot has values
    const anyValue = Object.values(payload).some((v) => typeof v === "string" && v.length > 0);
    if (!anyValue) {
      pushNotification({ type: "warning", title: "Nothing to save", body: "Fill in at least one field." });
      return;
    }
    saveM.mutate(payload);
  };

  const isMac = !!status?.os && !status.os.toLowerCase().includes("win");

  return (
    <div className="rounded-2xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5 space-y-4">
      <div className="flex items-baseline justify-between">
        <div>
          <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-1">
            🔐 Exchange Credentials
          </p>
          <p className="text-[11px] font-serif italic text-muted-foreground/70">
            Paste API keys here — backend DPAPI-encrypts under your Windows profile.
          </p>
        </div>
        {status && (
          <div className="text-[10px] font-mono text-muted-foreground/60 text-right">
            <div>file: <span className="text-foreground/70">{shortPath(status.path)}</span></div>
            <div>os: <span className="text-foreground/70">{status.os}</span></div>
          </div>
        )}
      </div>

      {/* Status row — three pills */}
      <div className="grid grid-cols-3 gap-2 text-center text-[10px] font-mono uppercase tracking-widest">
        <StatusPill label="OKX Demo"  on={!!status?.okxDemoConfigured} />
        <StatusPill label="OKX Live"  on={!!status?.okxLiveConfigured} />
        <StatusPill label="Binance"   on={!!status?.binanceConfigured} />
      </div>

      {isMac && (
        <p className="text-[10px] font-mono italic rounded-md border border-amber-400/30 bg-amber-500/10 text-amber-300 px-3 py-2">
          ⚠ DPAPI is Windows-only. On {status.os} the save endpoint will reject — set credentials via env vars instead.
        </p>
      )}

      {/* Tab switcher */}
      <div className="flex items-center gap-0.5 rounded-md p-0.5 border border-white/10 bg-white/[0.02] w-fit">
        <TabBtn active={tab === "okx-demo"} onClick={() => setTab("okx-demo")}>OKX Demo</TabBtn>
        <TabBtn active={tab === "okx-live"} onClick={() => setTab("okx-live")}>OKX Live</TabBtn>
        <TabBtn active={tab === "binance"}  onClick={() => setTab("binance")}>Binance</TabBtn>
      </div>

      {/* Forms */}
      {tab === "okx-demo" && (
        <KeyForm
          fields={[
            { label: "Demo API Key",     value: okxDemo.key,    onChange: (v) => setOkxDemo({ ...okxDemo, key: v }),    placeholder: "Paste OKX demo API key" },
            { label: "Demo Secret Key",  value: okxDemo.secret, onChange: (v) => setOkxDemo({ ...okxDemo, secret: v }), placeholder: "Paste secret",  secret: true },
            { label: "Demo Passphrase",  value: okxDemo.pass,   onChange: (v) => setOkxDemo({ ...okxDemo, pass: v }),   placeholder: "Passphrase",   secret: true },
          ]}
          extra={
            <label className="flex items-center gap-2 text-[10px] font-mono cursor-pointer select-none">
              <input
                type="checkbox"
                checked={setDemoMode === true}
                onChange={(e) => setSetDemoMode(e.target.checked ? true : null)}
              />
              Also set <code className="text-foreground/80">IsDemoMode = true</code> in exchange_settings.json
            </label>
          }
        />
      )}

      {tab === "okx-live" && (
        <KeyForm
          fields={[
            { label: "Live API Key",     value: okx.key,    onChange: (v) => setOkx({ ...okx, key: v }),    placeholder: "Paste OKX live API key" },
            { label: "Live Secret Key",  value: okx.secret, onChange: (v) => setOkx({ ...okx, secret: v }), placeholder: "Paste secret",   secret: true },
            { label: "Live Passphrase",  value: okx.pass,   onChange: (v) => setOkx({ ...okx, pass: v }),   placeholder: "Passphrase",     secret: true },
          ]}
          extra={
            <label className="flex items-center gap-2 text-[10px] font-mono cursor-pointer select-none">
              <input
                type="checkbox"
                checked={setDemoMode === false}
                onChange={(e) => setSetDemoMode(e.target.checked ? false : null)}
              />
              Also set <code className="text-foreground/80">IsDemoMode = false</code> in exchange_settings.json
            </label>
          }
          warning="🚨 LIVE keys will execute real trades. Make sure your TMQ_ALLOW_LIVE_TRADING flag and StartupSafetyPolicy are correctly set before flipping to live mode."
        />
      )}

      {tab === "binance" && (
        <KeyForm
          fields={[
            { label: "Binance API Key",    value: bnc.key,    onChange: (v) => setBnc({ ...bnc, key: v }),    placeholder: "Paste Binance API key" },
            { label: "Binance API Secret", value: bnc.secret, onChange: (v) => setBnc({ ...bnc, secret: v }), placeholder: "Paste secret",  secret: true },
          ]}
        />
      )}

      <div className="flex items-center gap-3 pt-2 border-t border-white/[0.04]">
        <button
          onClick={submit}
          disabled={saveM.isPending || isMac}
          className={cn(
            "px-4 py-1.5 rounded-md text-[11px] font-black uppercase tracking-widest border transition-colors",
            saveM.isPending || isMac
              ? "border-white/10 text-muted-foreground/40 cursor-not-allowed"
              : "border-accent/40 bg-accent/15 text-accent hover:bg-accent/25"
          )}
        >
          {saveM.isPending ? "Encrypting…" : "💾 Save & Encrypt"}
        </button>
        <p className="text-[10px] font-serif italic text-muted-foreground/55 flex-1">
          Saved values are encrypted with DPAPI <strong>CurrentUser</strong> scope — only this Windows account can decrypt them.
          Restart Listener.Api + Worker after saving so the new keys are loaded.
        </p>
      </div>
    </div>
  );
}

// ─── Sub-components ────────────────────────────────────────────────────────

function StatusPill({ label, on }: { label: string; on: boolean }) {
  return (
    <div
      className={cn(
        "px-2.5 py-1.5 rounded-md border",
        on
          ? "border-emerald-400/30 bg-emerald-500/10 text-emerald-300"
          : "border-white/10 bg-white/[0.02] text-muted-foreground/55"
      )}
    >
      {on ? "✓" : "○"} {label}
      <div className="text-[8px] tracking-widest opacity-65 mt-0.5">{on ? "configured" : "not set"}</div>
    </div>
  );
}

interface KeyFormField {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  secret?: boolean;
}

function KeyForm({
  fields, extra, warning,
}: {
  fields: KeyFormField[];
  extra?: React.ReactNode;
  warning?: string;
}) {
  return (
    <div className="space-y-2.5">
      {warning && (
        <p className="text-[10px] font-mono rounded-md border border-rose-400/30 bg-rose-500/10 text-rose-300 px-3 py-2">
          {warning}
        </p>
      )}
      {fields.map((f) => (
        <label key={f.label} className="block">
          <span className="text-[8px] font-black uppercase tracking-widest text-muted-foreground/50 mb-1 block">
            {f.label}
          </span>
          <input
            type={f.secret ? "password" : "text"}
            autoComplete="off"
            spellCheck={false}
            value={f.value}
            onChange={(e) => f.onChange(e.target.value)}
            placeholder={f.placeholder}
            className="w-full bg-[#070A0F] border border-white/10 rounded px-3 py-1.5 text-[11px] font-mono"
          />
        </label>
      ))}
      {extra}
    </div>
  );
}

function TabBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={cn(
        "px-3 py-0.5 rounded text-[10px] font-mono uppercase tracking-widest transition-colors",
        active ? "bg-foreground/85 text-background font-black" : "text-muted-foreground/60 hover:text-foreground"
      )}
    >
      {children}
    </button>
  );
}

function shortPath(p: string): string {
  const parts = p.split(/[\\/]/);
  return parts.length > 3 ? `…/${parts.slice(-2).join("/")}` : p;
}
