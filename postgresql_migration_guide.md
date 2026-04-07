# PostgreSQL Migration Guide

## Goal

Move production persistence off SQLite and onto PostgreSQL while keeping SQLite as the local-development default.

## Current Behavior

The application now resolves the database provider from configuration:

1. `Persistence:Provider=PostgreSQL` or `postgres`
2. `ConnectionStrings:QuantPostgres=<postgres connection string>`

If those values are not present, the app falls back to SQLite with `ConnectionStrings:Quant`.

## Required Package

PostgreSQL mode requires:

`Npgsql.EntityFrameworkCore.PostgreSQL`

The runtime will throw a clear startup error if PostgreSQL is selected but the package is not installed.

## Suggested Configuration

```json
{
  "Persistence": {
    "Provider": "PostgreSQL"
  },
  "ConnectionStrings": {
    "QuantPostgres": "Host=127.0.0.1;Port=5432;Database=thatsmyquant;Username=tmq;Password=change-me"
  }
}
```

## Rollout Steps

1. Add the Npgsql EF Core provider package to the service projects.
2. Provision a PostgreSQL database and least-privilege user.
3. Add `Persistence:Provider=PostgreSQL`.
4. Add `ConnectionStrings:QuantPostgres`.
5. Run schema creation/migrations in a staging environment first.
6. Verify `/ready` returns healthy with PostgreSQL enabled.
7. Run the focused execution, persistence, and auth test suites before production cutover.

## Notes

1. SQLite remains the safest default for local/offline runs.
2. PostgreSQL should be the production target for concurrent writer workloads.
