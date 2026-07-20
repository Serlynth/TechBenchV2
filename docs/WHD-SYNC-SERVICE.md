# TechBench Sync Service

`TechBench.SyncService` is the server-side worker that performs organization-wide Web Help Desk (WHD) synchronization and manually requested Sage customer synchronization. Install it on a domain-joined Windows Server that can reach TechBench SQL Server, the WHD endpoint, and the Sage company data.

## Build the service package

From the repository root, create a self-contained `win-x64` service package with its isolated self-contained `win-x86` Sage ODBC worker:

```powershell
.\scripts\Publish-TechBenchServer.ps1 -Version 2.0.0-alpha.8
```

The package is created at `dist\TechBenchSyncService-2.0.0-alpha.8-win-x64.zip`, with a SHA-256 sidecar. It includes the x64 service, x86 Sage worker, `appsettings.json`, runbook, release notes, install/uninstall/credential scripts, and matching standalone SQLCMD deployment under `database`. Do not place either external-system secret in the package or in `appsettings.json`.

The same command also creates the directly downloadable
`dist\TechBenchV2-SQLServer2016-2.0.0-alpha.8.sql` and its checksum. After the
matching client publisher has created GitHub release `v2.0.0-alpha.8`, attach
all four server-side assets with:

```powershell
.\scripts\Publish-TechBenchServer.ps1 `
  -Version 2.0.0-alpha.8 `
  -Publish
```

The server publisher will not create a release or overwrite an asset. Publish
the client first with `Publish-TechBenchRelease.ps1 -Publish`, then publish the
server/SQL assets.

## Deploy the database first

Before installing the service or alpha.8 clients, stop the old V2 service/clients, have the DBA back up `TechBench`, review `database\README-Deploy.md`, and execute `database\Deploy-CSRI-Standalone.sql` in SSMS while connected to `CSRI-SQL` as a SQL Server sysadmin with **Query > SQLCMD Mode** enabled. The script creates or upgrades schema version 7 and verifies the service-only WHD/Sage permissions, ticket row-security policy, and restricted Admin preview boundary. Stop if any verification reports a failure.

If you download the versioned standalone SQL asset instead of taking it from
the service ZIP, verify its sidecar before opening it in SSMS:

```powershell
$sql = '.\TechBenchV2-SQLServer2016-2.0.0-alpha.8.sql'
$expectedHash = ((Get-Content "$sql.sha256" -Raw) -split '\s+')[0]
$actualHash = (Get-FileHash $sql -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'TechBench SQL SHA-256 does not match.' }
```

## Prerequisites

- Run the installer from an elevated PowerShell session on the target server.
- Use the dedicated, least-privilege `CSRI\TechBench_Sync` AD domain account. The default SQL deployment maps that account directly to the service-only database role; no same-named AD group is required. Do not add it directly or through a nested group to `TechBench_Users`, `TechBench_Admins`, or any unrelated SQL role.
- For a gMSA, install the account on the host and grant that host permission to retrieve its password before installing the service. The installer calls `Test-ADServiceAccount` when the ActiveDirectory module is available.
- For an ordinary domain account, ensure the supplied account is allowed to log on as a service. The installer adds that right when local policy permits it; a domain GPO can override the local assignment.
- Review the package `appsettings.json`; it contains only the SQL Server name, database name, timeouts, worker paths, and worker tuning. Admins configure the WHD endpoint/service username and the server Sage DSN/username in the TechBench client, which stores them in SQL Server.
- Confirm the service host trusts the SQL Server certificate used by `CSRI-SQL.CSRI.local`; production keeps `TrustServerCertificate` set to `false`.
- Install the supported 32-bit Sage ODBC driver on the service host. Use `%windir%\SysWOW64\odbcad32.exe` to create a **System DSN**; a User DSN or mapped drive will not be visible reliably to the Windows service.
- Give `CSRI\TechBench_Sync` read access to the Sage data location and any other file/share rights required by the Sage ODBC driver. Use UNC paths rather than mapped drive letters.

## Install

For a normal domain account, PowerShell prompts for the Windows service account password without writing it to a command line or a file:

```powershell
$package = '.\TechBenchSyncService-2.0.0-alpha.8-win-x64.zip'
$expectedHash = ((Get-Content "$package.sha256" -Raw) -split '\s+')[0]
$actualHash = (Get-FileHash $package -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'TechBench service package SHA-256 does not match.' }

Unblock-File $package
Expand-Archive $package .\TechBenchSyncService
Get-ChildItem .\TechBenchSyncService -Recurse -File | Unblock-File

# Applies only to this elevated PowerShell process. It does not change the
# computer-wide execution policy.
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
Set-Location .\TechBenchSyncService
.\Install-TechBenchSyncService.ps1 `
  -ServiceAccount 'CSRI\TechBench_Sync' `
  -ConfigureWhdCredential `
  -ConfigureSageCredential
