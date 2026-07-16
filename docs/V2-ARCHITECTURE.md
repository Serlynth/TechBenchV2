# TechBench V2 architecture

## Status: Phase 1 alpha

TechBench V2 is being converted from the local SQLite design used by TechBench 1.x to a direct WPF-to-SQL Server architecture.

Phase 1 is limited to the SQL connection/security foundation and the shared-client vertical slice. The shared client list is the first workflow to use the central database. Today, history, search, tickets, templates, settings, drafts, imports, posting logs, and posting coordination are not considered fully ported until their local repository calls have been replaced and verified against SQL Server.

This alpha is not a production-ready multi-user release. The older API,
PostgreSQL, Identity-login, Docker, and token-client prototype have been removed.
The remaining SQLite path is transitional code for workflows that have not yet
been ported to SQL Server.

## Finalized boundaries

- TechBench V1 remains unchanged, independently buildable, and independently installable.
- TechBench V2 remains a Windows WPF application because Sage UI automation and Sage ODBC run on the technician workstation.
- There is no TechBench API, web application, container, or server service.
- Each WPF client connects directly to the existing SQL Server using Windows Integrated Authentication.
- Active Directory groups determine application roles.
- SQL Server is the authoritative store for all meaningful shared, user, worklog, draft, synchronization, and posting state.
- A shared SQLite file is never used. Transitional local SQLite data must disappear as each workflow is ported.
- WHD and Sage credentials remain protected by Windows Credential Manager and are never stored in source control or a SQL connection string.

## Runtime topology

```text
Domain-joined Windows workstation
    TechBench V2 WPF client (x86)
        |
        | Microsoft.Data.SqlClient
        | Windows Integrated Authentication
        | encrypted TDS connection
        v
Existing SQL Server 2016 instance
    TechBenchV2 database
    compatibility level 130
```

The application opens short-lived pooled SQL connections for individual operations. It must not keep a database transaction open while performing WHD requests, Sage ODBC calls, or Sage desktop automation.

A representative production connection string is:

```text
Server=tcp:sqlserver.example.local,1433;
Database=TechBenchV2;
Integrated Security=true;
Encrypt=true;
TrustServerCertificate=false;
Application Name=TechBenchV2;
Connect Timeout=5;
```

The deployed server name and database name are configuration, not credentials. Production should use a SQL Server certificate trusted by the workstations. `TrustServerCertificate=true` may be useful for a controlled development test, but it bypasses certificate-chain validation and is not the production target.

## Identity and authorization

There is no TechBench username/password screen. SQL Server sees the Windows identity of the person running TechBench.

The database should expose a `GetCurrentUserContext` procedure that returns:

- `ORIGINAL_LOGIN()`
- the user's Windows SID
- display/login name
- database instance identifier
- schema version
- SQL Server UTC time
- technician, manager, administrator, and synchronization-operator flags

The Windows SID is the durable owner key. Login names are retained for display and audit history but must not be the only ownership key because account names can change.

Recommended database roles are:

- `TechBench_Technician`: read shared reference data and manage the caller's own work.
- `TechBench_Manager`: technician rights plus approved team reporting.
- `TechBench_Admin`: manage shared clients, mappings, configuration, and imports.
- `TechBench_SyncOperator`: run shared WHD/Sage snapshot synchronization.

Domain groups are mapped to these database roles by the DBA. Role checks returned by SQL Server are authoritative; hiding a button in WPF is not authorization.

Application roles should receive `EXECUTE` permission on approved stored-procedure schemas. They should not receive broad `db_datareader`, `db_datawriter`, or direct table DML access. Tables and procedures should share an owner so normal ownership chaining can reach the underlying data without exposing it directly.

Owner-sensitive tables should also use SQL Server Row-Level Security as defense in depth. Managers may be authorized to read ordinary team work entries, but Personal Notes remain owner-only unless a separate privacy-auditor policy is deliberately approved.

## Data ownership

### SQL Server authoritative data

The target database owns:

- user profiles identified by Windows SID
- registered user/device records
- clients and WHD/Sage source identities
- client matches, merges, and aliases
- tickets and ticket status options
- work entries and work-entry links
- owner-only Personal Notes
- editor recovery drafts
- templates and saved tags
- Common Links
- organization, user, and device settings
- posting logs and durable posting attempts
- WHD/Sage synchronization runs and leases
- import batches and legacy-ID mappings
- audit events
- schema/version metadata

Editor drafts should be keyed by owner SID and device identifier, or by owner SID alone if drafts are expected to follow a user between workstations. The V1 singleton `EditorDrafts.Id = 1` design is not valid for multiple users.

### Workstation-local state

Only data that is not a shared business record remains local:

- installed application and update artifacts
- transient UI state such as window position
- WHD and Sage secrets in Windows Credential Manager
- temporary export files selected by the user

If an emergency local draft cache is later approved for network outages, it is recovery-only. It must never become a second writable worklog or an offline synchronization system.

## SQL Server schema conventions

