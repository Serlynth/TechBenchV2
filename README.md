# TechBench V2

TechBench V2 is the multi-user successor to TechBench 1.x. It keeps the existing Windows/WPF workstation experience while moving all business and operational data into the shared SQL Server database.

The original TechBench workspace is not modified. V1 and V2 have separate product identities, executables, mutex names, settings, credential namespaces, packages, and update feeds.

Current milestone: `2.0.0-alpha.13` - the server-backed client, owner-scoped V1 migration, server-owned WHD and Sage synchronization, Admin-only read-only user preview, and the server-local TechBench Server Manager GUI are implemented. Alpha.13 includes alpha.11's complete organization-wide WHD/Sage configuration move, alpha.12's corrected GitHub release discovery, and a Windows PowerShell 5.1 SQL connection-builder correction. The client retains only each user's personal WHD posting credential and workstation-specific Sage posting settings. It still requires deployment to the real SQL Server 2016 instance plus domain-user, live-WHD, live-Sage, and server-update smoke testing before it should be treated as production-ready.

## What V2 stores where

SQL Server is the source of truth for:

- clients, organization-wide aliases, external WHD/Sage identities, and canonical client matching
- tickets and ticket status options
- work entries, Personal Notes, links, follow-ups, and search/history
- editor recovery drafts
- organization-wide Common Links, shared templates, and the canonical tag catalog
- administrator-managed organization settings, including WHD/Sage defaults and the WHD automatic-sync schedule, plus user-scoped identity settings
- posting logs, durable posting attempts, and posting leases
- server-side WHD synchronization requests, leases, cursors, health, technicians, groups, and AD-user mappings
- server-side Sage customer synchronization requests, leases, health, and snapshot results
- import batches, legacy-ID mappings, and audit history

The workstation keeps only non-business state:

- the SQL Server address and database name, without credentials
- a generated device ID
- theme, window position/size/state, the shared-data view refresh interval, and similar device preferences
- device-specific Sage time-ticket, update, and browser options
- each user's personal WHD/Sage posting secrets protected by Windows Credential Manager
- installed application/update artifacts and temporary files explicitly created by the user

The production client packages the SQLite runtime only for the read-only **Import V1 Database...** action. It never creates or uses a local SQLite business-data store, and the legacy local repository/providers remain excluded from production builds. V2 has no offline business-data store or client-side database-backup feature, so SQL Server must be reachable to use the application. SQL Server backup and restore are DBA responsibilities.

## Deployment model

There is no TechBench web server, public API, or container. V2 includes one small internal Windows service for organization-wide WHD reads and Sage customer synchronization.

The service host also receives an administrator-only TechBench Server Manager GUI. A consoleless Start Menu launcher requests elevation and reports startup errors instead of flashing a command window and disappearing. The Manager controls the fixed TechBench service, owns organization-wide WHD/Sage synchronization configuration and manual requests, rotates the machine-protected WHD/Sage secrets, and installs verified service updates while preserving SQL-owned configuration and the existing Windows service identity. It can be minimized to the notification area and restored from its tray icon. It is an operations tool, not an application server or a general workstation-settings editor.

~~~text
WHD
    -> HTTPS with one dedicated WHD service identity
    -> TechBench Sync Service (Windows service, x64)
Sage 50 customer data
    -> 32-bit System DSN and isolated x86 ODBC worker
    -> the same TechBench Sync Service
    -> approved tb_service stored procedures over encrypted TDS
    -> TechBench database, schema version 7

TechBench V2 WPF clients (x86)
    -> approved tb_app stored procedures over encrypted TDS
    -> Windows Integrated Authentication
    -> the same TechBench database
~~~

The service performs the initial full organization ticket import, five-minute overlapping ticket deltas, and daily reference refreshes for WHD clients, statuses, technicians, and group memberships. SQL Server stores the durable queue, cursor, and health state. Admin clients configure the non-secret WHD endpoint/service username, request an immediate run, and map AD users to WHD technicians; the WHD credential exists only as machine-protected data on the service host. Explicit closed or deleted records update the shared snapshot, but omission never closes a ticket.

Sage customer synchronization is manual-only. A TechBench Admin queues it from Settings, the Windows service runs the isolated 32-bit ODBC reader, and SQL Server validates every returned row before atomically applying the customer snapshot. Empty, malformed, over-length, or duplicate-ID snapshots change no customer data. If an established snapshot would remove at least 10 and at least 25 percent of existing Sage mappings, SQL Server rejects it and Settings exposes a separate Admin confirmation action showing the proposed counts. The approval references that rejected request and is accepted only when the rerun has exactly the same read, existing, and stale counts; a changed rerun is blocked for fresh review. The server Sage password is machine-protected on the service host. Personal Sage desktop time-ticket posting remains workstation-side and continues to use each technician's own protected credential and local Sage preferences.