```

Download the ZIP and its matching `.sha256` sidecar from the same release. Do not run the scripts if the hash comparison fails. `Unblock-File` removes the Internet mark inherited from the downloaded ZIP; the process-scoped execution-policy setting handles the unsigned internal deployment scripts for only that PowerShell window. If domain policy prevents the process-scoped setting, have your administrator sign or explicitly approve the scripts instead of weakening machine-wide policy.

The predictable alpha.8 server download is
`https://github.com/Serlynth/TechBenchV2-Releases/releases/download/v2.0.0-alpha.8/TechBenchSyncService-2.0.0-alpha.8-win-x64.zip`.
Download its `.sha256` sidecar by appending `.sha256` to that URL. The standalone
SQL asset follows the same pattern with filename
`TechBenchV2-SQLServer2016-2.0.0-alpha.8.sql`.

The prepared CSRI deployment does not use a gMSA. If a future deployment
switches to one, first redeploy SQL with that exact gMSA (or a dedicated group
containing it) as `SyncServicePrincipal`; then include the trailing `$` and do
not supply `-Credential`:

```powershell
.\Install-TechBenchSyncService.ps1 `
  -ServiceAccount 'CSRI\gmsa_techbench_sync$' `
  -ConfigureWhdCredential `
  -ConfigureSageCredential
```

The installer copies binaries to `%ProgramFiles%\CSRI\TechBench Sync Service`, creates `TechBenchWhdSync`, and preserves an existing `appsettings.json` unless `-ReplaceConfiguration` is supplied. It creates `%ProgramData%\CSRI\TechBench Sync Service` with an explicit ACL: SYSTEM and Administrators have Full Control, and the service identity has Modify. It also grants the service identity read-and-execute access to the install directory. Custom install and data directories must remain dedicated child folders under `%ProgramFiles%\CSRI` and `%ProgramData%\CSRI`; the installer rejects root or reparse-point paths before changing permissions.

The service is set to automatic delayed start, is given an SCM description, and restarts after its first three failures (after 60 seconds, 60 seconds, and 5 minutes; the failure counter resets after one day). Start is skipped when `-SkipStart` is specified or when no protected WHD credential has been provisioned yet. Sage can be configured later without preventing WHD synchronization from running.

## Store or rotate the WHD credential

The WHD API key, token, or password is prompted as a `SecureString` and passed to the executable over redirected standard input. It is never added to a command line, configuration file, package, transcript, or log by these scripts.

```powershell
.\Set-TechBenchSyncCredential.ps1
```

The executable protects the credential with machine-scoped DPAPI and writes it to `%ProgramData%\CSRI\TechBench Sync Service\whd.secret`. The service can read it because its identity has Modify access to that directory. `Set-TechBenchSyncCredential.ps1` restarts the service by default; use `-NoRestart` as part of a controlled maintenance window. To configure it during installation, add `-ConfigureWhdCredential` to the install command.

## Configure Sage customer synchronization

In the TechBench V2 client, sign in normally as a member of `CSRI\TechBench_Admins`. In Settings, enter the server's 32-bit Sage **System DSN** name and Sage ODBC username, then save. These non-secret values are shared in SQL Server.

On the service host, provision the separate Sage ODBC password:

```powershell
.\Set-TechBenchSageSyncCredential.ps1
```

The script protects it with machine-scoped DPAPI in `%ProgramData%\CSRI\TechBench Sync Service\sage.secret` and restarts `TechBenchWhdSync` by default. The WHD and Sage credentials are independent; do not enter the Windows service-account password or WHD secret at this prompt.

Sage customer synchronization has no automatic schedule. A TechBench Admin selects **Request Server Sage Sync**; the request is stored in SQL Server, and the service claims it. SQL Server rejects empty, malformed, over-length, or duplicate-ID snapshots without changing customer data. If a snapshot would remove at least 10 and at least 25 percent of 20 or more existing Sage mappings, the failed request reports the exact counts and Settings shows **Confirm Large Removal**. That second action displays a warning and queues a new request bound to the rejected request. It is accepted only within one hour and only when the fresh read has exactly the same read, existing, and stale counts; otherwise no customer data changes and the Admin must review the new proposal. WHD synchronization continues automatically every five minutes and may also be requested immediately by an Admin.

## Verify and remove

```powershell
Get-Service TechBenchWhdSync
sc.exe qc TechBenchWhdSync
sc.exe qfailure TechBenchWhdSync

.\Uninstall-TechBenchSyncService.ps1
```

Uninstall keeps the protected data directory by default so an upgrade does not discard either credential. To remove service binaries and both credentials permanently, run the uninstall script with `-RemoveCredential`. This operation is irreversible.
