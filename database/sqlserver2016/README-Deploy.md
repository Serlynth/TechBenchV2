# TechBench V2 SQL Server 2016 deployment

This package creates the shared `TechBench` database used by the TechBench V2
WPF client. There is no separate TechBench server service. Each client connects
directly to SQL Server with Windows Integrated Authentication.

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
5. `30-Security.sql`
6. `40-StoredProcedures.sql`
7. `41-V0002-WorkProcedures.sql`
8. `42-V0002-SharedProcedures.sql`
9. `43-V0002-PostingProcedures.sql`
10. `44-V0002-SyncImportProcedures.sql`
11. `50-Grants.sql`
12. `51-V0002-OperationalGrants.sql`
13. `90-Verify.sql`
14. `91-V0002-OperationalVerify.sql`

The scripts are idempotent for the Phase 1 baseline and the V0002 operational
storage migration. They stop on validation or deployment errors.

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

## Stored-procedure contract

- `tb_app.GetCurrentUserContext` identifies and registers the current Windows
  user and returns effective TechBench role flags.
- `tb_app.SearchClients` and `tb_app.GetClient` read shared clients.
- `tb_app.AdminSaveClient` creates or updates clients for application
  administrators and uses SQL Server `rowversion` conflict detection.
- V0002 procedures store tickets, work entries, owner-private Personal Notes,
  links, drafts, templates, Common Links, organization/user settings, aliases,
  posting coordination, synchronization runs, imports, and legacy mappings.
- `tb_app.EnsureWorkspaceDefaults` idempotently creates the original seven
  built-in Common Links and seven note templates after the first authenticated
  application user has been registered.
- Device-specific preferences remain in the workstation-local JSON settings
  file; they are intentionally not stored in SQL Server.
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
5. Configure and test database backups before production data entry.

The final verification script prints the database name, compatibility level,
recovery model, owner, instance identifier, and installed schema migration when
the deployment succeeds.
