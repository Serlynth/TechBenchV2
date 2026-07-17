# TechBench V2 architecture

## Status

TechBench V2 `2.0.0-alpha.6` implements the conversion from TechBench 1.x's local SQLite design to a shared SQL Server design, including an owner-scoped V1 migration contract and a dedicated Windows service for organization-wide WHD synchronization.

The production WPF runtime uses the SQL Server repository for every business and operational workflow. It packages `Microsoft.Data.Sqlite` only for the explicit, read-only V1 migration reader; production builds still exclude the legacy local repository, database-location service, and local client/ticket providers. V2 has no client-side business-database backup path; SQL Server protection is owned by DBA/operations.

The implementation is still an alpha until the schema-version-6 upgrade, service, and client are exercised against the actual SQL Server 2016 instance, real domain identities, and the live WHD server. "Implemented" does not mean "approved for production."

## Fixed boundaries

- TechBench V1 remains unchanged, independently buildable, and independently installable.
- TechBench V2 remains a Windows WPF application because Sage UI automation and Sage ODBC execute on the technician workstation.
- There is no TechBench API, web application, or container. One internal x64 Windows service owns organization-wide WHD reads.
- Each client connects directly to SQL Server using Windows Integrated Authentication over encrypted TDS.
- Active Directory membership maps to database roles.
- SQL Server owns all business, user, worklog, draft, posting, synchronization, import, and audit state.
- The workstation owns only non-business connection/device preferences, protected external-system secrets, updater artifacts, and user-created temporary/export files.
- V2 has no offline business-data store and never uses a SQLite database on a network share.
- The client never creates, schedules, verifies, or restores a SQL Server database backup.
- Personal WHD/Sage posting credentials remain in each user's Windows Credential Manager. The organization WHD credential is machine-protected on the service host and never stored in SQL Server, client JSON, packages, or source control.

## Runtime topology

~~~text
WHD server
    | HTTPS / dedicated WHD service identity
    v
TechBench WHD Sync Service (domain-joined Windows Server, x64)
    | tb_service procedures / Windows authentication / Encrypt=True
    v
CSRI-SQL.CSRI.local
    SQL Server 2016
    TechBench database
    compatibility level 130
    schema version 6
    ^
    | tb_app procedures / Windows authentication / Encrypt=True
    |
TechBench V2 WPF clients (domain-joined workstations, x86)
~~~

The client and service open short-lived pooled connections for stored-procedure calls. Neither holds a database transaction open during WHD HTTP, Sage ODBC, or Sage desktop automation.

A representative production connection string is:

~~~text
Server=CSRI-SQL.CSRI.local;
Database=TechBench;
Integrated Security=True;
Encrypt=True;
TrustServerCertificate=False;
MultipleActiveResultSets=False;
Application Name=TechBench V2;
Connect Timeout=15;
~~~

The server and database names are non-secret deployment configuration. Production workstations should trust the SQL Server certificate. Enabling `TrustServerCertificate` bypasses certificate-chain validation and is only appropriate for a deliberately controlled diagnostic environment.

## Startup, identity, and authorization

At startup the client:

1. Loads the non-secret SQL endpoint configuration.
2. Opens an integrated-authentication SQL connection.
3. Calls `tb_app.GetCurrentUserContext`.
4. Verifies that the database reports schema version `6`.
5. Receives the caller's Windows SID, login/display name, database instance ID, server UTC time, and effective role flags.
6. When the caller is an Admin, performs the one-time insert-missing template/Common Link seed if its server marker is absent and independently repairs missing WHD auto-sync defaults without overwriting Admin values.
7. Refuses startup when the database is unreachable, the schema is incompatible, or the caller has no TechBench role.

The Windows SID is the durable user/owner key. Login names are retained for display and audit history but are not the ownership key because names can change.

The deployed CSRI mappings are:

| Active Directory group | Database roles |
|---|---|
| `CSRI\TechBench_Users` | `tb_role_user` |
| `CSRI\TechBench_Admins` | `tb_role_user`, `tb_role_manager`, `tb_role_admin`, `tb_role_sync_operator` |
| `CSRI\TechBench_SyncService` | `tb_role_sync_service` only |

The database role meanings are:

- `tb_role_user`: use ordinary TechBench workflows, manage the caller's own work, and import that caller's V1 personal history.
- `tb_role_manager`: read approved team-level work and reports.
- `tb_role_admin`: manage organization-scoped clients, matching, aliases, Common Links, templates, shared configuration, synchronization, and audit views.
- `tb_role_sync_operator`: retained for upgrade compatibility and synchronization-history inspection; it grants no shared mutation authority by itself.
- `tb_role_sync_service`: claim/renew leased WHD work and apply validated WHD batches; it grants no interactive application or Admin authority.

Authorization is enforced in stored procedures, with a SQL Server row-level-security policy adding table-level defense for WHD tickets. The groups do not receive `db_owner`, `db_datareader`, `db_datawriter`, `db_ddladmin`, or direct application-table DML permission. UI visibility is not a security boundary.

Shared mutation and synchronization require the effective Admin role even if the caller has another technical role. Ordinary users can read shared catalogs but cannot change customer matching or aliases, Common Links, note templates, organization defaults, the WHD automatic-sync schedule, or WHD/Sage snapshot state.

## Client persistence contract

The WPF view models depend on `ITechBenchRepository`. The production composition root supplies `SqlServerTechBenchRepository`; no production path constructs the legacy SQLite repository.

The SQL repository:

- invokes SQL Server 2016-compatible stored procedures with typed parameters
- obtains identity from the SQL session instead of trusting a caller-supplied username
- maps SQL `rowversion` values into client models for optimistic concurrency
- sends the generated device ID for device-scoped drafts, leases, and synchronization coordination
- uses server UTC timestamps for durable state
- exposes asynchronous operations and cancellation while retaining the synchronous surface required by existing WPF call sites
- reports server capabilities rather than assuming optional SQL features are installed

The schema-version-2 search implementation uses stored-procedure filtering and reports SQL Full-Text Search as unavailable. It therefore does not require the optional Full-Text component for this milestone.

## SQL Server authoritative data

The `TechBench` database owns:

- registered users identified by Windows SID
- clients, organization-wide aliases, external identities, matching state, and merge audit
- tickets and ticket status options
- work entries, related-entry links, follow-ups, and search/history fields
- an Admin-curated organization-wide tag catalog used as the shared suggestion list; saving a work entry does not publish new catalog values
- owner-private Personal Notes and their WHD inclusion choice
- editor recovery drafts keyed by owner SID and device ID
- organization templates, with read compatibility for legacy personal templates
- organization-wide Common Links
- administrator-managed organization settings, including WHD/Sage defaults and the WHD automatic-sync enabled state and interval, plus owner-scoped user identity settings
- posting logs, attempts, outstanding-result state, and posting leases
- WHD and Sage synchronization leases and runs
- staged/imported records and legacy-ID mappings
- audit events
- database instance and schema migration metadata

Tables are separated into deployment, data, private, user, operations, audit, security, and application-procedure schemas. Ordinary clients do not access those tables directly.

### Ownership and privacy

Ordinary work is owned by the caller's Windows SID. Managers may read approved ordinary team work through manager-enabled procedure paths. Personal Notes are joined and returned only when the current SID owns them; team reads redact the private content.

Drafts and user settings are scoped by the current SID. Common Links, canonical tags, customer aliases, external identities, and client matching are organization-wide. Every template, Common Link, matching, alias, organization-setting, and shared synchronization mutation requires the Admin role and is enforced at the stored-procedure boundary.

Settings intentionally contains no separate manual Sage customer-mapping editor. Administrators perform matching in the dedicated Client Matching workspace, which uses the same shared, audited server contract as the rest of client administration.

This milestone enforces those rules through the stored-procedure boundary, the absence of direct table permissions, and `tb_security.WhdTicketAccessPolicy`. That policy filters WHD rows to the mapped technician or technician group for ordinary users and applies insert/update block predicates; Admins, the dedicated sync service, and database owners retain the access required for their roles.

### Optimistic concurrency and immutability

Mutable shared records carry SQL Server `rowversion` values. Update and delete procedures compare the expected rowversion and report conflicts instead of silently overwriting another workstation's changes.

The database also enforces posting boundaries:

- a Sage-posted work entry cannot be modified
- a WHD- or Sage-posted work entry cannot be deleted
- Personal Note content remains separate from ordinary manager-readable fields
- client merges run as administrator-only transactions and preserve/reassign dependent metadata

## Workstation-local state

Local JSON files under `%LOCALAPPDATA%\TechBenchV2` contain no work entries, notes, drafts, tickets, templates, links, posting history, sync history, or import data.

`preferences.json` is limited to device behavior such as:

