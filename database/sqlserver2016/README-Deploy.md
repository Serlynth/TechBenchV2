# TechBench V2 SQL Server 2016 deployment

This package creates the shared `TechBench` database used by the TechBench V2
WPF client and the dedicated TechBench sync service. Both use Windows Integrated
Authentication; the service has a separate least-privilege database role.

TechBench V2 `0.5.39` requires schema version `12`, including the server-owned
WHD/Sage/Credentials synchronization contracts and encrypted credential
storage, restricted Admin read-only user preview, administrator-only shared-
configuration boundary, owner-scoped V1 import, and verified missing-WHD-
TechNote recovery deletion contracts, plus Admin-controlled client presence
and safe sign-out requests in this package. Schema 11 records recipient
acknowledgements and written responses, and reports whether a forced sign-out
safely stored the user's current editor recovery draft. Schema 12 stores
runtime-discovered Credentials workbook columns as individually encrypted
fields so new headers appear after synchronization without another schema change.
The 0.5.39 procedure set also recovers assigned WHD administrators that WHD
12.x omits from its normal technician collection. Reapply the complete
0.5.39 deployment even when the database already reports schema 12.

## CSRI production deployment

The prepared CSRI configuration is:

| Setting | Value |
|---|---|
| SQL Server | `CSRI-SQL` |
| Database | `TechBench` |
| Standard users | `CSRI\TechBench_Users` |
| Application administrators | `CSRI\TechBench_Admins` |
| TechBench sync service | `CSRI\TechBench_Sync` |
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

Three distinct AD principals are required:

| AD principal | Database roles |
|---|---|
| `CSRI\TechBench_Users` | `tb_role_user` |
| `CSRI\TechBench_Admins` | `tb_role_user`, `tb_role_manager`, `tb_role_admin`, `tb_role_sync_operator` |
| `CSRI\TechBench_Sync` | `tb_role_sync_service` only |

Administrators receive normal user access through their role mapping, so they
do not need membership in both AD groups.

The TechBench service uses the separate `CSRI\TechBench_Sync` AD account, which is
mapped only to `tb_role_sync_service`. It is not an application Admin and has
execution rights only for the leased `tb_service` WHD, Sage, and Credentials contracts. The prepared
CSRI deployment maps that service account directly; no same-named AD group is
required. Do not place the service account in either TechBench application
group.

Organization-wide configuration and manual WHD/Sage/Credentials synchronization requests require
the effective `tb_role_admin` role. `tb_role_sync_operator` is retained for upgrade
compatibility and synchronization-history inspection; membership in that role
alone does not authorize a shared synchronization run. Ordinary users can read
shared catalogs but cannot change matching or aliases, Common Links, note
templates, organization defaults, the WHD automatic-sync schedule, or snapshot
state. Every authenticated TechBench user may execute only the approved
Credentials search and explicit reveal procedures. All encrypted
credential tables and SQL encryption keys remain inaccessible directly.

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
| `SyncServicePrincipal` | `CSRI\TechBench_Sync` |

Run the scripts in this order:

1. `00-Preflight.sql`
2. `10-CreateDatabase.sql`
3. `20-BaselineSchema.sql`
4. `21-V0002-OperationalSchema.sql`
5. `22-V0003-SharedReferenceData.sql`
6. `23-V0004-AdminOwnedSharedConfig.sql`
7. `24-V0005-TechBenchV1ImportSchema.sql`
8. `25-V0006-WhdServerSyncSchema.sql`
9. `26-V0007-ServerOwnedSageAndAdminPreviewSchema.sql`
10. `30-Security.sql`
11. `40-StoredProcedures.sql`
12. `41-V0002-WorkProcedures.sql`
13. `42-V0002-SharedProcedures.sql`
14. `43-V0002-PostingProcedures.sql`
15. `44-V0002-SyncImportProcedures.sql`
16. `45-V0003-SharedReferenceProcedures.sql`
17. `46-V0004-AdminSharedProcedures.sql`
18. `47-V0005-TechBenchV1ImportProcedures.sql`
19. `48-V0006-WhdServerSyncProcedures.sql`
20. `49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql`
21. `50-Grants.sql`
22. `51-V0002-OperationalGrants.sql`
23. `52-V0004-AdminSharedGrants.sql`
24. `53-V0005-TechBenchV1ImportGrants.sql`
25. `54-V0006-WhdServerSyncGrants.sql`
26. `55-V0007-ServerOwnedSageAndAdminPreviewGrants.sql`
27. `90-Verify.sql`
28. `91-V0002-OperationalVerify.sql`
29. `92-V0003-SharedReferenceVerify.sql`
30. `93-V0004-AdminSharedVerify.sql`
31. `94-V0005-TechBenchV1ImportVerify.sql`
32. `95-V0006-WhdServerSyncVerify.sql`
33. `96-V0007-ServerOwnedSageAndAdminPreviewVerify.sql`

