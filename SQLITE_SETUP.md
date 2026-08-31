# SQLite Build & Deployment — WarcraftAutoBalance v2.9

v2.9 uses `Microsoft.Data.Sqlite` directly. Gameplay statistics remain in RAM;
SQLite is only the persistent backend.

## NuGet dependency

Add the normal Microsoft package to the WarcraftAutoBalance project:

```bash
dotnet add package Microsoft.Data.Sqlite
```

Use the current stable version compatible with the server's .NET 8 build.

The normal `Microsoft.Data.Sqlite` package uses SQLitePCLRaw and brings the
`bundle_e_sqlite3` native SQLite bundle by default.

Do not copy only `Microsoft.Data.Sqlite.dll` manually. Publish/deploy the
managed dependencies and the native runtime asset produced by NuGet.

## Recommended release build

```bash
dotnet restore
dotnet build -c Release
```

If the project uses an explicit RuntimeIdentifier for the Linux CS2 host, publish
for that exact runtime and deploy the resulting native SQLite asset with the
plugin.

Examples may include:

```text
linux-x64
linux-arm64
win-x64
```

Use the runtime that actually hosts CounterStrikeSharp.

## Database files

Runtime database:

```text
balance.db
```

SQLite WAL mode can additionally create:

```text
balance.db-wal
balance.db-shm
```

while the server is running. This is normal.

Backups should either be taken while the database is closed or use an
SQLite-aware backup/checkpoint process. Do not assume copying only `balance.db`
during active WAL writes captures the newest committed pages.

## Legacy migration

On first startup:

```text
balance.db missing/empty
+ balance_data.json exists
        ↓
create schema
        ↓
transactionally import:
  Players
  RecentRounds
  RaceStats
  RoundNumber
        ↓
commit
        ↓
rename JSON to:
balance_data.json.migrated-YYYYMMDD-HHMMSS
```

If migration fails, the original JSON is intentionally left untouched.