- generated device ID
- theme and window bounds/state
- shared-data view refresh interval
- update-check and skipped-version state
- Sage DSN/company-path/native-automation choices
- the Microsoft admin-link browser preference

The local refresh interval drives a client timer that reloads shared clients, tickets, statuses, matching, links, tags, and templates. It does not create a cache or overwrite an active editor.

The WHD automatic-sync enabled state and interval are organization settings stored in SQL Server, so every Admin sees one schedule. The Windows service claims durable SQL work under an expiring lease. It performs one initial full import, overlapping ticket deltas at the configured interval, and reference snapshots for clients, statuses, technicians, and group membership at least daily. No Admin workstation needs to remain open.

Ticket synchronization uses WHD's organization `Tickets` resource, requests UTC timestamps and explicit deletion state, and pages until completion. Deltas use the durable cursor minus an overlap window, and the cursor advances only after a complete ticket batch is durably applied. Explicit closed or deleted records update the shared snapshot. Omission is not authoritative—permissions, paging, or concurrent changes can omit a ticket—so absence never closes a ticket. Personal credential testing continues to use the permission-light `Tickets/mine` probe.

`sql-server.json` contains the SQL Server address, database name, timeouts, and certificate-trust choice. It contains no username or password.

Personal WHD tokens and Sage passwords use the V2-specific Windows Credential Manager namespace. The service's organization credential uses machine-scoped DPAPI under an ACL restricted to SYSTEM, Administrators, and the service identity. Update packages, installed files, logs, and user-selected exports are operational files rather than an alternate business datastore.

If an emergency local draft cache is ever introduced, it must be recovery-only and separately approved. It must not create an offline worklog or synchronization system.

## Posting and synchronization

Personal WHD posting and Sage external calls run on the workstation. Organization-wide WHD reads run only in the Windows service. SQL Server owns coordination and durable results for both paths.

The posting protocol is:

1. Acquire or validate the server-side posting lease.
2. Begin a durable posting attempt in SQL Server and commit it.
3. Close the database transaction.
4. Perform the WHD or Sage operation.
5. Complete the attempt as success or failure, or leave/reconcile an unknown result when the external outcome is uncertain.
6. Update the work entry's durable posting state under its rowversion/ownership rules.

Outstanding or unknown attempts prevent a blind retry that could duplicate external work. The same-process mutex still protects one local instance, but server leases coordinate multiple workstations.

Organization WHD synchronization uses:

- an Admin-only request, monitor, configuration, and mapping boundary
- a dedicated service principal that is not an application user or Admin
- a serialized durable queue plus expiring, renewable work leases
- work-type-bound, transactionally applied JSON-array batches
- a success-only, monotonic UTC ticket cursor with an overlap window
- shared external identities that preserve reconciled `Both` WHD/Sage clients
- service health and request/work history in SQL Server

The legacy sync-operator role never grants a non-Admin permission to start or apply shared WHD work. Admins can queue work but cannot call service apply procedures. Ordinary users see only WHD tickets directly assigned to their mapped technician or assigned to one of that technician's synchronized groups; Admins may see all WHD tickets.

The service uses one dedicated WHD identity with permission to read the full organization ticket set. The secret never reaches a desktop client. Ticket absence is deliberately non-authoritative: TechBench updates open/closed/deleted state explicitly returned by WHD but never closes a shared ticket merely because it was omitted.

## Database deployment and versioning

Database creation and schema changes are DBA operations. Ordinary clients never create or migrate the production database.

The SQL Server 2016 package contains idempotent stages for:

1. preflight validation
2. database creation and configuration
3. baseline schema
4. schema-version-2 operational storage
5. schema-version-3 shared reference data
6. schema-version-4 Admin-owned shared configuration
7. schema-version-5 owner-scoped TechBench V1 import storage
8. schema-version-6 server WHD synchronization storage
9. security and AD-role mappings
10. baseline and versioned stored procedures
11. procedure grants
12. baseline and versioned verification

`database/sqlserver2016/Deploy-CSRI-Standalone.sql` combines every numbered stage for SSMS SQLCMD Mode and has no external include paths.

Schema version `2` is recorded as migration `SqlServer2016.OperationalStorage.0002`; schema version `3` adds shared reference data; schema version `4` records the strict Admin-owned boundary; schema version `5` adds owner-scoped, idempotent V1 entity mappings; and schema version `6` adds the leased service-only WHD ingestion boundary through `SqlServer2016.WhdServerSync.0006`. The alpha.6 client requires exactly schema version 6.