The scripts are idempotent for the baseline, V0002 operational-storage, V0003
shared-reference-data, V0004 Admin-owned-configuration, and V0005 owner-scoped
V1-import, V0006 WHD server-sync, and V0007 server-Sage/Admin-preview migrations. They stop on validation or
deployment errors.

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
  scope, retains the original organization-tag catalog for migration
  compatibility, and separates shared WHD/Sage defaults from per-user identity
  settings. Current tag suggestions are derived from each effective user's
  saved work entries.
- V0004 makes shared mutation and WHD/Sage synchronization strictly Admin-only,
  makes built-in Common Links Admin-editable but non-removable, retains the
  legacy Admin tag procedures for upgrade compatibility, and moves the WHD
  auto-sync schedule into organization settings. The first V0004 Admin initialization records
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
- V0006 introduces a dedicated `CSRI\TechBench_Sync` Windows login and
  least-privilege `tb_role_sync_service` role for server-side WHD ingestion.
  Admins can request, monitor, and map Windows users to WHD technicians, but
  only the service role can claim leased work or apply WHD JSON snapshots.
  Ticket batches never treat omission as deletion; a ticket closes or becomes
  deleted only when WHD explicitly supplies that state. Direct technician and
  synchronized group membership limit non-Admin WHD ticket reads and writes.
  An enabled row-level-security filter/block policy enforces that boundary for
  ticket-linked work entries, V1 imports, and future table access paths as well
  as the primary ticket procedures.
- V0007 moves Sage customer ingestion behind a durable Admin-requested,
  service-claimed queue. Only `tb_role_sync_service` can claim/renew work or
  apply a validated nonempty Sage snapshot; the desktop client can no longer
  submit customer rows. V0007 also creates short-lived Admin-authorized user
  preview sessions and the `tb_preview_reader` `WITHOUT LOGIN` principal. Every
  preview connection is switched to that restricted principal, has only an
  approved read-procedure allowlist, follows the target user's WHD visibility,
  redacts Personal Notes, and cannot load editor drafts or mutate data. A target
  must have opened V2 within the past hour so preview eligibility is backed by
  a recent role refresh; a zero-role refresh is persisted before access denial.
- `tb_app.ResolveTechBenchV1Reference` gives ordinary users a read-only,
  source-qualified exact resolver for V1 imports. It queries the authoritative
  client identities, Sage customer IDs, organization aliases/names, and ticket
  identities directly without fuzzy or capped-list lookup. Alias/name matches
  are accepted only when unambiguous and the procedure returns explicit match,
  not-found, ambiguous, conflict, or not-resolved statuses.
- Organization-scoped matching, aliases, Common Links, templates, settings,
  and manual WHD/Sage synchronization requests require a TechBench Admin.
- The WHD automatic-sync enabled state and five-minute interval are stored as
  organization settings. The Windows sync service performs organization-wide
  WHD calls; Admin workstations only configure, queue, and monitor durable SQL
  work. Sage customer synchronization has no automatic enqueue path and runs
  only after an Admin request.
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
3. Run the TechBench sync service as the dedicated `CSRI\TechBench_Sync` account;
   do not add the account to either TechBench application group or the Admin
   role.
4. Have affected users sign out of Windows and sign back in so their group
   membership token refreshes.
5. Test the V2 client using `CSRI-SQL.CSRI.local` and database `TechBench`.
6. Verify an ordinary user cannot change shared settings or manually request
   WHD/Sage synchronization, and verify an Admin can queue both intended jobs.
7. Configure the server's 32-bit Sage System DSN and protected Sage credential,
   request a Sage customer sync as an Admin, and verify the resulting row counts.
   Also verify malformed or duplicate snapshot input changes no customer data and
   that an unusually large removal requires the separate Admin confirmation path.
8. Have the ordinary domain user open V2, then within one hour preview that user
   as an Admin and verify target-scoped WHD tickets, Personal Note redaction,
   editor-draft denial, and write denial.
9. Verify an ordinary user can start or abandon a V1 import only for their own
   Windows SID. Confirm a same-batch resume preserves imported outcomes, a later
   unchanged retry skips rather than duplicates rows, and equivalent legacy link
   IDs can reuse one SQL relationship.
10. Have the DBA configure and test SQL Server backups, integrity checks, and a
   restore before production data entry. These operations are not performed by
   the TechBench client.

The final verification script prints the database name, compatibility level,
recovery model, owner, instance identifier, and installed schema migration when
the deployment succeeds.
