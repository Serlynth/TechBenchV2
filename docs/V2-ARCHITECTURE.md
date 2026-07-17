# TechBench V2 architecture

## Status

TechBench V2 `2.0.0-alpha.3` implements the completed client-side conversion from the local SQLite design used by TechBench 1.x to a direct WPF-to-SQL Server design, including an explicit organization-wide reference-data boundary.

The production WPF runtime now uses the SQL Server repository for every business and operational workflow. Production builds exclude the SQLite packages and legacy local-database implementation. SQLite remains available only to test builds for regression and migration-boundary coverage.

The implementation is still an alpha until the schema-version-3 upgrade and client are exercised against the actual SQL Server 2016 instance with real domain identities. "Implemented" does not mean "approved for production."

## Fixed boundaries

- TechBench V1 remains unchanged, independently buildable, and independently installable.
- TechBench V2 remains a Windows WPF application because Sage UI automation and Sage ODBC execute on the technician workstation.
- There is no TechBench API, web application, container, or server service.
- Each client connects directly to SQL Server using Windows Integrated Authentication over encrypted TDS.
- Active Directory membership maps to database roles.
- SQL Server owns all business, user, worklog, draft, posting, synchronization, import, and audit state.
- The workstation owns only non-business connection/device preferences, protected external-system secrets, updater artifacts, and user-created temporary/export files.
- V2 has no offline business-data store and never uses a SQLite database on a network share.
- WHD and Sage credentials are stored in Windows Credential Manager and never in SQL Server connection strings, local JSON, or source control.

## Runtime topology

~~~text
Domain-joined Windows workstation
    TechBench V2 WPF client (x86)
        |
        | Microsoft.Data.SqlClient
        | Windows Integrated Authentication
        | Encrypt=True
        v
CSRI-SQL.CSRI.local
    SQL Server 2016
    TechBench database
    compatibility level 130
    schema version 3
~~~

The application opens short-lived pooled connections for individual stored-procedure calls. It does not keep a database transaction open during WHD HTTP requests, Sage ODBC queries, or Sage desktop automation.

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
4. Verifies that the database reports schema version `3`.
5. Receives the caller's Windows SID, login/display name, database instance ID, server UTC time, and effective role flags.
6. Idempotently ensures the original built-in templates and Common Links exist.
7. Refuses startup when the database is unreachable, the schema is incompatible, or the caller has no TechBench role.

The Windows SID is the durable user/owner key. Login names are retained for display and audit history but are not the ownership key because names can change.

The deployed CSRI mappings are:

| Active Directory group | Database roles |
|---|---|
| `CSRI\TechBench_Users` | `tb_role_user` |
| `CSRI\TechBench_Admins` | `tb_role_user`, `tb_role_manager`, `tb_role_admin`, `tb_role_sync_operator` |

The database role meanings are:

- `tb_role_user`: use ordinary TechBench workflows and manage the caller's own work.
- `tb_role_manager`: read approved team-level work and reports.
- `tb_role_admin`: manage organization-scoped clients, matching, shared configuration, imports, and audit views.
- `tb_role_sync_operator`: coordinate WHD and Sage snapshots.

Authorization is enforced in stored procedures. The groups do not receive `db_owner`, `db_datareader`, `db_datawriter`, `db_ddladmin`, or direct application-table DML permission. UI visibility is not a security boundary.

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
- a canonical organization-wide tag catalog populated from work-entry tags
- owner-private Personal Notes and their WHD inclusion choice
- editor recovery drafts keyed by owner SID and device ID
- organization templates, with read compatibility for legacy personal templates
- organization-wide Common Links
- organization and user settings that represent application state
- posting logs, attempts, outstanding-result state, and posting leases
- WHD and Sage synchronization leases and runs
- staged/imported records and legacy-ID mappings
- audit events
- database instance and schema migration metadata

Tables are separated into deployment, data, private, user, operations, audit, security, and application-procedure schemas. Ordinary clients do not access those tables directly.

### Ownership and privacy

Ordinary work is owned by the caller's Windows SID. Managers may read approved ordinary team work through manager-enabled procedure paths. Personal Notes are joined and returned only when the current SID owns them; team reads redact the private content.

Drafts and user settings are scoped by the current SID. Common Links, canonical tags, customer aliases, external identities, and client matching are organization-wide. Shared template, Common Link, matching, and organization-setting changes require the appropriate administrative role; adding a previously unknown import alias is audited and cannot reassign an existing shared alias without that role.

