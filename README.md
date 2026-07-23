# TechBench V2

TechBench V2 is the multi-user successor to TechBench 1.x. It keeps the existing Windows/WPF workstation experience while moving all business and operational data into the shared SQL Server database.

The original TechBench workspace is not modified. V1 and V2 have separate product identities, executables, mutex names, settings, credential namespaces, packages, and update feeds.

Current milestone: `0.5.35` - TechBench V2 uses the `0.5.x` development release line; "V2" remains the product generation and is assumed. Credentials workbook columns are now discovered from headers and stored as flexible encrypted fields, so newly appended columns appear automatically after synchronization. This patch also aligns every Server Manager schema gate with schema version 12.

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
- encrypted credential records plus synchronization requests, leases, and health
- active TechBench client heartbeats, Admin-issued update/sign-out requests, and client responses
- import batches, legacy-ID mappings, and audit history

The workstation keeps only non-business state:

- the SQL Server address and database name, without credentials
- a generated device ID
- theme, window position/size/state, the shared-data view refresh interval, and similar device preferences
- update and browser options
- each user's personal WHD posting secret protected by Windows Credential Manager
- installed application/update artifacts and temporary files explicitly created by the user

The production client packages the SQLite runtime only for the read-only **Import V1 Database...** action. It never creates or uses a local SQLite business-data store, and the legacy local repository/providers remain excluded from production builds. V2 has no offline business-data store or client-side database-backup feature, so SQL Server must be reachable to use the application. SQL Server backup and restore are DBA responsibilities.

## Deployment model

There is no TechBench web server, public API, or container. V2 includes one small internal Windows service for organization-wide WHD reads and Sage customer synchronization.

The service host is installed or repaired by the administrator-only, self-contained `TechBenchServerSetup.exe` and receives the self-contained `TechBench.ServerManager.exe`. The Start Menu shortcut targets the Manager executable directly and Windows requests elevation from both manifests; users do not extract packages or run commands. The Manager controls the fixed TechBench service, SQL connection configuration, organization-wide WHD/Sage/Credentials configuration and manual requests, machine-protected external-system secrets, and verified service updates while preserving SQL-owned configuration and the existing Windows service identity. It minimizes to the notification area and is an operations tool, not an application server or a general workstation-settings editor.

~~~text
WHD
    -> HTTPS with one dedicated WHD service identity
    -> TechBench Sync Service (Windows service, x64)
Sage 50 customer data
    -> 32-bit System DSN and isolated x86 ODBC worker
    -> the same TechBench Sync Service
    -> approved tb_service stored procedures over encrypted TDS
Password-encrypted Credentials workbook on the internal file share
    -> read-only daily import by the same TechBench Sync Service
    -> flexible encrypted credential fields and explicit reveal procedures
    -> TechBench database, schema version 12

TechBench V2 WPF clients (x86)
    -> approved tb_app stored procedures over encrypted TDS
    -> Windows Integrated Authentication
    -> the same TechBench database
~~~

The service performs the initial full organization ticket import, five-minute overlapping ticket deltas, and daily reference refreshes for WHD clients, statuses, technicians, and group memberships. SQL Server stores the durable queue, cursor, and health state. TechBench Admins use the server-local Manager to configure the non-secret WHD endpoint/service username and map AD users to WHD technicians. They can request immediate WHD and Sage runs from either Server Manager or the client Admin Center; the service performs the work. The WHD credential exists only as machine-protected data on the service host. Explicit closed or deleted records update the shared snapshot, but omission never closes a ticket.

Sage customer synchronization is manual-only. A TechBench Admin queues it from Server Manager, the Windows service runs the isolated 32-bit ODBC reader, and SQL Server validates every returned row before atomically applying the customer snapshot. Empty, malformed, over-length, or duplicate-ID snapshots change no customer data. If an established snapshot would remove at least 10 and at least 25 percent of existing Sage mappings, SQL Server rejects it and Server Manager exposes a separate Admin confirmation action showing the proposed counts. The approval references that rejected request and is accepted only when the rerun has exactly the same read, existing, and stale counts; a changed rerun is blocked for fresh review. The server Sage password is machine-protected on the service host. Personal Sage time-ticket posting remains workstation-side because it automates the signed-in employee's Sage Desktop session, but it needs only that user's server-stored employee ID and activity item ID; no Sage ODBC setting or credential is stored or used by the client.

