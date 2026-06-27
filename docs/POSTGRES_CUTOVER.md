# Postgres cutover — flip-ready checklist

2026-06-27: the codebase is **code-ready** for Postgres. The remaining work is the live
cutover, which is gated on Docker (or a native Postgres) being available on the host. This
doc is the exact, minimal procedure to flip once that's in place.

## What's already done (no action needed)

- **Provider switch is config-driven.** `QuantDbConfigurationExtensions.UseConfiguredQuantDatabase`
  picks Postgres when `Persistence:Provider = postgres` **or** a `QuantPostgres` connection
  string is present. `Npgsql.EntityFrameworkCore.PostgreSQL` (9.0.4) is already referenced.
- **Schema creation already handles Postgres.** `SqliteCompatibilityBootstrapper.EnsureCompatibleSchemaAsync`
  calls `EnsureCreatedAsync()` for any non-SQLite provider, building the full schema from the
  EF model. `ef_compare` (the SQLite decimal shim seen in query logs) is an EF-internal SQLite
  thing — on Npgsql, EF emits native numeric comparisons, so nothing to change.
- **SQLite-only maintenance is guarded.** `SqliteMaintenanceHostedService` now no-ops when the
  provider isn't SQLite (WAL/optimize/backup/integrity all skipped), so the same worker binary
  runs cleanly on Postgres.

## Cutover steps

1. **Bring up Postgres.**
   ```powershell
   # set a real password in docker-compose.postgres.yml first (POSTGRES_PASSWORD: REPLACE_ME)
   docker compose -f docker-compose.postgres.yml up -d
   docker compose -f docker-compose.postgres.yml ps   # wait for healthy
   ```
   (Or install native PostgreSQL 16 and create db `thatsmyquant`, user `tmq`.)

2. **Point the worker at Postgres.** In `appsettings.Paper.json` (gitignored, local) add:
   ```json
   "Persistence": { "Provider": "postgres" },
   "ConnectionStrings": {
     "QuantPostgres": "Host=localhost;Port=5432;Database=thatsmyquant;Username=tmq;Password=<the password>"
   }
   ```
   Leaving `Persistence:Provider` unset (or removing `QuantPostgres`) reverts to SQLite — so
   rollback is a one-line config change.

3. **Restart via the launcher.** `EnsureCreatedAsync` provisions the schema on first boot.
   ```powershell
   Stop-Process -Name 'ThatsMyQuant.Bot' -Force
   .\Launch-PaperTrade-Clean.ps1 -SkipBuild
   ```
   Confirm `[HB BOOT]` and that there are no `SqliteMaintenance` lines (it logs
   "DB provider is 'Npgsql...'; ... maintenance disabled").

## Data migration (decision)

`EnsureCreatedAsync` builds an **empty** Postgres schema — the existing SQLite history
(closed positions, Trades, heartbeats) does **not** come across automatically. Options:
- **Fresh start (simplest):** acceptable for the paper/$2K demo cycle; pre-registered sample
  counts restart. Recommended unless the closed-trade history is needed for the kill switch's
  realized-Sharpe window.
- **Migrate data:** dump the SQLite tables and load into Postgres (e.g. via `pgloader`, or a
  one-off EF copy job). Heavier; only worth it to preserve realized PnL continuity.

## Rollback

Remove `Persistence:Provider`/`QuantPostgres` from `appsettings.Paper.json` and relaunch —
the worker is back on SQLite (which keeps getting 6-hourly `VACUUM INTO` backups under
`%LOCALAPPDATA%\ThatsMyQuant\backups`).
