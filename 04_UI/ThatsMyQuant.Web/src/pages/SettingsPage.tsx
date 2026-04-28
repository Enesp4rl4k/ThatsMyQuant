import { useQuery } from "@tanstack/react-query";
import { settingsApi } from "@/api/settings";
import { cn } from "@/lib/utils";
import { RuntimeOverridesCard } from "@/components/settings/RuntimeOverridesCard";
import { ExchangeCredentialsCard } from "@/components/settings/ExchangeCredentialsCard";
import { StrategyTunerCard } from "@/components/settings/StrategyTunerCard";
import { NotificationPrefsCard } from "@/components/markets/NotificationPrefsCard";
import { restartOnboarding } from "@/components/layout/OnboardingTour";

export function SettingsPage() {
  const q = useQuery({
    queryKey: ["settings"],
    queryFn: settingsApi.get,
    staleTime: 60_000,
  });

  const s = q.data;

  return (
    <div className="space-y-8 max-w-3xl">
      <div className="relative">
        <div className="absolute -left-4 top-0 bottom-0 w-1 bg-accent shadow-[0_0_15px_rgba(0,191,255,0.5)]" />
        <h2 className="text-[10px] font-black uppercase tracking-[0.4em] text-accent mb-1 ml-2">
          Configuration
        </h2>
        <p className="text-2xl font-black tracking-tight ml-2">
          Settings{" "}
          <span className="text-muted-foreground/30 font-light">
            — Runtime snapshot · read-only view
          </span>
        </p>
      </div>

      {q.isLoading ? (
        <p className="text-sm text-muted-foreground">Loading settings…</p>
      ) : q.isError ? (
        <p className="text-sm text-rose-400">Failed to load settings from API.</p>
      ) : s ? (
        <div className="space-y-5">
          {/* Exchange */}
          <Section title="Exchange" eyebrow="01 · Connectivity">
            <Row label="Mode" value={s.exchange.mode} />
            <Row
              label="API Credentials"
              value={s.exchange.hasApiCredentials ? "Configured" : "Not configured"}
              valueClass={s.exchange.hasApiCredentials ? "text-emerald-400" : "text-rose-400"}
            />
            <Row
              label="Trading Mode"
              value={s.exchange.isDemo ? "Demo / Paper" : "Live"}
              valueClass={s.exchange.isDemo ? "text-amber-400" : "text-emerald-400"}
            />
            <InfoBox>
              Edit credentials inline below — values are DPAPI-encrypted under your Windows
              profile and saved to <code className="text-accent/80">exchange_credentials.dpapi</code>.
              For non-Windows hosts, use environment variables instead.
            </InfoBox>
          </Section>

          {/* Exchange credentials editor — DPAPI-encrypt + persist */}
          <ExchangeCredentialsCard />

          {/* Live strategy tuner — sliders + toggles for runtime overrides */}
          <StrategyTunerCard />

          {/* Telegram */}
          <Section title="Telegram Alerts" eyebrow="02 · Notifications">
            <Row
              label="Bot Token"
              value={s.telegram.configured ? "Configured ✓" : "Not set"}
              valueClass={s.telegram.configured ? "text-emerald-400" : "text-muted-foreground/50"}
            />
            <Row
              label="Chat ID"
              value={s.telegram.hasChatId ? "Configured ✓" : "Not set"}
              valueClass={s.telegram.hasChatId ? "text-emerald-400" : "text-muted-foreground/50"}
            />
            <InfoBox>
              Set <code className="text-accent/80">Telegram:BotToken</code> and{" "}
              <code className="text-accent/80">Telegram:ChatId</code> in appsettings to receive
              circuit-breaker and fill alerts.
            </InfoBox>
          </Section>

          {/* Risk */}
          <Section title="Risk Controls" eyebrow="03 · Risk Governor">
            <Row
              label="Max Drawdown"
              value={`${s.risk.maxDrawdownPercent.toFixed(1)}%`}
              valueClass="text-rose-400"
            />
            <Row label="Default Leverage" value={`${s.risk.defaultLeverage}×`} />
            <Row label="Max Requested Leverage" value={`${s.risk.maxRequestedLeverage}×`} />
            <InfoBox>
              These parameters are enforced at runtime by{" "}
              <code className="text-accent/80">RiskSupervisorService</code>. Edit{" "}
              <code className="text-accent/80">Risk:Supervisor:MaxDrawdownPercent</code> and{" "}
              <code className="text-accent/80">RiskGovernor:MaxRequestedLeverage</code> to adjust.
            </InfoBox>
          </Section>

          {/* Paper Trade */}
          <Section title="Paper Trade" eyebrow="04 · Simulation">
            <Row label="Timeframe" value={s.paper.timeframe} />
            <Row label="Default Leverage" value={`${s.paper.defaultLeverage}×`} />
            <Row label="Max Leverage" value={`${s.paper.maxLeverage}×`} />
            <Row label="Max Concurrent Positions" value={s.paper.maxConcurrentPositions.toString()} />
            <Row
              label="Max Total Exposure"
              value={`${s.paper.maxTotalExposurePercent.toFixed(0)}%`}
            />
            <Row
              label="Sync With Exchange"
              value={s.paper.syncWithExchange ? "Enabled" : "Disabled"}
              valueClass={s.paper.syncWithExchange ? "text-emerald-400" : "text-muted-foreground/50"}
            />
            <InfoBox>
              Configure via <code className="text-accent/80">Paper:*</code> section in appsettings or{" "}
              <code className="text-accent/80">appsettings.Paper.json</code>.
            </InfoBox>
          </Section>

          {/* Promotion Gate */}
          <Section title="Promotion Gate" eyebrow="05 · Champion/Challenger">
            <div className="grid grid-cols-2 gap-x-8 gap-y-1.5">
              <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/40 col-span-2 mt-1">
                Backtest → Paper
              </p>
              <Row label="Min Trades" value={s.promotion.backtestMinTrades.toString()} />
              <Row label="Min PF" value={s.promotion.backtestMinProfitFactor.toFixed(2)} />
              <Row label="Min Win Rate" value={`${(s.promotion.backtestMinWinRate * 100).toFixed(0)}%`} />

              <p className="text-[9px] font-black uppercase tracking-widest text-muted-foreground/40 col-span-2 mt-2">
                Paper → Live
              </p>
              <Row label="Min Trades" value={s.promotion.paperMinTrades.toString()} />
              <Row label="Min PF" value={s.promotion.paperMinProfitFactor.toFixed(2)} />
              <Row label="Min Win Rate" value={`${(s.promotion.paperMinWinRate * 100).toFixed(0)}%`} />
            </div>
          </Section>

          {/* Alert channels — sound + desktop notifications */}
          <NotificationPrefsCard />

          {/* Restart onboarding tour */}
          <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5 flex items-center justify-between gap-4 flex-wrap">
            <div>
              <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
                Onboarding
              </p>
              <p className="text-[12px] font-serif italic text-muted-foreground/70">
                Replay the welcome tour — useful when handing the console to a teammate.
              </p>
            </div>
            <button
              onClick={restartOnboarding}
              className="px-3 py-1.5 rounded-md text-[10px] font-black uppercase tracking-widest bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25 transition-colors"
            >
              Restart tour
            </button>
          </div>

          <RuntimeOverridesCard />

          {/* Config file reference */}
          <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5">
            <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-2">
              06 · Config Files
            </p>
            <p className="text-xs text-muted-foreground/60 mb-3 leading-relaxed">
              All settings are loaded from the files below at startup. Changes require a service
              restart. Secrets should use environment variables or a secrets manager, never be
              committed to source control.
            </p>
            <ul className="space-y-1.5 text-xs font-mono">
              {[
                "appsettings.json",
                "appsettings.Paper.json",
                "appsettings.Development.json",
                ".env (VITE_API_BASE_URL, VITE_API_KEY)",
              ].map((f) => (
                <li key={f} className="flex items-center gap-2">
                  <span className="text-accent/40">▸</span>
                  <span className="text-muted-foreground/70">{f}</span>
                </li>
              ))}
            </ul>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function Section({
  title,
  eyebrow,
  children,
}: {
  title: string;
  eyebrow: string;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-white/5 bg-[#0D1117]/60 backdrop-blur-xl p-5 space-y-3">
      <div>
        <p className="text-[9px] font-black uppercase tracking-[0.3em] text-accent/70 mb-0.5">
          {eyebrow}
        </p>
        <p className="text-sm font-bold">{title}</p>
      </div>
      <div className="space-y-2">{children}</div>
    </div>
  );
}

function Row({
  label,
  value,
  valueClass = "",
}: {
  label: string;
  value: string;
  valueClass?: string;
}) {
  return (
    <div className="flex items-center justify-between text-xs">
      <span className="text-muted-foreground/50">{label}</span>
      <span className={cn("font-mono font-bold", valueClass || "text-foreground/80")}>{value}</span>
    </div>
  );
}

function InfoBox({ children }: { children: React.ReactNode }) {
  return (
    <div className="mt-2 rounded-md bg-accent/5 border border-accent/10 px-3 py-2 text-[10px] text-muted-foreground/60 leading-relaxed">
      {children}
    </div>
  );
}