The V2 client uses short-lived pooled connections and stored procedures. It does not hold a database transaction open while calling WHD or Sage.

## Authentication and authorization

Users do not create a separate TechBench username or enter a database password. SQL Server authenticates the Windows identity of the person running TechBench.

The connection screen lets an authenticated TechBench Admin enter the domain login of a registered non-Admin user who has opened V2 within the past hour. That one-hour authorization-freshness window prevents a user removed from the TechBench AD groups from remaining indefinitely eligible through cached role flags. The preview is short-lived, server-authorized, and read-only; it does not authenticate as that person, expose that person's Personal Notes or editor draft, or permit writes. SQL Server switches every preview connection to a restricted `WITHOUT LOGIN` database principal, and the window displays a persistent preview warning.

The prepared CSRI mapping is:

| Active Directory principal | Database roles |
|---|---|
| `CSRI\TechBench_Users` | `tb_role_user` |
| `CSRI\TechBench_Admins` | `tb_role_user`, `tb_role_manager`, `tb_role_admin`, `tb_role_sync_operator` |
| `CSRI\TechBench_Sync` | `tb_role_sync_service` only |

The database derives the caller's durable owner identity from the Windows SID. Stored procedures enforce owner and role checks, and a SQL Server row-level-security policy applies the WHD technician/group assignment boundary to every ticket-table access path. Hiding or disabling a WPF control is only a user-interface convenience and is not the authorization boundary.

Only members of `CSRI\TechBench_Admins` may change organization-wide configuration or manually queue WHD or Sage synchronization. Only the dedicated service principal can claim those jobs and apply organization-wide snapshots. Ordinary users can read their mapped direct/group WHD tickets and the resulting shared catalogs while managing their own work, notes, drafts, credentials, and user-specific identifiers.

Settings does not provide a second manual Sage customer-mapping editor. Administrators manage customer matching in the dedicated Client Matching workspace so there is one shared, audited workflow.

Application users receive execution rights on approved stored procedures. They are not granted broad `db_datareader`, `db_datawriter`, `db_owner`, or direct table DML access.

## Requirements

Development:

- Windows
- .NET SDK 8.0.422, pinned by `global.json`

Deployment:

- domain-joined or trusted-domain Windows workstations
- SQL Server 2016 at compatibility level 130
- the `TechBench` database at schema version 7
- three distinct CSRI Active Directory principals mapped by the database deployment
- a domain-joined x64 Windows service host with outbound HTTPS access to WHD and encrypted SQL access
- the supported 32-bit Sage ODBC driver and a server-local 32-bit **System DSN** on that service host
- TCP connectivity from workstations to SQL Server
- TLS 1.2 and a SQL Server certificate trusted by the workstations
- a DBA-owned, tested SQL Server backup, integrity-check, and restore process

The production client remains x86 because Sage desktop integration requires it.

