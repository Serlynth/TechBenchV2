# TechBench WHD Sync Service

`TechBench.SyncService` is the server-side worker that performs the organization-wide Web Help Desk (WHD) synchronization. Install it on a domain-joined Windows Server that can reach the TechBench SQL Server and the WHD endpoint.

## Build the service package

From the repository root, create a self-contained `win-x64` package:

```powershell
.\scripts\Publish-TechBenchServer.ps1 -Version 2.0.0-alpha.6
```

The package is created at `dist\TechBenchSyncService-2.0.0-alpha.6-win-x64.zip`, with a SHA-256 sidecar. It includes the executable, `appsettings.json`, the runbook, release notes, install/uninstall/credential scripts, and the matching standalone SQLCMD deployment under `database`. Do not place a WHD secret in the package or in `appsettings.json`.

## Deploy the database first

Before installing the service or alpha.6 clients, have the DBA back up `TechBench`, review `database\README-Deploy.md`, and execute `database\Deploy-CSRI-Standalone.sql` in SSMS while connected to `CSRI-SQL` as a SQL Server sysadmin with **Query > SQLCMD Mode** enabled. The script creates or upgrades schema version 6 and verifies the service-only permissions and WHD ticket row-security policy. Stop if any verification reports a failure.

## Prerequisites

- Run the installer from an elevated PowerShell session on the target server.
- Use the dedicated, least-privilege `CSRI\TechBench_Sync` AD domain account. The default SQL deployment maps that account directly to the service-only database role; no same-named AD group is required. Do not add it directly or through a nested group to `TechBench_Users`, `TechBench_Admins`, or any unrelated SQL role.
- For a gMSA, install the account on the host and grant that host permission to retrieve its password before installing the service. The installer calls `Test-ADServiceAccount` when the ActiveDirectory module is available.
- For an ordinary domain account, ensure the supplied account is allowed to log on as a service. The installer adds that right when local policy permits it; a domain GPO can override the local assignment.
- Review the package `appsettings.json`; it contains only the SQL Server name, database name, timeouts, and worker tuning. Admins configure the WHD endpoint, service username, authentication mode, and schedule in the TechBench client, which stores them in SQL Server.
- Confirm the service host trusts the SQL Server certificate used by `CSRI-SQL.CSRI.local`; production keeps `TrustServerCertificate` set to `false`.

## Install

For a normal domain account, PowerShell prompts for the Windows service account password without writing it to a command line or a file:

```powershell
Expand-Archive .\TechBenchSyncService-2.0.0-alpha.6-win-x64.zip .\TechBenchSyncService
Set-Location .\TechBenchSyncService
.\Install-TechBenchSyncService.ps1 -ServiceAccount 'CSRI\TechBench_Sync' -ConfigureWhdCredential
```

The prepared CSRI deployment does not use a gMSA. If a future deployment
switches to one, first redeploy SQL with that exact gMSA (or a dedicated group
containing it) as `SyncServicePrincipal`; then include the trailing `$` and do
not supply `-Credential`:

```powershell
.\Install-TechBenchSyncService.ps1 -ServiceAccount 'CSRI\gmsa_techbench_sync$' -ConfigureWhdCredential
```

The installer copies binaries to `%ProgramFiles%\CSRI\TechBench Sync Service`, creates `TechBenchWhdSync`, and preserves an existing `appsettings.json` unless `-ReplaceConfiguration` is supplied. It creates `%ProgramData%\CSRI\TechBench Sync Service` with an explicit ACL: SYSTEM and Administrators have Full Control, and the service identity has Modify. It also grants the service identity read-and-execute access to the install directory. Custom install and data directories must remain dedicated child folders under `%ProgramFiles%\CSRI` and `%ProgramData%\CSRI`; the installer rejects root or reparse-point paths before changing permissions.

The service is set to automatic delayed start, is given an SCM description, and restarts after its first three failures (after 60 seconds, 60 seconds, and 5 minutes; the failure counter resets after one day). Start is skipped when `-SkipStart` is specified or when no protected WHD credential has been provisioned yet.

## Store or rotate the WHD credential

The WHD API key, token, or password is prompted as a `SecureString` and passed to the executable over redirected standard input. It is never added to a command line, configuration file, package, transcript, or log by these scripts.

```powershell
.\Set-TechBenchSyncCredential.ps1
```

The executable protects the credential with machine-scoped DPAPI and writes it to `%ProgramData%\CSRI\TechBench Sync Service\whd.secret`. The service can read it because its identity has Modify access to that directory. `Set-TechBenchSyncCredential.ps1` restarts the service by default; use `-NoRestart` as part of a controlled maintenance window. To configure it during installation, add `-ConfigureWhdCredential` to the install command.

## Verify and remove

```powershell
Get-Service TechBenchWhdSync
sc.exe qc TechBenchWhdSync
sc.exe qfailure TechBenchWhdSync

.\Uninstall-TechBenchSyncService.ps1
```

Uninstall keeps the protected data directory by default so an upgrade does not discard the WHD credential. To remove service binaries and the credential permanently, run the uninstall script with `-RemoveCredential`. This operation is irreversible.
