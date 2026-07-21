# TechBench V2 architecture

## Status

TechBench V2 `2.0.0-alpha.17` implements the conversion from TechBench 1.x's local SQLite design to a shared SQL Server design, including an owner-scoped V1 migration contract, a dedicated Windows service for organization-wide WHD and Sage customer synchronization, an Admin-only read-only user-preview boundary, a compiled server-local administrator GUI, and a self-contained one-click server installer. Alpha.17 preserves the installed service identity, SQL configuration, and protected WHD/Sage secrets during same-schema update or repair and restores the service identity's read-and-execute ACL before restart.

The production WPF runtime uses the SQL Server repository for every business and operational workflow. It packages `Microsoft.Data.Sqlite` only for the explicit, read-only V1 migration reader; production builds still exclude the legacy local repository, database-location service, and local client/ticket providers. V2 has no client-side business-database backup path; SQL Server protection is owned by DBA/operations.

The implementation is still an alpha until the schema-version-7 upgrade, service, and client are exercised against the actual SQL Server 2016 instance, real domain identities, the live WHD server, and the live Sage ODBC data source. "Implemented" does not mean "approved for production."

## Fixed boundaries

- TechBench V1 remains unchanged, independently buildable, and independently installable.
- TechBench V2 remains a Windows WPF application because personal Sage time-ticket UI automation and verification execute on the technician workstation.
- There is no TechBench API, web application, or container. One internal x64 Windows service owns organization-wide WHD reads and Sage customer synchronization; it launches an isolated x86 ODBC worker for the 32-bit Sage driver.
- Each client connects directly to SQL Server using Windows Integrated Authentication over encrypted TDS.
- Active Directory membership maps to database roles.
- SQL Server owns all business, user, worklog, draft, posting, synchronization, import, and audit state.
- The workstation owns only non-business connection/device preferences, protected external-system secrets, updater artifacts, and user-created temporary/export files.
- V2 has no offline business-data store and never uses a SQLite database on a network share.
- The client never creates, schedules, verifies, or restores a SQL Server database backup.
- Personal WHD/Sage posting credentials remain in each user's Windows Credential Manager. The organization WHD and Sage synchronization credentials are separately machine-protected on the service host and never stored in SQL Server, client JSON, packages, or source control.

## Runtime topology

~~~text
WHD server
    | HTTPS / dedicated WHD service identity
    v
TechBench Sync Service (domain-joined Windows Server, x64)
    ^
    | private stdin/stdout JSON / no shell
Sage ODBC worker (self-contained x86 process)
    ^
    | 32-bit Sage System DSN / server Sage read identity
Sage 50 customer data

TechBench Sync Service
    | tb_service procedures / Windows authentication / Encrypt=True
    v
CSRI-SQL.CSRI.local
    SQL Server 2016
    TechBench database
    compatibility level 130
    schema version 7
    ^
    | tb_app procedures / Windows authentication / Encrypt=True
    |
TechBench V2 WPF clients (domain-joined workstations, x86)
~~~

The client and service open short-lived pooled connections for normal stored-procedure calls. Read-only preview connections deliberately disable pooling, activate a short-lived server session, and switch to a restricted database user before any application read. Neither the client nor service holds a database transaction open during WHD HTTP, Sage ODBC, or Sage desktop automation.

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

## Server Manager lifecycle

TechBench Server Manager is a compiled, self-contained server-local administrator process, not a network service. It is the sole product UI for the service identity, protected server credentials, SQL connection settings, organization-wide WHD/Sage synchronization configuration, and manual sync requests. It connects to SQL Server with the elevated operator's Windows identity, requires the TechBench Admin role, and uses only the existing stored-procedure boundary; external-system secrets remain machine-protected outside SQL. Its Start Menu shortcut targets `TechBench.ServerManager.exe` directly, and its application manifest requests elevation. `TechBenchServerSetup.exe` provides the first-install and repair boundary without requiring package extraction or operator-entered commands.

