# TechBench V2 SQL Server 2016 deployment

This package creates the first TechBench V2 SQL Server vertical slice:

- Windows-integrated current-user identity, keyed by the caller's immutable Windows SID
- AD-group-backed Technician, Manager, Admin, Sync Operator, Deployer, and Audit Reader roles
- Shared client storage with SQL Server `rowversion` optimistic concurrency
- Append-only client audit events
- Stored-procedure-only application access
- SQL Server 2016 compatibility level 130

The scripts are DBA-owned and do not grant desktop users direct table access. The WPF client calls only procedures in the `tb_app` schema.

## Important integration boundary

The procedures derive identity from the Windows login authenticated by SQL Server:

- `SUSER_SID(ORIGINAL_LOGIN())` supplies the owner/actor SID.
- `ORIGINAL_LOGIN()` supplies the account name.
- SQL database-role membership supplies Technician, Manager, and Admin authorization.

No callable procedure accepts a caller-provided owner SID or role flag.

This requires each desktop connection to use Windows Integrated Security. If a future API server connects with one shared service identity, SQL Server will see the service SID rather than the end user's SID; that architecture must use Kerberos delegation or a separately reviewed trusted-context design.

## Prerequisites

- SQL Server 2016 (13.x) or newer
- A Windows-authenticated DBA account with `sysadmin` for the initial deployment
- The following AD principals identified before deployment:
  - A database owner login or existing DBA owner group
  - A deployment group
  - A Technician group
  - An Admin group
  - Optional Manager, Sync Operator, and Audit Reader groups
- `sqlcmd`, or SSMS with SQLCMD mode enabled
- A database backup and change window for any redeployment to an existing database

Use direct AD groups rather than relying on deeply nested group membership. SQL Server role checks can depend on domain-controller availability for indirect Windows membership.

## SQLCMD variables

Every script uses the same variables:

| Variable | Example |
|---|---|
| `DatabaseName` | `TechBenchV2` |
| `DatabaseOwnerLogin` | `CONTOSO\GG-SQL-TechBench-Owners` |
| `DeploymentGroup` | `CONTOSO\GG-TechBench-DB-Deploy` |
| `TechnicianGroup` | `CONTOSO\GG-TechBench-Technicians` |
| `ManagerGroup` | `CONTOSO\GG-TechBench-Managers` |
| `AdminGroup` | `CONTOSO\GG-TechBench-Admins` |
| `SyncOperatorGroup` | `CONTOSO\GG-TechBench-Sync-Operators` |
| `AuditReaderGroup` | `CONTOSO\GG-TechBench-Audit-Readers` |

The database name must not contain a closing bracket (`]`). Principal names must resolve as Windows users or groups from the SQL Server host.

For this small installation, only two application groups are necessary:

- `TechBench Users` for `TechnicianGroup`
- `TechBench Admins` for `AdminGroup`

Until separate responsibilities are needed, `ManagerGroup`,
`SyncOperatorGroup`, and `AuditReaderGroup` may all use the Admin group.
`DatabaseOwnerLogin` and `DeploymentGroup` may use existing DBA principals,
but should be distinct from each other and from the application groups.
Do not use the Admin group as the Technician group, or every user will receive
administration rights.

## Deployment order

Run all scripts from this directory in order:

1. `00-Preflight.sql`
2. `10-CreateDatabase.sql`
3. `20-BaselineSchema.sql`
4. `30-Security.sql`
5. `40-StoredProcedures.sql`
6. `50-Grants.sql`
7. `90-Verify.sql`

Example PowerShell:

```powershell
$variables = @(
    'DatabaseName=TechBenchV2'
    'DatabaseOwnerLogin=CONTOSO\GG-SQL-TechBench-Owners'
    'DeploymentGroup=CONTOSO\GG-TechBench-DB-Deploy'
    'TechnicianGroup=CONTOSO\GG-TechBench-Technicians'
    'ManagerGroup=CONTOSO\GG-TechBench-Managers'
    'AdminGroup=CONTOSO\GG-TechBench-Admins'
    'SyncOperatorGroup=CONTOSO\GG-TechBench-Sync-Operators'
    'AuditReaderGroup=CONTOSO\GG-TechBench-Audit-Readers'
)

Get-ChildItem -Filter '*.sql' |
    Sort-Object Name |
    ForEach-Object {
        sqlcmd -S 'SQLSERVER01' -E -b -I -v $variables -i $_.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Deployment stopped at $($_.Name)."
        }
    }
```

For a named instance, use a server value such as `SQLSERVER01\INSTANCE`.

## Database roles