Credentials synchronization is server-owned and defaults to 4:00 AM server local time. A TechBench Admin enters the workbook UNC path only in Server Manager; no organization-specific location is embedded in the application or deployment package, and ordinary clients do not receive the saved path. The service opens the configured workbook with read sharing, so another employee may keep it open for editing. It copies one stable snapshot into memory, opens the first visible worksheet with the server-local DPAPI-protected workbook password, requires one uniquely named `Client` column, discovers every other nonblank header at runtime, and atomically encrypts each field value in SQL Server. Columns may be reordered or appended without an application release; a newly added column appears in Client Credentials after the next successful synchronization. Blank or duplicate headers, duplicate clients, over-length data, and invalid workbooks leave the current SQL snapshot intact. Every TechBench user may search, reveal, and copy; only Admins can configure or manually sync it.

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

Only members of `CSRI\TechBench_Admins` may change organization-wide configuration or manually queue WHD, Sage, or Credentials synchronization. The client Admin Center can queue WHD/Sage work, display active TechBench client sessions, send notices that let recipients acknowledge and write a response, review recent responses, and request a cooperative TechBench-only sign-out. Before a forced TechBench sign-out, the client saves the complete current editor state as that user's SQL recovery draft without posting to WHD or Sage. If that save fails, the client remains open and reports the failure to the Admin Center. It cannot sign a user out of Windows or kill a SQL session. Only the dedicated service principal can claim synchronization jobs and apply organization-wide snapshots. All authenticated TechBench users may search and explicitly reveal/copy the shared credentials; ordinary users can otherwise read their mapped direct/group WHD tickets and the resulting shared catalogs while managing their own work, notes, drafts, personal posting credentials, and user-specific identifiers.

Settings does not provide a second manual Sage customer-mapping editor. Administrators manage customer matching in the dedicated Client Matching workspace so there is one shared, audited workflow.

Application users receive execution rights on approved stored procedures. They are not granted broad `db_datareader`, `db_datawriter`, `db_owner`, or direct table DML access.

## Requirements

Development:

- Windows
- .NET SDK 8.0.422, pinned by `global.json`

Deployment:

- domain-joined or trusted-domain Windows workstations
- SQL Server 2016 at compatibility level 130
- the `TechBench` database at schema version 12
- three distinct CSRI Active Directory principals mapped by the database deployment
- a domain-joined x64 Windows service host with outbound HTTPS access to WHD and encrypted SQL access
- the supported 32-bit Sage ODBC driver and a server-local 32-bit **System DSN** on that service host
- TCP connectivity from workstations to SQL Server
- TLS 1.2 and a SQL Server certificate trusted by the workstations
- a DBA-owned, tested SQL Server backup, integrity-check, and restore process

The production client remains x86 because Sage desktop integration requires it.