The Manager owns one notification-area icon and one exclusive lifetime lock while running. Minimizing clears/remasks entered credentials and hides the form; double-clicking the icon or selecting **Open TechBench Server Manager** restores it. A second launch directs the operator to the existing tray instance. **Exit** and the window X end the process, except that closing is rejected while a service/update operation is active. The icon, context menu, in-memory icon copy, and lifetime lock are explicitly disposed at shutdown. The packaged icon is cloned from memory so the update transaction never retains a file handle on it.

Routine updates are performed by a verified compiled helper mode of the Manager. It validates the outer SHA-256 sidecar and every package-manifest entry before stopping the service. It stages and backs up the service and Manager directories, preserves the installed SQL configuration and ProgramData secrets, creates a direct EXE shortcut, restarts the service, and restores the previous directories if installation fails. The one-click setup can perform the same replacement for any earlier V2 installation; when installed and target packages require the same schema it does not depend on the interactive operator's SQL login. A schema change still requires the matching DBA deployment and verification.

## Startup, identity, and authorization

At startup the client:

1. Shows the connection screen, including the optional Admin-only username-preview field.
2. Loads the non-secret SQL endpoint configuration and opens an integrated-authentication SQL connection.
3. Calls `tb_app.GetCurrentUserContext` and verifies schema version `7`.
4. Receives the caller's Windows SID, login/display name, database instance ID, server UTC time, and effective role flags.
5. When the caller requests another username, requires the real caller to be a TechBench Admin, creates a short-lived server preview session, disables connection pooling, activates that session on every connection, and executes as `tb_preview_reader`.
6. When the effective caller is an authenticated, writable Admin, performs the insert-missing template/Common Link/default initialization without overwriting Admin values.
7. Refuses startup when the database is unreachable, the schema is incompatible, the caller has no TechBench role, or preview authorization fails.

The Windows SID is the durable user/owner key. Login names are retained for display and audit history but are not the ownership key because names can change.

The deployed CSRI mappings are:

| Active Directory principal | Database roles |
|---|---|
| `CSRI\TechBench_Users` | `tb_role_user` |
| `CSRI\TechBench_Admins` | `tb_role_user`, `tb_role_manager`, `tb_role_admin`, `tb_role_sync_operator` |
| `CSRI\TechBench_Sync` | `tb_role_sync_service` only |

The database role meanings are:

- `tb_role_user`: use ordinary TechBench workflows, manage the caller's own work, and import that caller's V1 personal history.
- `tb_role_manager`: read approved team-level work and reports.
- `tb_role_admin`: manage organization-scoped clients, matching, aliases, Common Links, templates, shared configuration, synchronization, and audit views.
- `tb_role_sync_operator`: retained for upgrade compatibility and synchronization-history inspection; it grants no shared mutation authority by itself.
- `tb_role_sync_service`: claim/renew leased WHD or Sage work and apply validated snapshots; it grants no interactive application or Admin authority.

Authorization is enforced in stored procedures, with a SQL Server row-level-security policy adding table-level defense for WHD tickets. The application principals do not receive `db_owner`, `db_datareader`, `db_datawriter`, `db_ddladmin`, or direct application-table DML permission. UI visibility is not a security boundary.

Shared mutation and manual synchronization requests require the effective Admin role even if the caller has another technical role. Ordinary users can read shared catalogs but cannot change customer matching or aliases, Common Links, note templates, organization defaults, the WHD automatic-sync schedule, or WHD/Sage snapshot state. Preview targets are registered, enabled non-Admin technicians who have opened TechBench V2 within the past hour; Admin accounts are deliberately excluded because their role can be tested directly. A real user's normal connection refreshes the stored role flags before access is allowed or denied, and a zero-role refresh revokes active preview sessions before returning the denial.

The preview session is an observation tool, not alternate authentication. SQL records the authenticated Admin SID, target user SID, client instance, expiry, and revocation state. The restricted principal receives only approved read procedure execution and has no direct table rights or mutation procedures. Preview-safe work-entry procedures redact `PersonalNote` and `IncludePersonalNoteInWhd`, and the restricted principal cannot load editor drafts or local credentials.

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
- WHD and Sage synchronization requests, leases, runs, cursors, and health
- staged/imported records and legacy-ID mappings
- audit events
- database instance and schema migration metadata

