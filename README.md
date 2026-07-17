# TechBench V2

TechBench V2 is the multi-user successor to TechBench 1.x. It keeps the existing Windows/WPF workstation experience while moving all business and operational data into the shared SQL Server database.

The original TechBench workspace is not modified. V1 and V2 have separate product identities, executables, mutex names, settings, credential namespaces, packages, and update feeds.

Current milestone: `2.0.0-alpha.3` - the server-backed client conversion and organization-wide reference-data boundary are implemented. It still requires deployment to the real SQL Server 2016 instance and domain-user smoke testing before it should be treated as production-ready.

## What V2 stores where

SQL Server is the source of truth for:

- clients, organization-wide aliases, external WHD/Sage identities, and canonical client matching
- tickets and ticket status options
- work entries, Personal Notes, links, follow-ups, and search/history
- editor recovery drafts
- organization-wide Common Links, shared templates, and the canonical tag catalog
- organization and user-scoped application settings
- posting logs, durable posting attempts, and posting leases
- WHD/Sage synchronization leases and runs
- import batches, legacy-ID mappings, and audit history

The workstation keeps only non-business state:

- the SQL Server address and database name, without credentials
- a generated device ID
- theme, window position/size/state, refresh intervals, and similar device preferences
- device-specific Sage/WHD/update/browser options
- WHD and Sage secrets protected by Windows Credential Manager
- installed application/update artifacts and temporary files explicitly created by the user

The production client neither references nor packages SQLite. Test builds retain the legacy SQLite repository only for regression and migration-boundary testing. V2 has no offline business-data store, so the SQL Server must be reachable to use the application.

## Deployment model

There is no TechBench web server, API process, container, or background server service to install.

~~~text
TechBench V2 WPF client (x86)
    -> Microsoft.Data.SqlClient
    -> Windows Integrated Authentication
    -> encrypted SQL Server connection
    -> CSRI-SQL.CSRI.local
    -> TechBench database, schema version 3
~~~

WHD API work, Sage ODBC access, and Sage desktop automation still run from the technician workstation. SQL Server stores the durable state and coordinates posting and synchronization across workstations.

The V2 client uses short-lived pooled connections and stored procedures. It does not hold a database transaction open while calling WHD or Sage.

## Authentication and authorization

Users do not create a separate TechBench username or enter a database password. SQL Server authenticates the Windows identity of the person running TechBench.

The prepared CSRI mapping is:

| Active Directory group | Database roles |
|---|---|
| `CSRI\TechBench_Users` | `tb_role_user` |
| `CSRI\TechBench_Admins` | `tb_role_user`, `tb_role_manager`, `tb_role_admin`, `tb_role_sync_operator` |

The database derives the caller's durable owner identity from the Windows SID. Stored procedures enforce owner and role checks. Hiding or disabling a WPF control is only a user-interface convenience and is not the authorization boundary.

Application users receive execution rights on approved stored procedures. They are not granted broad `db_datareader`, `db_datawriter`, `db_owner`, or direct table DML access.

## Requirements

Development:

- Windows
- .NET SDK 8.0.422, pinned by `global.json`

Deployment:

- domain-joined or trusted-domain Windows workstations
- SQL Server 2016 at compatibility level 130
- the `TechBench` database at schema version 3
- the CSRI Active Directory groups mapped by the database deployment
- TCP connectivity from workstations to SQL Server
- TLS 1.2 and a SQL Server certificate trusted by the workstations
- a tested SQL Server backup, integrity-check, and restore process

The production client remains x86 because Sage desktop integration requires it.

SQL Server 2016 extended support ended July 14, 2026. Before production use, confirm the installed service pack/security-update posture and Extended Security Updates coverage, or document a database upgrade plan. See the [Microsoft SQL Server 2016 lifecycle](https://learn.microsoft.com/lifecycle/products/sql-server-2016).

## Database deployment

The DBA-owned deployment package is in [database/sqlserver2016](database/sqlserver2016). The standalone script creates or upgrades the database and installs the complete schema-version-3 stored-procedure contract:

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

The desktop application checks the schema version at startup and refuses an incompatible database. Version `2.0.0-alpha.3` requires database schema version `3`.

### Coordinated alpha.3 upgrade

The database and client must be upgraded as one planned cutover:

1. Back up the `TechBench` database.
2. Run the complete schema-version-3 standalone deployment and confirm its verification output.
3. Install the alpha.3 client.
4. Test with at least one ordinary domain user and one TechBench administrator.
5. Verify shared clients, tickets, canonical customer matching, aliases, tags, templates, Common Links, work entries, Personal Note privacy, drafts, posting coordination, synchronization, automatic refresh, and optimistic-concurrency conflicts.
6. Configure and test ongoing backups before production data entry.

Do not deploy only one side. The alpha.3 client rejects schema versions 1 and 2; earlier alpha clients are not compatible with the completed schema-version-3 contract.

Users newly added to an AD group should sign out of Windows and sign back in before testing so their Windows security token includes the new membership.

## Build and test

~~~powershell
dotnet restore TechBenchV2.sln
dotnet build TechBench.csproj -c Release
dotnet test TechBench.Tests\TechBench.Tests.csproj -c Release
~~~

A production artifact check should also confirm that the output contains no `Microsoft.Data.Sqlite`, `SQLitePCLRaw`, or `e_sqlite3` dependency.

Unit and contract tests do not replace the required integration run against the actual SQL Server 2016 instance. Testing only on a newer SQL Server at compatibility level 130 cannot detect every engine-version difference.

## V1 migration and rollback rules

- Never point V2 at a live V1 SQLite database.
- Never place a SQLite database on a network share.
- Import only from a verified copy of a V1 database.
- Assign imported work to explicit Windows/AD SIDs.
- Preserve legacy identifiers through the server-side mapping tables.
- Reconcile WHD and Sage identities before fuzzy client-name matching.
- Verify counts, relationships, ownership, posting state, and sample note content before cutover.
- Do not run V1 and V2 as dual writable production systems.

V1 remains untouched and available for rollback or historical reference. Its data is not automatically migrated by installing alpha.3.

For implementation details, see [docs/V2-ARCHITECTURE.md](docs/V2-ARCHITECTURE.md). For the DBA runbook, see [database/sqlserver2016/README-Deploy.md](database/sqlserver2016/README-Deploy.md).