The first schema-version-4 Admin startup performs one insert-missing catalog seed and writes the organization setting `WorkspaceDefaults.Initialized=4` with that Admin's real SID. Subsequent startups do not recreate renamed or deleted note templates. The WHD auto-sync enabled/interval rows remain independently insert-missing so required runtime defaults can be repaired without changing an Admin's saved values.

The schema-version-6 database, alpha.6 client, and alpha.6 sync service are a coordinated cutover. Have the DBA back up and upgrade the database, install the service and its protected credential, install the matching client, and run smoke tests as one planned operation. Do not leave mixed alpha clients in normal use.

The desktop client has no database-backup command and no authority to create a SQL Server backup. Full/log backup scheduling, `DBCC CHECKDB`, retention, monitoring, and restore testing belong to DBA/operations outside TechBench.

## Validation state and release gate

Code-level validation covers:

- repository contract coverage for every WPF workflow
- local-preference persistence without business fields or secrets
- production build exclusion of the legacy SQLite repository/providers while retaining only the read-only V1 reader
- release-package rejection of local database artifacts and verification that the V1 reader runtime is present
- database procedure-name and grant coverage
- SQL deployment-script static checks
- application unit/regression tests

Production approval still requires a live SQL Server 2016 exercise. At minimum:

1. Run the complete standalone upgrade on a backed-up `TechBench` database.
2. Confirm schema version 6 and successful verification output.
3. Install the service under its distinct principal, configure the protected WHD credential, and verify initial full plus incremental synchronization.
4. Connect as a member of `CSRI\TechBench_Users` and as a member of `CSRI\TechBench_Admins`.
5. Confirm ordinary users and a sync-operator-only test identity cannot change shared configuration, matching, aliases, links, templates, schedules, or snapshot state.
6. Confirm two workstations see shared client, ticket, matching, alias, tag, template, link, and entry changes after automatic refresh.
7. Confirm Personal Notes remain invisible to other users and manager views.
8. Force a rowversion conflict and verify that the client does not overwrite silently.
9. Exercise personal WHD/Sage posting attempts and lease expiry/reconciliation.
10. Verify service lease recovery, direct/group ticket visibility, explicit close/delete handling, and that omission does not close a ticket.
11. Have the DBA verify SQL Server backup, `DBCC CHECKDB`, and restore procedures independently of the client.

Until those checks pass, alpha.6 is an implementation candidate, not a production release.

## V1 data migration

Installing alpha.6 does not automatically import V1 data; each authenticated user explicitly uses **Settings > Import V1 Database...** and confirms a preview for their own account.

The user must close V1 and select its closed local database or a verified copy. The reader opens SQLite in read-only/query-only mode, rejects active journal/WAL sidecars, runs `quick_check`, validates known schema variants and SQL field limits, and rejects a source whose SHA-256 changes during the read. It extracts work entries, Personal Notes, entry tags, follow-up state, posting state/history, and note links. It does not import shared catalogs/configuration, credentials, editor drafts, active posting attempts, or local caches.

Existing shared clients and tickets are resolved inside SQL Server with exact source-qualified external identities, Sage customer IDs, organization aliases, or an unambiguous exact client name/ticket number. Unmatched references remain on the work entry as a manual name or ticket number; the importer never creates or changes shared reference records. SQL Server derives the imported owner from `ORIGINAL_LOGIN()` and never accepts an owner SID from the client. Durable mappings keyed by owner, `TechBenchV1`, entity type, and legacy ID make partial retries idempotent; a changed source or resolved reference is a conflict, not an overwrite. Counts, foreign keys, ownership, posting flags, private notes, links, posting logs, and sample note content must be verified.

V1 remains installed and unchanged for rollback or historical reference. After cutover it should be archive-only. V1 and V2 must not operate as dual writable systems.

## SQL Server 2016 caveat

The database targets SQL Server 2016 and compatibility level 130. Scripts must be run against an actual SQL Server 2016 test or production instance; compatibility level 130 on a newer engine is not a substitute for engine-version testing.

SQL Server 2016 extended support ended July 14, 2026. Before production use, confirm:

- the organization's approved SQL Server service pack and security-update posture
- Extended Security Updates coverage or a documented upgrade plan
- TLS 1.2 and a trusted SQL Server certificate
- tested full/database-log backup policy appropriate to the selected recovery model
- recurring integrity checks and a proven restore procedure

Microsoft lifecycle reference: <https://learn.microsoft.com/lifecycle/products/sql-server-2016>