Tables are separated into deployment, data, private, user, operations, audit, security, and application-procedure schemas. Ordinary clients do not access those tables directly.

### Ownership and privacy

Ordinary work is owned by the caller's Windows SID. Managers may read approved ordinary team work through manager-enabled procedure paths. Personal Notes are joined and returned only when the current SID owns them; team reads redact the private content.

Drafts and user settings are scoped by the current SID. Common Links, canonical tags, customer aliases, external identities, and client matching are organization-wide. Every template, Common Link, matching, alias, organization-setting, and shared synchronization mutation requires the Admin role and is enforced at the stored-procedure boundary.

Settings intentionally contains no separate manual Sage customer-mapping editor. Administrators perform matching in the dedicated Client Matching workspace, which uses the same shared, audited server contract as the rest of client administration.

This milestone enforces those rules through the stored-procedure boundary, the absence of direct table permissions, `tb_security.WhdTicketAccessPolicy`, and the V7 read-only preview execution context. The ticket policy filters WHD rows to the mapped technician or technician group for ordinary users and applies insert/update block predicates. A valid preview target takes precedence over the authenticated Admin's normal all-ticket bypass, while the dedicated sync service and database owners retain the access required for their roles.

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
- personal Sage time-ticket DSN/company-path/native-automation choices
- the Microsoft admin-link browser preference

The local refresh interval drives a client timer that reloads shared clients, tickets, statuses, matching, links, tags, and templates. It does not create a cache or overwrite an active editor.

The WHD automatic-sync enabled state and interval are organization settings stored in SQL Server, so every Admin sees one schedule. The Windows service claims durable SQL work under an expiring lease. It performs one initial full import, overlapping ticket deltas every five minutes, and reference snapshots for clients, statuses, technicians, and group membership at least daily. An Admin may also queue an immediate WHD run. No Admin workstation needs to remain open.

Ticket synchronization uses WHD's organization `Tickets` resource, requests UTC timestamps and explicit deletion state, and pages until completion. Deltas use the durable cursor minus an overlap window, and the cursor advances only after a complete ticket batch is durably applied. Explicit closed or deleted records update the shared snapshot. Omission is not authoritative—permissions, paging, or concurrent changes can omit a ticket—so absence never closes a ticket. Personal credential testing continues to use the permission-light `Tickets/mine` probe.

Sage customer synchronization has no timer and no automatic enqueue path. Only an authenticated TechBench Admin can create a durable request. The Windows service reads the shared DSN and username from SQL, reads the separate server password from its machine-protected secret, and invokes the packaged x86 worker. SQL Server preserves and validates every JSON array element before projection, rejecting the entire snapshot for an empty array, non-object row, missing/malformed/over-length field, or duplicate normalized customer ID. Before any shared customer mutation, it also computes the proposed stale delta. Removing at least 10 and at least 25 percent of an established set of 20 or more Sage mappings requires a second, explicitly confirmed Admin request; the rejected attempt retains the read, existing, and proposed-stale counts for review. That approval references the rejected request, expires after one hour, and applies only if the fresh read has exactly the same read, existing, and stale counts. Any difference produces a new no-write proposal that must be reviewed again. Failed, rejected, or abandoned work is visible in server health and can be retried without allowing a client to submit customer rows.

`sql-server.json` contains the SQL Server address, database name, timeouts, and certificate-trust choice. It contains no username or password.

Personal WHD tokens and Sage passwords use the V2-specific Windows Credential Manager namespace. The service's separate organization WHD and Sage credentials use machine-scoped DPAPI under an ACL where SYSTEM and Administrators can write and the service identity can only read. Update packages, installed files, logs, and user-selected exports are operational files rather than an alternate business datastore.

If an emergency local draft cache is ever introduced, it must be recovery-only and separately approved. It must not create an offline worklog or synchronization system.

## Posting and synchronization