SQL Server 2016 extended support ended July 14, 2026. Before production use, confirm the installed service pack/security-update posture and Extended Security Updates coverage, or document a database upgrade plan. See the [Microsoft SQL Server 2016 lifecycle](https://learn.microsoft.com/lifecycle/products/sql-server-2016).

## Database deployment

The DBA-owned deployment package is in [database/sqlserver2016](database/sqlserver2016). The standalone script creates or upgrades the database and installs the complete schema-version-12 stored-procedure contract:

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

The desktop application and Server Manager check schema compatibility and refuse an incompatible deployment. Version `0.5.35` requires database schema version `12`. If the database is newer than the client, the connection screen checks the public release channel and offers to install a compatible client before any workspace connection is attempted.

### Coordinated 0.5.35 upgrade

The database and client must be upgraded as one planned cutover:

1. Back up the `TechBench` database.
2. Stop the existing V2 clients and sync service, run the complete schema-version-12 standalone deployment, and confirm all verification output passes. If the Results grid reports a newly generated database-master-key recovery password, store it in the protected administrative password vault before closing SSMS.
3. Run the 0.5.35 `TechBenchServerSetup.exe` as Administrator. It installs or repairs the x64 TechBench Sync Service under `CSRI\TechBench_Sync`, preserves existing machine-protected secrets, restores the required service and Manager read-and-execute ACLs, and installs the compiled Manager and direct EXE Start Menu shortcut.
4. On the service host, create the 32-bit Sage **System DSN**, grant the service identity the required Sage read access, and provision the separate machine-protected Sage password.
5. Grant `CSRI\TechBench_Sync` read access to both the Credentials share and workbook. In Server Manager, open **Credentials**, enter the UNC path and confirm the 4:00 AM schedule, select **Save settings**, enter the workbook open password under **Workbook open password**, select **Save / Rotate**, then select **Sync now**. Install the 0.5.35 client on workstations.
6. Test with at least one ordinary domain user and one TechBench administrator. Confirm the initial WHD full sync, a later automatic delta, a manually requested Sage customer sync, malformed/empty snapshot rejection, the explicit large-removal confirmation path, service health, and direct/group ticket visibility.
7. Verify ordinary users cannot see the Admin Center, queue either server job, view active sessions, or change shared configuration. Verify an Admin can queue WHD/Sage work, view active TechBench clients, send a notice and receive a response, and request safe sign-out. With an unsaved entry open, verify forced sign-out restores the entry on the next launch without posting it; simulate a draft-save failure and verify the client remains open. Verify an Admin preview remains read-only and cannot see another user's Personal Note or editor draft.
8. Verify shared clients, tickets, canonical customer matching, aliases, tags, templates, Common Links, work entries, Personal Note privacy, drafts, posting coordination, automatic refresh, and optimistic-concurrency conflicts.
9. Have the DBA configure and test ongoing SQL Server backups before production data entry.

To replace or repair any earlier V2 server installation, download and run `TechBenchServerSetup.exe` as Administrator. The setup EXE contains and verifies the matching server payload, closes the old Manager, preserves the installed service identity, configuration, and protected secrets, replaces both programs, restarts the service, repairs the Start Menu shortcut, and opens the Manager. No ZIP extraction or command-line bootstrap is required.

The 0.5.35 client and service require the schema-version-12 procedure set. Upgrading from 0.5.32, 0.5.33, or 0.5.34 requires the matching SQL deployment before either application is updated.

Users newly added to an AD group should sign out of Windows and sign back in before testing so their Windows security token includes the new membership.

## Build and test

~~~powershell
dotnet restore TechBenchV2.sln
dotnet build TechBenchV2.sln -c Release
dotnet test TechBench.Tests\TechBench.Tests.csproj -c Release
.\scripts\Publish-TechBenchRelease.ps1 -Version 0.5.35
.\scripts\Publish-TechBenchServer.ps1 -Version 0.5.35
~~~

Inspect and smoke-test both local packages before publishing. For an approved
release, run `Publish-TechBenchRelease.ps1 -Publish` first; it creates the
Velopack release and predictable `TechBenchV2Setup.exe` download. Then run
`Publish-TechBenchServer.ps1 -Publish`; it requires that existing release and
attaches the one-click server installer, versioned service ZIP, and standalone SQL file with SHA-256
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

V1 remains untouched and available for rollback or historical reference. Its data is not automatically migrated merely by installing 0.5.35; each user uses **Settings > Import V1 Database...**, reviews the preview, and explicitly starts their own import. Work history, Personal Notes, entry tags, follow-up state, posting state/history, and note links move to that user's SQL-owned records. Equivalent legacy link rows may share one canonical SQL relationship. A resumed batch counts mappings first accepted by that same batch as imported, while a later batch skips unchanged mappings. Dependent links and posting logs attach only through work-entry mappings accepted by the current batch, and a successful completion must account for every read item with zero errors. A user may abandon their own stale active V1 batch before restarting. Shared configuration, credentials, editor drafts, active posting attempts, and local caches are intentionally excluded.

For implementation details, see [docs/V2-ARCHITECTURE.md](docs/V2-ARCHITECTURE.md). For deployment, see the [database runbook](database/sqlserver2016/README-Deploy.md) and [sync-service runbook](docs/WHD-SYNC-SERVICE.md).
