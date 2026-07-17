# TechBench V2 SQL Server 2016 deployment

This package creates the shared `TechBench` database used by the TechBench V2
WPF client. There is no separate TechBench server service. Each client connects
directly to SQL Server with Windows Integrated Authentication.

TechBench V2 `2.0.0-alpha.5` requires schema version `5`, including the
administrator-only shared-configuration boundary and owner-scoped V1 import
contract in this package.

## CSRI production deployment

The prepared CSRI configuration is:

| Setting | Value |
|---|---|
| SQL Server | `CSRI-SQL` |
| Database | `TechBench` |
| Standard users | `CSRI\TechBench_Users` |
| Application administrators | `CSRI\TechBench_Admins` |
| Compatibility level | SQL Server 2016 / level 130 |
| Recovery model | `SIMPLE` initially |
| Data file | At least 256 MB; fixed 64 MB growth |
| Log file | At least 128 MB; fixed 64 MB growth |

Copy the complete contents of `Deploy-CSRI-Standalone.sql` into SSMS while
connected to `CSRI-SQL` as an existing SQL Server `sysadmin`. Enable
**Query > SQLCMD Mode**, then execute the entire query. The standalone script
contains every numbered deployment stage and has no external file references.

The entry script contains no password and does not connect as `sa` itself. It
uses whichever existing sysadmin session opened the file.

## Authentication and authorization

The desktop application never stores a SQL username or password. SQL Server
authenticates the current Windows user, and the database derives identity from
`ORIGINAL_LOGIN()` and `SUSER_SID()`.

Only two AD groups are required:

| AD group | Database roles |
|---|---|
| `CSRI\TechBench_Users` | `tb_role_user` |
| `CSRI\TechBench_Admins` | `tb_role_user`, `tb_role_manager`, `tb_role_admin`, `tb_role_sync_operator` |

Administrators receive normal user access through their role mapping, so they
do not need membership in both AD groups.

Organization-wide configuration and WHD/Sage synchronization require the
effective `tb_role_admin` role. `tb_role_sync_operator` is retained for upgrade
compatibility and synchronization-history inspection; membership in that role
alone does not authorize a shared synchronization run. Ordinary users can read
shared catalogs but cannot change matching or aliases, Common Links, note
templates, organization defaults, the WHD automatic-sync schedule, or snapshot
state.

Application groups receive stored-procedure execution rights only. They are not
members of `db_owner`, `db_datareader`, `db_datawriter`, or `db_ddladmin`, and
they receive no server-wide administrative permissions.

The database is owned by SQL Server's built-in owner principal, identified by
SID `0x01`. This avoids tying database ownership to a removable employee
account or AD group. Application users never authenticate as this principal.

## Generic SQLCMD variables

The numbered scripts can also be run with another SQLCMD-capable deployment
tool by supplying:

| Variable | CSRI value |
|---|---|
| `DatabaseName` | `TechBench` |
| `UserGroup` | `CSRI\TechBench_Users` |
| `AdminGroup` | `CSRI\TechBench_Admins` |

Run the scripts in this order:

1. `00-Preflight.sql`
2. `10-CreateDatabase.sql`
3. `20-BaselineSchema.sql`
4. `21-V0002-OperationalSchema.sql`
5. `22-V0003-SharedReferenceData.sql`
6. `23-V0004-AdminOwnedSharedConfig.sql`
7. `24-V0005-TechBenchV1ImportSchema.sql`
8. `30-Security.sql`
9. `40-StoredProcedures.sql`
10. `41-V0002-WorkProcedures.sql`
11. `42-V0002-SharedProcedures.sql`
12. `43-V0002-PostingProcedures.sql`
13. `44-V0002-SyncImportProcedures.sql`
14. `45-V0003-SharedReferenceProcedures.sql`
15. `46-V0004-AdminSharedProcedures.sql`
16. `47-V0005-TechBenchV1ImportProcedures.sql`
17. `50-Grants.sql`
18. `51-V0002-OperationalGrants.sql`
19. `52-V0004-AdminSharedGrants.sql`
20. `53-V0005-TechBenchV1ImportGrants.sql`
21. `90-Verify.sql`
22. `91-V0002-OperationalVerify.sql`
23. `92-V0003-SharedReferenceVerify.sql`
24. `93-V0004-AdminSharedVerify.sql`
25. `94-V0005-TechBenchV1ImportVerify.sql`

The scripts are idempotent for the baseline, V0002 operational-storage, V0003
shared-reference-data, V0004 Admin-owned-configuration, and V0005 owner-scoped
V1-import migrations. They stop on validation or deployment errors.

## Database behavior

- Compatibility level is 130.
- `AUTO_CLOSE` and `AUTO_SHRINK` are disabled.
- Page verification uses `CHECKSUM`.
- `TRUSTWORTHY` and cross-database ownership chaining are disabled.
- Snapshot isolation and read-committed snapshot isolation are enabled.
- Recovery starts as `SIMPLE`, which avoids requiring transaction-log backups
  during the pilot.