- Keep existing integer identifiers where practical to reduce WPF model churn.
- Use `date`, `time(0)`, `bit`, `nvarchar`, and UTC `datetime2(3)` values.
- Use native SQL Server `rowversion` columns for optimistic concurrency.
- The database assigns owner SID, created/updated identity, and UTC timestamps.
- Update and delete procedures require the expected `rowversion` and report a conflict when the row changed.
- Client merges are administrator-only transactions that reassign all dependent records and write an audit event.
- Personal Notes are stored separately from manager-visible work-entry fields.
- WHD/Sage external identities should have unique source/external-ID constraints. A normalized child table is preferable to a pipe-delimited identity field.

The existing SQLite repository cannot be converted by replacing only its connection class. It contains SQLite-specific FTS5, `PRAGMA`, `LIMIT`, `ON CONFLICT`, `julianday`, `COLLATE NOCASE`, and `last_insert_rowid()` behavior. V2 should split persistence behind feature-level interfaces and port each feature to parameterized SqlClient commands and SQL Server 2016-compatible stored procedures.

SQL Server Full-Text Search can replace SQLite FTS5, but Full-Text Search is an optional SQL Server component. The DBA must confirm that it is installed. Search needs a documented fallback until the SQL full-text path is deployed and verified.

## Posting and synchronization

WHD and Sage external operations continue to run on the workstation, while SQL Server owns their durable state.

The posting sequence is:

1. Call a stored procedure to begin a posting attempt.
2. SQL Server atomically records the outstanding attempt and commits it.
3. Close the database transaction.
4. Perform the WHD request or Sage operation.
5. Complete the attempt in SQL Server with success, failure, or unknown status.

A process crash or uncertain external result must leave an outstanding `Unknown` attempt that blocks a blind retry until it is reconciled or explicitly abandoned. The current same-process mutex remains useful, but it is not sufficient for multiple workstations.

Shared client and ticket synchronization should use:

- a synchronization-operator role
- one durable synchronization lease per source
- table-valued parameters or staging tables for complete snapshots
- a single SQL transaction for upsert and missing-record reconciliation
- audit records describing who synchronized, from which workstation, and whether the snapshot was complete

## Database deployment and versioning

Database creation and schema changes are DBA operations. The desktop application must not create, alter, or migrate the production database under an ordinary technician account.

Versioned DBA scripts or a SQL Server database project should:

1. create the `TechBenchV2` database
2. set compatibility level 130
3. create schemas, tables, indexes, procedures, and security policies
4. create database roles
5. map approved Active Directory groups
6. record the applied schema version
7. configure backup, integrity-check, and restore procedures

The application checks the database schema version at startup and refuses to run against an incompatible schema. Database changes should be additive and backward-compatible while older V2 clients may still be installed. Deploy compatible database changes before the client version that consumes them.

No TechBench server process needs to be installed or started.

## Migration phases

### Phase 1: connection, security, and shared clients

- Preserve the independent V2 product identity.
- Establish the integrated and encrypted SQL connection.
- Deploy schema metadata, user-context, roles, and shared-client objects.
- Load shared clients directly from SQL Server.
- Permit client administration only for the administrator role.
- Clearly report database-unavailable, permission-denied, and schema-version failures.

Acceptance requires two domain users to see the same client list and a technician to be unable to perform an administrator-only client change.

### Phase 2: Today workspace and drafts

- Move committed WorkEntries, Personal Notes, links, and recovery drafts to SQL Server.
- Enforce owner/private-note access in the database.
- Add `rowversion` conflict handling.
- Convert WPF database calls to asynchronous operations.
- Remove the local WorkEntry source of truth.

### Phase 3: remaining shared workflows

- Move tickets, statuses, templates, Common Links, aliases, settings, tags, history, search, reports, and posting history.
- Add manager-scoped reporting.
- Deploy SQL Full-Text Search or the approved fallback.
- Remove the remaining meaningful SQLite tables and local database-management UI.

### Phase 4: posting and source synchronization

- Add centralized posting-attempt coordination.
- Add WHD and Sage synchronization leases and snapshot procedures.
- Enforce Sage-posted immutability and WHD tracking rules at the database boundary.
- Add operational audit and reconciliation tools.

### Phase 5: V1 import and production pilot

- Import only from copied V1 SQLite databases.
- Assign imported work to an explicit AD SID.
- Preserve legacy IDs through an import mapping table.
- Deduplicate clients by WHD/Sage identities before name similarity.
- Verify counts, foreign keys, posting state, audit ownership, and sample content.
- Rehearse SQL backup and restore.
- Freeze V1 data entry for final import and make V2 authoritative.

V1 remains installed and unchanged, but it must be treated as archive-only after cutover. V1 and V2 must never be operated as dual writable systems.

## SQL Server 2016 caveat

The application is intentionally designed for SQL Server 2016 and database compatibility level 130. Scripts must be tested on an actual SQL Server 2016 staging database; setting compatibility level 130 on a newer server does not prove that newer engine syntax will run on SQL Server 2016.

SQL Server 2016 extended support ended on July 14, 2026. Before production use, confirm:

- SQL Server 2016 SP3 and the organization's approved cumulative/security updates
- Extended Security Updates coverage, or a documented upgrade plan
- TLS 1.2 and a trusted server certificate
- SQL Server edition and database-size/SQL Agent limitations
- Full-Text Search installation if required
- tested backup, `DBCC CHECKDB`, and restore procedures

Microsoft lifecycle reference: <https://learn.microsoft.com/lifecycle/products/sql-server-2016>