| Database role | AD membership | Effective access |
|---|---|---|
| `tb_role_user` | Technician, Manager, Admin groups | Execute current-user and shared-client read procedures |
| `tb_role_manager` | Manager and Admin groups | Reserved for reporting procedures in later migrations |
| `tb_role_admin` | Admin group | Execute client administration procedures |
| `tb_role_sync_operator` | Sync Operator group | Read shared clients and run future centrally coordinated synchronization procedures |
| `tb_role_auditor` | Audit Reader group | Execute the audit read procedure |
| `tb_role_deployer` | Deployment group | `CONTROL` on the TechBench database for reviewed releases |

The group hierarchy is implemented explicitly in SQL membership:

- Technician group → `tb_role_user`
- Manager group → `tb_role_user`, `tb_role_manager`
- Admin group → `tb_role_user`, `tb_role_manager`, `tb_role_admin`
- Sync Operator group → `tb_role_sync_operator`

No application group is added to `db_datareader`, `db_datawriter`, `db_ddladmin`, or `db_owner`.

## Stored procedure contract

### `tb_app.GetCurrentUserContext`

No parameters. Returns:

1. `UserSid` (`varbinary(85)`)
2. `LoginName` (`nvarchar(256)`)
3. `DisplayName` (`nvarchar(160)`)
4. `DatabaseInstanceId` (`uniqueidentifier`)
5. `SchemaVersion` (`int`)
6. `ServerUtc` (`datetime2(3)`)
7. `IsTechnician` (`bit`)
8. `IsManager` (`bit`)
9. `IsAdmin` (`bit`)
10. `IsSyncOperator` (`bit`)

The procedure creates or refreshes the caller's profile using only SQL Server's authenticated Windows identity and effective database roles. Role state is derived on every call and cannot be asserted by the desktop.

### `tb_app.SearchClients`

Parameters:

- `@IncludeInactive bit = 0`
- `@Search nvarchar(240) = NULL`
- `@Limit int = 250`, clamped to 1–1000

Returns the current `Client` model fields:

`Id`, `Name`, `Source`, `ExternalId`, `IsActive`, `LastSyncedAt`,
`WhdLocationName`, `WhdContactName`, `SageCustomerId`,
`SageCustomerName`, `SageContactName`, `SageTelephone`, `MatchStatus`,
and `RowVersion`.

Search text is treated literally; SQL wildcard characters are escaped.

### `tb_app.AdminSaveClient`

Creates a client when `@Id` is null and updates it otherwise.

Updates require the exact `@ExpectedRowVersion binary(8)` returned by a prior read. A stale value raises SQL error `51012`. The procedure verifies live `tb_role_admin` membership, writes the client and audit event in one transaction, and returns the saved row.

### `tb_app.ReadAuditEvents`

Available only to `tb_role_auditor` and `tb_role_deployer`.

## Client connection

Use Windows authentication and encryption:

```text
Server=SQLSERVER01;Database=TechBenchV2;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=False
```

The SQL Server certificate must be trusted by client machines. Do not work around certificate errors with `TrustServerCertificate=True` in production.

The Phase 1 WPF client calls `GetCurrentUserContext` and `SearchClients`
directly through `Microsoft.Data.SqlClient`. Later phases will add repository
adapters and procedures for work entries, notes, tickets, posting, and imports.

## Concurrency

`tb_data.Clients.RowVersion` is SQL Server `rowversion`. Treat it as an opaque eight-byte value:

- Preserve the bytes returned by reads.
- Send the exact bytes as `@ExpectedRowVersion` on update.
- On SQL error `51012`, reload and show a conflict rather than retrying blindly.

Do not convert rowversion to a numeric counter.

## Audit and ownership

- `CreatedByWindowsSid`, `UpdatedByWindowsSid`, and `ActorWindowsSid` are derived inside SQL from the authenticated login.
- Audit rows are inserted by `AdminSaveClient` in the same transaction as the client mutation.
- Application roles receive no update/delete permission on `tb_audit.AuditEvents`.
- DBA/deployer authority can still modify data by design; use SQL Server Audit and protected backups if compliance requires monitoring privileged DBA activity.

## Schema versioning

`tb_deploy.SchemaMigrations` records the installed package migration and script checksum field. The first migration is:

```text
SqlServer2016.Baseline.0001
```

Future changes should be additive, ordered scripts such as:

```text
21-V0002-WorkEntries.sql
22-V0003-Tickets.sql
```

Production applications should validate the installed schema version at startup, but should not run DDL migrations under an application login.

## Rollback

The initial scripts create security principals and persistent data. There is deliberately no automated destructive rollback.

Before deployment:

1. Back up an existing database.
2. Capture the current role membership and migration rows.
3. Test the scripts against a restored non-production copy.

If an initial empty deployment must be abandoned, a DBA may drop the database after confirming that it contains no production data. Once clients have been created, use a forward migration or restore the approved backup.
