# PostgreSQL Migration Guide

**TL;DR:** Postgres support is already plumbed — `Npgsql.EntityFrameworkCore.PostgreSQL` is referenced, the provider is selected at runtime via `Persistence:Provider` or a `QuantPostgres` connection string, and `SqliteCompatibilityBootstrapper` auto-skips on non-SQLite. You just need to run a Postgres instance, set the connection string, and flip the flag.

---

## 1. Run Postgres (Docker, 1 command)

```bash
docker run -d --name tmq-postgres \
  -e POSTGRES_DB=thatsmyquant \
  -e POSTGRES_USER=tmq \
  -e POSTGRES_PASSWORD=CHANGE_ME \
  -p 5432:5432 \
  -v tmq-pgdata:/var/lib/postgresql/data \
  postgres:16-alpine
```

Or install natively on Windows: https://www.postgresql.org/download/windows/

---

## 2. Flip the flag (appsettings.Paper.json or appsettings.json)

Change two things:

```json
{
  "ConnectionStrings": {
    "QuantPostgres": "Host=localhost;Port=5432;Database=thatsmyquant;Username=tmq;Password=CHANGE_ME"
  },
  "Persistence": {
    "Provider": "postgres"
  }
}
```

`Persistence:Provider` accepts `sqlite`, `postgres`, `postgresql`, `npgsql`. Default is `sqlite`.

**Automatic fallback:** If you *only* add a `QuantPostgres` connection string (and leave `Persistence:Provider` empty), the resolver still picks Postgres — see `QuantDbConfigurationExtensions.ResolveProvider`.

---

## 3. Start any worker

On first start, EF Core `EnsureCreatedAsync` creates the full schema in Postgres (all tables + indexes) — no migrations to run. The SQLite-specific compatibility bootstrapper auto-skips (checked at `SqliteCompatibilityBootstrapper.cs:118`).

---

## 4. Migrating existing SQLite data (optional)

If you already have trade history in `quant.db` and want to preserve it:

### Option A: Hand-scripted copy (simple, ~100 rows/min)
```bash
# 1. Dump SQLite to CSV per table
sqlite3 quant.db <<'EOF'
.mode csv
.headers on
.output trades.csv
SELECT * FROM Trades;
.output signals.csv
SELECT * FROM Signals;
.quit
EOF

# 2. Bulk-import into Postgres
psql -U tmq -d thatsmyquant -h localhost <<'EOF'
\copy "Trades" FROM 'trades.csv' CSV HEADER;
\copy "Signals" FROM 'signals.csv' CSV HEADER;
EOF
```

### Option B: Start fresh
Easier for paper trade — no historical trades need to survive. Just point at an empty Postgres DB; the schema is auto-created on startup.

---

## 5. Connection pooling

EF Core's `AddDbContextPool` (already configured with `poolSize: 128`) works identically with Npgsql. Postgres itself handles concurrent writers far better than SQLite — multi-worker writes no longer serialize.

Optional: add PgBouncer in front for transaction-level pooling if you scale past 128 concurrent sessions.

---

## 6. Verification after switching

1. `docker logs tmq-postgres` — no errors.
2. Start worker → look for log line `Checking SQLite compatibility` — if present, provider still resolving to SQLite. Flip again.
3. Connect: `psql -U tmq -d thatsmyquant -c "\dt"` — should list `Trades`, `Signals`, `Accounts`, `OperatorUsers`, etc.
4. Run a paper trade cycle → verify a row lands: `SELECT COUNT(*) FROM "Trades";`

---

## 7. Rollback

Change `Persistence:Provider` back to `sqlite` (or remove the key). Workers resume against `quant.db` as if nothing changed. No schema rollback needed — Postgres DB just becomes dormant.

---

## 8. Why Postgres over Mongo (the decision)

- EF Core provider swap = **single file change** (connection string + flag). Mongo = rewrite every entity + query.
- ACID transactions on trade writes (critical for PnL integrity). Mongo's transactions are newer, more constrained.
- Join-heavy analytics queries (used by P&L attribution, Quant Lab folds). Mongo would require aggregation pipelines or duplication.
- TimescaleDB extension available for candle/tick time-series hypertables if you later need it.
- `jsonb` column type handles any future flexible-schema needs (ML feature store etc.) without leaving the relational world.

---

## 9. Known edge cases

- **JSON columns:** SQLite stores JSON as `TEXT`. Postgres stores as `jsonb`. If you've manually SELECT'd raw SQLite JSON blobs via string comparison, switch to `@>` or `->>` operators on Postgres.
- **Case sensitivity:** Postgres identifiers are case-sensitive when quoted. EF quotes by default (`"Trades"`, not `trades`). Ad-hoc psql queries need the quotes.
- **DateTimeOffset:** Npgsql maps to `timestamp with time zone` — UTC is preserved. No action needed.
- **Auto-generated IDs:** Postgres uses `bigserial` / `uuid` depending on entity; EF handles this automatically.
- **`SqliteCompatibilityBootstrapper`:** SQLite-only. On Postgres it calls `EnsureCreatedAsync` and returns. Real EF migrations (`dotnet ef migrations add …`) is the preferred path once on Postgres.

---

## 10. Recommended Postgres tuning for paper-trade scale

`postgresql.conf` (or docker env vars):
```
shared_buffers = 256MB
effective_cache_size = 1GB
work_mem = 16MB
maintenance_work_mem = 128MB
max_connections = 200
wal_level = replica
checkpoint_timeout = 15min
```

Defaults are fine for paper. Revisit only if you see `pg_stat_statements` showing slow queries in the top 10.