SQL Server 2016 extended support ended July 14, 2026. Before production use, confirm the installed service pack/security-update posture and Extended Security Updates coverage, or document a database upgrade plan. See the [Microsoft SQL Server 2016 lifecycle](https://learn.microsoft.com/lifecycle/products/sql-server-2016).

## Database deployment

The DBA-owned deployment package is in [database/sqlserver2016](database/sqlserver2016). The standalone script creates or upgrades the database and installs the complete schema-version-7 stored-procedure contract:

`database/sqlserver2016/Deploy-CSRI-Standalone.sql`

Run it in SSMS while connected to `CSRI-SQL` as an existing SQL Server sysadmin, with **Query > SQLCMD Mode** enabled. The script has no external file references and contains no password.

A representative client connection string is:

~~~text
Server=CSRI-SQL.CSRI.local;
Database=TechBench;
Integrated Security=True;
Encrypt=True;
TrustServerCertificate=False;
MultipleActiveResultSets=False;
Application Name=TechBench V2;
~~~

No SQL username, `sa` password, or other credential belongs in the client connection configuration.

The desktop application and Server Manager check schema compatibility and refuse an incompatible deployment. Version `2.0.0-alpha.13` requires database schema version `7`, including the service-only WHD and Sage ingestion contracts and restricted Admin preview boundary. Alpha.13 introduces no database migration.

### Coordinated alpha.13 upgrade

The database and client must be upgraded as one planned cutover:

1. Back up the `TechBench` database.
2. Stop the existing V2 clients and sync service, run the complete schema-version-7 standalone deployment, and confirm all verification output passes.
3. Install the alpha.13 x64 TechBench Sync Service package under `CSRI\TechBench_Sync`, preserving its machine-protected WHD/Sage credentials. The installer adds or repairs TechBench Server Manager, its consoleless launcher, icon, and Start Menu shortcut.
4. On the service host, create the 32-bit Sage **System DSN**, grant the service identity the required Sage read access, and provision the separate machine-protected Sage password.
5. Open Server Manager as a TechBench Admin and configure the WHD endpoint, authentication mode, service username, five-minute schedule, server Sage DSN/username, shared activity item ID, and AD-to-WHD technician mappings. Install the alpha.13 client on workstations; each technician configures only their personal posting credentials and local workstation options.
6. Test with at least one ordinary domain user and one TechBench administrator. Confirm the initial WHD full sync, a later automatic delta, a manually requested Sage customer sync, malformed/empty snapshot rejection, the explicit large-removal confirmation path, service health, and direct/group ticket visibility.
7. Verify ordinary users cannot queue either server job or change shared configuration. Verify an Admin can open a read-only preview, cannot write through it, and cannot see another user's Personal Note or editor draft.
8. Verify shared clients, tickets, canonical customer matching, aliases, tags, templates, Common Links, work entries, Personal Note privacy, drafts, posting coordination, automatic refresh, and optimistic-concurrency conflicts.
9. Have the DBA configure and test ongoing SQL Server backups before production data entry.

The alpha.9 Manager has a Windows PowerShell 5.1 release-list parsing defect and cannot discover alpha.10 or later when several GitHub releases exist. Bootstrap alpha.13 once by downloading and extracting its service ZIP, closing the old Manager, and running `TechBench-ServerManager.ps1` from the extracted package as Administrator. In that Manager, select **Check for updates** and **Download & Install**. Alpha.13 can discover the release and construct its schema-verification connection correctly on Windows PowerShell 5.1; the update installs the repaired Manager companions and Start Menu shortcut.

Do not deploy only one side. The alpha.13 client and service require schema version 7; earlier alpha clients keep obsolete organization-sync controls in their Settings page.

Users newly added to an AD group should sign out of Windows and sign back in before testing so their Windows security token includes the new membership.

## Build and test

~~~powershell
dotnet restore TechBenchV2.sln
dotnet build TechBenchV2.sln -c Release
dotnet test TechBench.Tests\TechBench.Tests.csproj -c Release
.\scripts\Publish-TechBenchRelease.ps1 -Version 2.0.0-alpha.13
.\scripts\Publish-TechBenchServer.ps1 -Version 2.0.0-alpha.13
~~~

Inspect and smoke-test both local packages before publishing. For an approved
release, run `Publish-TechBenchRelease.ps1 -Publish` first; it creates the
Velopack release and predictable `TechBenchV2Setup.exe` download. Then run
`Publish-TechBenchServer.ps1 -Publish`; it requires that existing release and
attaches the versioned service ZIP and standalone SQL file with SHA-256
sidecars. Published assets are not overwritten.

The client release script rejects any packaged `.db`, `.sqlite`, or `.sqlite3` data file and verifies that the read-only V1 importer dependency is present. The packaged SQLite runtime is an import reader only; the production project still excludes the V1 local repository, database-location service, and local client/ticket providers.

Unit and contract tests do not replace the required integration run against the actual SQL Server 2016 instance. Testing only on a newer SQL Server at compatibility level 130 cannot detect every engine-version difference.

## V1 migration and rollback rules

- Never point V2 at a live V1 SQLite database.
- Never place a SQLite database on a network share.
- Close TechBench V1, then select its closed local database or a verified copy. The importer rejects active SQLite sidecar files and detects a source file that changes during the read.
- Let SQL Server derive imported-work ownership from the authenticated Windows/AD SID; the client never supplies an owner SID.
- Preserve legacy identifiers through the server-side mapping tables.
- Have an Admin run the shared WHD/Sage sync before employee imports. The server resolves source-qualified WHD identities, Sage customer IDs, organization aliases, and exact names directly against the authoritative SQL tables; alias/name matches must be unambiguous and it never performs a fuzzy or capped-list automatic match.
- Verify counts, relationships, ownership, posting state, and sample note content before cutover.
- Do not run V1 and V2 as dual writable production systems.

V1 remains untouched and available for rollback or historical reference. Its data is not automatically migrated merely by installing alpha.13; each user uses **Settings > Import V1 Database...**, reviews the preview, and explicitly starts their own import. Work history, Personal Notes, entry tags, follow-up state, posting state/history, and note links move to that user's SQL-owned records. Equivalent legacy link rows may share one canonical SQL relationship. A resumed batch counts mappings first accepted by that same batch as imported, while a later batch skips unchanged mappings. Dependent links and posting logs attach only through work-entry mappings accepted by the current batch, and a successful completion must account for every read item with zero errors. A user may abandon their own stale active V1 batch before restarting. Shared configuration, credentials, editor drafts, active posting attempts, and local caches are intentionally excluded.

For implementation details, see [docs/V2-ARCHITECTURE.md](docs/V2-ARCHITECTURE.md). For deployment, see the [database runbook](database/sqlserver2016/README-Deploy.md) and [sync-service runbook](docs/WHD-SYNC-SERVICE.md).