This milestone enforces those rules through the stored-procedure boundary and the absence of direct table permissions. Future row-level-security policies could provide additional defense in depth, but documentation must not assume a policy that is not deployed.

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
- refresh and WHD auto-sync intervals
- update-check and skipped-version state
- Sage DSN/company-path/native-automation choices
- the Microsoft admin-link browser preference

The local refresh interval drives a client timer that reloads shared clients, tickets, statuses, matching, links, tags, and templates. It does not create a cache or overwrite an active editor.

`sql-server.json` contains the SQL Server address, database name, timeouts, and certificate-trust choice. It contains no username or password.

WHD API tokens and Sage passwords use the V2-specific Windows Credential Manager namespace. Update packages, installed files, logs, and user-selected exports are operational files rather than an alternate business datastore.

If an emergency local draft cache is ever introduced, it must be recovery-only and separately approved. It must not create an offline worklog or synchronization system.

## Posting and synchronization

WHD and Sage external calls still run on the workstation, while SQL Server owns coordination and durable results.

The posting protocol is:

1. Acquire or validate the server-side posting lease.
2. Begin a durable posting attempt in SQL Server and commit it.
3. Close the database transaction.
4. Perform the WHD or Sage operation.
5. Complete the attempt as success or failure, or leave/reconcile an unknown result when the external outcome is uncertain.
6. Update the work entry's durable posting state under its rowversion/ownership rules.

Outstanding or unknown attempts prevent a blind retry that could duplicate external work. The same-process mutex still protects one local instance, but server leases coordinate multiple workstations.

WHD and Sage snapshot synchronization uses:

- the `tb_role_sync_operator` permission
- a durable, expiring lease per source
- source, device, run, and lease validation
- server-side snapshot application with a durable recorded run outcome
- preservation of reconciled `Both` client source identity
- audit/state records identifying the operator and workstation

## Database deployment and versioning

Database creation and schema changes are DBA operations. Ordinary clients never create or migrate the production database.

The SQL Server 2016 package contains idempotent stages for:

1. preflight validation
2. database creation and configuration
3. baseline schema
4. schema-version-2 operational storage
5. schema-version-3 shared reference data
6. security and AD-role mappings
7. baseline and versioned stored procedures
8. procedure grants
9. baseline and versioned verification

`database/sqlserver2016/Deploy-CSRI-Standalone.sql` combines every numbered stage for SSMS SQLCMD Mode and has no external include paths.

Schema version `2` is recorded as migration `SqlServer2016.OperationalStorage.0002`; schema version `3` adds the organization tag catalog and promotes shared reference/configuration data through `SqlServer2016.SharedReferenceData.0003`. The alpha.3 client requires exactly schema version 3 and refuses other versions.

The schema-version-3 database and alpha.3 client are a coordinated cutover. Back up the database, upgrade it, install the matching client, and run smoke tests as one planned operation. Do not leave mixed alpha clients in normal use.

No TechBench server process needs to be installed or started.

## Validation state and release gate

Code-level validation covers:

- repository contract coverage for every WPF workflow
- local-preference persistence without business fields or secrets
- production build exclusion of SQLite code and packages
- database procedure-name and grant coverage
- SQL deployment-script static checks
- application unit/regression tests

Production approval still requires a live SQL Server 2016 exercise. At minimum:

1. Run the complete standalone upgrade on a backed-up `TechBench` database.
2. Confirm schema version 3 and successful verification output.
3. Connect as a member of `CSRI\TechBench_Users` and as a member of `CSRI\TechBench_Admins`.
4. Confirm ordinary users cannot execute administrator-only operations.
5. Confirm two workstations see shared client, ticket, matching, alias, tag, template, link, and entry changes after automatic refresh.
6. Confirm Personal Notes remain invisible to other users and manager views.
7. Force a rowversion conflict and verify that the client does not overwrite silently.
8. Exercise WHD/Sage posting attempts and lease expiry/reconciliation.
9. Exercise WHD/Sage snapshot leases with the expected operator role.
10. Verify backup, `DBCC CHECKDB`, and restore procedures.

Until those checks pass, alpha.3 is an implementation candidate, not a production release.

## V1 data migration

Installing alpha.3 does not automatically import V1 data.

Migration must use a verified copy of a V1 SQLite database, assign each imported work record to an explicit AD SID, preserve original identifiers in legacy mapping tables, and reconcile WHD/Sage identities before name-similarity matching. Counts, foreign keys, ownership, posting flags, links, and sample note content must be verified.

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