Change recovery to `FULL` only after a DBA has configured recurring transaction
log backups and taken the required full backup. Database backups, integrity
checks, retention, and restore testing remain DBA/operations responsibilities.
The TechBench desktop client has no database-backup command and cannot create,
schedule, verify, or restore a SQL Server backup.

## Stored-procedure contract

- `tb_app.GetCurrentUserContext` identifies and registers the current Windows
  user and returns effective TechBench role flags.
- `tb_app.SearchClients` and `tb_app.GetClient` read shared clients.
- `tb_app.AdminSaveClient` creates or updates clients for application
  administrators and uses SQL Server `rowversion` conflict detection.
- V0002 procedures store tickets, work entries, owner-private Personal Notes,
  links, drafts, templates, Common Links, organization/user settings, aliases,
  posting coordination, synchronization runs, imports, and legacy mappings.
- V0003 promotes Common Links and import/customer aliases to organization
  scope, adds the canonical shared tag catalog, and separates shared WHD/Sage
  defaults from per-user identity settings.
- V0004 makes shared mutation and WHD/Sage synchronization strictly Admin-only,
  makes built-in Common Links Admin-editable but non-removable, turns common
  tags into an Admin-curated catalog, and moves the WHD auto-sync schedule into
  organization settings. The first V0004 Admin initialization records
  `WorkspaceDefaults.Initialized=4` with that Admin's identity.
- V0005 lets every `tb_role_user` import their own TechBench V1 history. SQL
  derives the owner SID from `ORIGINAL_LOGIN()`, keys legacy mappings by that
  owner and legacy ID, skips unchanged prior-batch retries, and reports changed
  legacy content as a conflict instead of overwriting it. A resumed active batch
  continues to count mappings first accepted by that batch as imported. Multiple
  legacy link IDs may map to one equivalent SQL relationship, while work-entry
  and posting-log reverse mappings remain unique. Dependent links and posting
  logs require work-entry mappings accepted by the current batch, so a stale
  mapping cannot attach history to an omitted or conflicted row. Successful
  posting logs conservatively reconcile the work entry's posted flags,
  timestamps, and aggregate posting status. Successful completion requires one
  outcome for every read item and zero errors. A user can abandon only their own
  active V1 batch, including selecting that current batch by passing a null ID;
  the recovery is audited.
- `tb_app.ResolveTechBenchV1Reference` gives ordinary users a read-only,
  source-qualified exact resolver for V1 imports. It queries the authoritative
  client identities, Sage customer IDs, organization aliases/names, and ticket
  identities directly without fuzzy or capped-list lookup. Alias/name matches
  are accepted only when unambiguous and the procedure returns explicit match,
  not-found, ambiguous, conflict, or not-resolved statuses.
- Organization-scoped matching, aliases, Common Links, templates, settings,
  and WHD/Sage synchronization mutations require a TechBench Admin.
- The WHD automatic-sync enabled state and interval are stored as organization
  settings. An authorized workstation performs the external call because there
  is no server process; SQL leases coordinate competing Admin workstations.
- `tb_app.EnsureWorkspaceDefaults` is Admin-only. Before the initialization
  marker exists, it performs one insert-missing seed of the original Common
  Links and note templates and then records the marker with the real Admin SID.
  Later startups never recreate a renamed or deleted template. Missing WHD
  auto-sync defaults are still repaired without overwriting existing values.
- Device-specific preferences remain in the workstation-local JSON settings
  file; they are intentionally not stored in SQL Server. The local refresh
  interval controls only how often that workstation reloads shared data.
- `tb_app.ReadAuditEvents` is restricted to application administrators.

The application roles receive no direct table write access. Client changes and
their audit event are committed in one SQL transaction.

## Client connection

Use the DNS hostname and Windows authentication:

```text
Server=CSRI-SQL.CSRI.local;Database=TechBench;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=False
```

The SQL Server certificate must be trusted by the workstations before
production use. Do not put the `sa` password or any SQL login password in the
client connection string.

## After deployment

1. Add ordinary users to `CSRI\TechBench_Users`.
2. Add application administrators to `CSRI\TechBench_Admins`.
3. Have affected users sign out of Windows and sign back in so their group
   membership token refreshes.
4. Test the V2 client using `CSRI-SQL.CSRI.local` and database `TechBench`.
5. Verify an ordinary user cannot change shared settings or run WHD/Sage
   synchronization, and verify an Admin can perform the intended shared actions.
6. Verify an ordinary user can start or abandon a V1 import only for their own
   Windows SID. Confirm a same-batch resume preserves imported outcomes, a later
   unchanged retry skips rather than duplicates rows, and equivalent legacy link
   IDs can reuse one SQL relationship.
7. Have the DBA configure and test SQL Server backups, integrity checks, and a
   restore before production data entry. These operations are not performed by
   the TechBench client.

The final verification script prints the database name, compatibility level,
recovery model, owner, instance identifier, and installed schema migration when
the deployment succeeds.
