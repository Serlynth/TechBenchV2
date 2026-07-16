# TechBench V2

TechBench V2 is the multi-user successor to TechBench 1.x. It keeps the existing Windows/WPF workstation experience while moving meaningful application data into a shared SQL Server database.

The original `TechBench` workspace is not modified. V1 and V2 have separate product identities, executables, mutex names, settings, credential namespaces, and update paths.

V2 also has its own Velopack package identity and GitHub Releases feed:
`https://github.com/Serlynth/TechBenchV2-Releases`. It never checks or installs
packages from the V1 update repository.

Current milestone: `2.0.0-alpha.1` — Phase 1.

## Current status

This repository is an alpha under active architectural conversion.

Phase 1 establishes:

- the independent TechBench V2 application identity
- direct WPF-to-SQL Server connectivity
- Windows Integrated Authentication
- Active Directory/database-role authorization
- database schema/version and current-user checks
- the shared client list as the first direct-SQL workflow

The remaining workflows are not yet considered ported. Today, history, search, tickets, templates, settings, drafts, imports, posting logs, WHD synchronization, Sage synchronization, and posting coordination may still contain V1-derived local repository behavior until their SQL Server phases are completed.

Do not treat this alpha as a production-ready multi-user release. The older API,
PostgreSQL, Identity-login, Docker, and token-client prototype have been removed.
The remaining SQLite code supports only workflows that have not yet reached their
direct-SQL migration phase.

## Final deployment model

There is no TechBench web server, API process, container, or background server service.

```text
TechBench V2 WPF client
    -> Microsoft.Data.SqlClient
    -> Windows Integrated Authentication
    -> existing SQL Server 2016
    -> TechBench database at compatibility level 130
```

SQL Server is the source of truth for shared clients, tickets, work entries, Personal Notes, drafts, templates, Common Links, settings, posting state, synchronization state, imports, and audit history.

WHD and Sage operations continue to run on the workstation. WHD and Sage secrets remain protected in Windows Credential Manager.

See [docs/V2-ARCHITECTURE.md](docs/V2-ARCHITECTURE.md) for the target schema, security boundary, phases, and migration rules.

## Authentication and access

Users do not create a TechBench account or enter a database password. The application connects as the current Windows user.

The DBA maps approved Active Directory groups into database roles such as:

- `TechBench_Technician`
- `TechBench_Manager`
- `TechBench_Admin`
- `TechBench_SyncOperator`

The database enforces permissions. WPF button visibility is only a user-interface convenience and is not an authorization boundary.

Normal application users should receive execution rights on approved stored procedures, not broad direct access to the underlying tables.

## Requirements

Development:

- Windows
- .NET SDK 8.0.422, pinned by `global.json`

Deployment:

- domain-joined or trusted-domain Windows workstations
- an existing SQL Server 2016 instance
- a `TechBench` database at compatibility level 130
- approved Active Directory groups mapped by the DBA
- TCP connectivity from workstations to SQL Server
- TLS 1.2 and a server certificate trusted by the workstations
- SQL Server Full-Text Search if the full search implementation will use it

SQL Server 2016 extended support ended July 14, 2026. Production deployment requires confirmation of SP3/current approved patches and Extended Security Updates coverage, or a documented database upgrade plan:

<https://learn.microsoft.com/lifecycle/products/sql-server-2016>

## Build and test

Restore and build the V2 desktop solution:

```powershell
dotnet restore TechBenchV2.sln
dotnet build TechBenchV2.sln
dotnet test TechBench.Tests\TechBench.Tests.csproj -c Release
```

The production client remains x86 because Sage desktop integration requires it. Tests may use x64 where configured.

SQL integration, security, and migration tests must also run against an actual SQL Server 2016 staging database. Testing on a newer SQL Server with compatibility level 130 is not sufficient to detect every engine-version incompatibility.

## Database provisioning

The database is provisioned and upgraded with DBA-owned, versioned SQL scripts or a database project.

The DBA deployment is responsible for:

- creating the database and setting compatibility level 130
- creating schemas, tables, indexes, stored procedures, and security policies
- creating database roles and mapping AD groups
- configuring the trusted TLS endpoint
- recording the schema version
- configuring backups, integrity checks, retention, and restore procedures

The desktop application does not apply production schema migrations and does not require a companion service to be started.

A representative connection string is:

```text
Server=tcp:sqlserver.example.local,1433;
Database=TechBench;
Integrated Security=true;
Encrypt=true;
TrustServerCertificate=false;
Application Name=TechBenchV2;
Connect Timeout=5;
```

No username or password belongs in this connection string. The server and database names are deployment configuration.

## Data and migration rules

- Never point V2 at a live V1 SQLite database.
- Never put a SQLite database on a network share.
- Import only from a verified copy of a V1 database.
- Assign every imported work entry to an explicit Windows/AD SID.
- Preserve legacy identifiers in an import mapping table.
- Deduplicate clients using WHD/Sage external identities before name similarity.
- Verify row counts, relationships, posting locks, ownership, and sample note content before cutover.
- Do not use V1 and V2 as dual writable systems.

V1 remains untouched and available for rollback or historical reference. After final migration, operational procedures must treat it as archive-only.

## Phase roadmap

1. Phase 1 — integrated SQL connection, roles, schema checks, and shared clients.
2. Phase 2 — Today work entries, owner-only Personal Notes, links, drafts, and concurrency.
3. Phase 3 — tickets, history, search, templates, links, settings, imports, and reporting.
4. Phase 4 — centralized posting attempts and WHD/Sage synchronization coordination.
5. Phase 5 — V1 migration, pilot, backup/restore rehearsal, and production cutover.

Until the relevant phase is complete, a screen that still uses transitional local persistence is not part of the shared multi-user guarantee.