Personal WHD posting and Sage time-ticket calls run on the workstation. Organization-wide WHD reads and Sage customer reads run only in the Windows service. SQL Server owns coordination and durable results for both paths.

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

Organization Sage synchronization uses:

- an Admin-only request and monitor boundary with no automatic schedule
- the same dedicated Windows service principal, but a separate Sage ODBC identity and protected secret
- a serialized durable queue plus renewable lease
- a server-local 32-bit System DSN and a packaged self-contained x86 worker
- a validated, nonempty, transactionally applied customer snapshot
- lossless row validation and an explicit Admin confirmation gate for unusually large customer removals
- service health and row counts stored in SQL Server

Admins configure only the non-secret server DSN and username in TechBench. They provision or rotate the Sage password on the service host; ordinary clients never receive it and cannot call the service apply procedures.

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
9. schema-version-7 server Sage synchronization and Admin preview storage
10. security and AD-role mappings
11. baseline and versioned stored procedures
12. procedure grants
13. baseline and versioned verification

`database/sqlserver2016/Deploy-CSRI-Standalone.sql` combines every numbered stage for SSMS SQLCMD Mode and has no external include paths.

Schema version `2` is recorded as migration `SqlServer2016.OperationalStorage.0002`; schema version `3` adds shared reference data; schema version `4` records the strict Admin-owned boundary; schema version `5` adds owner-scoped, idempotent V1 entity mappings; schema version `6` adds the leased service-only WHD ingestion boundary through `SqlServer2016.WhdServerSync.0006`; and schema version `7` adds server-owned Sage synchronization and Admin read-only preview through `SqlServer2016.ServerOwnedSageAndAdminPreview.0007`. The alpha.17 client and service require exactly schema version 7; alpha.17 introduces no database migration.

The first schema-version-4 Admin startup performs one insert-missing catalog seed and writes the organization setting `WorkspaceDefaults.Initialized=4` with that Admin's real SID. Subsequent startups do not recreate renamed or deleted note templates. The WHD auto-sync enabled/interval rows remain independently insert-missing so required runtime defaults can be repaired without changing an Admin's saved values.

The schema-version-7 database, alpha.17 client, and alpha.17 sync service are a coordinated cutover. Have the DBA back up and verify the database, run the one-click server setup, install both protected credentials, configure shared WHD/Sage values in Server Manager, install the matching client, and run smoke tests as one planned operation. Do not leave mixed alpha clients in normal use.

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
2. Confirm schema version 7 and successful verification output.
3. Install the service under its distinct principal, configure the protected WHD and Sage credentials, and verify initial full plus incremental WHD synchronization.
4. Connect as a member of `CSRI\TechBench_Users` and as a member of `CSRI\TechBench_Admins`.
5. Confirm ordinary users and a sync-operator-only test identity cannot change shared configuration, matching, aliases, links, templates, schedules, or snapshot state.
6. Confirm two workstations see shared client, ticket, matching, alias, tag, template, link, and entry changes after automatic refresh.
7. Confirm Personal Notes remain invisible to other users and manager views.
8. Force a rowversion conflict and verify that the client does not overwrite silently.
9. Exercise personal WHD/Sage posting attempts and lease expiry/reconciliation.
10. Queue a Sage customer sync from Server Manager as an Admin, verify its snapshot and row counts, reject malformed/duplicate input without changing customer data, exercise the explicit large-removal confirmation gate, and confirm an ordinary user cannot queue or apply it.
11. Preview an ordinary user as an Admin; verify the mapped WHD view, persistent warning, database write denial, Personal Note redaction, draft denial, and preview-session expiry/revocation.
12. Verify service lease recovery, direct/group ticket visibility, explicit close/delete handling, and that omission does not close a ticket.
13. Have the DBA verify SQL Server backup, `DBCC CHECKDB`, and restore procedures independently of the client.

Until those checks pass, alpha.17 is an implementation candidate, not a production release.

## V1 data migration

Installing alpha.17 does not automatically import V1 data; each authenticated user explicitly uses **Settings > Import V1 Database...** and confirms an import preview for their own account. This is separate from the Admin-only login preview.

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
