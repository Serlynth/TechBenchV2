# TechBench Sync Service

`TechBench.SyncService` is the server-side worker that performs organization-wide Web Help Desk (WHD) synchronization and manually requested Sage customer synchronization. Install it on a domain-joined Windows Server that can reach TechBench SQL Server, the WHD endpoint, and the Sage company data.

## Build the service package

From the repository root, create a self-contained `win-x64` service package with its isolated self-contained `win-x86` Sage ODBC worker:

```powershell
.\scripts\Publish-TechBenchServer.ps1 -Version 2.0.0-alpha.14
```

The package is created at `dist\TechBenchSyncService-2.0.0-alpha.14-win-x64.zip`, with a SHA-256 sidecar. It includes the x64 service, x86 Sage worker, the self-contained compiled Server Manager, `appsettings.json`, runbook, release notes, deployment/credential scripts, and matching standalone SQLCMD deployment under `database`. Do not place either external-system secret in the package or in `appsettings.json`.

The same command also creates the directly downloadable
`dist\TechBenchV2-SQLServer2016-2.0.0-alpha.14.sql` and its checksum. After the
matching client publisher has created GitHub release `v2.0.0-alpha.14`, attach
all four server-side assets with:

```powershell
.\scripts\Publish-TechBenchServer.ps1 `
  -Version 2.0.0-alpha.14 `
  -Publish
```

The server publisher will not create a release or overwrite an asset. Publish
the client first with `Publish-TechBenchRelease.ps1 -Publish`, then publish the
server/SQL assets.

## Deploy the database first

Before installing the service or alpha.14 clients, stop the old V2 service/clients, have the DBA back up `TechBench`, review `database\README-Deploy.md`, and execute `database\Deploy-CSRI-Standalone.sql` in SSMS while connected to `CSRI-SQL` as a SQL Server sysadmin with **Query > SQLCMD Mode** enabled. The script creates or verifies schema version 7 and checks the service-only WHD/Sage permissions, ticket row-security policy, and restricted Admin preview boundary. Alpha.14 introduces no database migration; an already verified schema-version-7 database remains current. Stop if any verification reports a failure.

If you download the versioned standalone SQL asset instead of taking it from
the service ZIP, verify its sidecar before opening it in SSMS:

```powershell
$sql = '.\TechBenchV2-SQLServer2016-2.0.0-alpha.14.sql'
$expectedHash = ((Get-Content "$sql.sha256" -Raw) -split '\s+')[0]
$actualHash = (Get-FileHash $sql -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'TechBench SQL SHA-256 does not match.' }
```

## Prerequisites

- Run the installer from an elevated PowerShell session on the target server.
- Use the dedicated, least-privilege `CSRI\TechBench_Sync` AD domain account. The default SQL deployment maps that account directly to the service-only database role; no same-named AD group is required. Do not add it directly or through a nested group to `TechBench_Users`, `TechBench_Admins`, or any unrelated SQL role.
- For a gMSA, install the account on the host and grant that host permission to retrieve its password before installing the service. The installer calls `Test-ADServiceAccount` when the ActiveDirectory module is available.
- For an ordinary domain account, ensure the supplied account is allowed to log on as a service. The installer adds that right when local policy permits it; a domain GPO can override the local assignment.
- Review the package `appsettings.json`; it contains only the SQL Server name, database name, timeouts, worker paths, and worker tuning. TechBench Admins configure the WHD endpoint/service username/schedule and server Sage values in Server Manager, which stores the non-secret values in SQL Server.
- Confirm the service host trusts the SQL Server certificate used by `CSRI-SQL.CSRI.local`; production keeps `TrustServerCertificate` set to `false`.
- Install the supported 32-bit Sage ODBC driver on the service host. Use `%windir%\SysWOW64\odbcad32.exe` to create a **System DSN**; a User DSN or mapped drive will not be visible reliably to the Windows service.
- Give `CSRI\TechBench_Sync` read access to the Sage data location and any other file/share rights required by the Sage ODBC driver. Use UNC paths rather than mapped drive letters.

## Install

For a normal domain account, the installer opens a credential dialog for the
Windows service account password. The password is hidden initially. Select
**Show password while I verify it** to reveal what you typed, then clear the
check box to hide it again before continuing. Revealing it affects only the
on-screen password field; the installer does not write it to the command line,
a file, PowerShell output, or a log. Clipboard shortcuts are disabled in that
field to reduce accidental disclosure. Avoid revealing it while anyone else
can see or record the server screen. `-WhatIf` reports the proposed installation
without prompting for any credential.

If Windows cannot open the dialog (for example, on Server Core or through a
non-GUI remote session), the installer falls back to PowerShell's standard
protected, masked credential prompt. For unattended installation, obtain a
`PSCredential` from the organization's approved secret-management process and
pass that object with `-Credential`; never put the password itself in a command
line, script, environment variable, or response file.

Install interactively with:

```powershell
$package = '.\TechBenchSyncService-2.0.0-alpha.14-win-x64.zip'
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

Before it changes the Windows service, the elevated installer verifies every extracted file against `package-manifest.json`, copies only those verified files into an Administrators/SYSTEM-only staging directory, and verifies them again there. It rejects an incomplete, altered, wrong-version, or wrong-architecture package.

The predictable alpha.14 server download is
`https://github.com/Serlynth/TechBenchV2-Releases/releases/download/v2.0.0-alpha.14/TechBenchSyncService-2.0.0-alpha.14-win-x64.zip`.
Download its `.sha256` sidecar by appending `.sha256` to that URL. The standalone
SQL asset follows the same pattern with filename
`TechBenchV2-SQLServer2016-2.0.0-alpha.14.sql`.

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

The installer copies binaries to `%ProgramFiles%\CSRI\TechBench Sync Service`, creates `TechBenchWhdSync`, and preserves an existing `appsettings.json` unless `-ReplaceConfiguration` is supplied. It creates `%ProgramData%\CSRI\TechBench Sync Service` with an explicit ACL: SYSTEM and Administrators have Full Control, while the service identity has read-and-execute access only. Elevated credential helpers perform writes and rotations. It also grants the service identity read-and-execute access to the install directory. Custom install and data directories must remain dedicated child folders under `%ProgramFiles%\CSRI` and `%ProgramData%\CSRI`; the installer rejects root or reparse-point paths before changing permissions.

The service is set to automatic delayed start, is given an SCM description, and restarts after its first three failures (after 60 seconds, 60 seconds, and 5 minutes; the failure counter resets after one day). Start is skipped when `-SkipStart` is specified or when no protected WHD credential has been provisioned yet. Sage can be configured later without preventing WHD synchronization from running.

## Use TechBench Server Manager

Installation places the self-contained GUI under `%ProgramFiles%\CSRI\TechBench Server Manager` and adds **TechBench Server Manager** under the Start Menu's **CSRI** folder. The shortcut targets `TechBench.ServerManager.exe` directly; its application manifest requests administrator access. Normal launch and runtime do not invoke PowerShell or VBScript. The Manager provides:

- current service status, installed version, and Windows service identity;
- Start, Stop, Restart, and Refresh controls;
- an Install/Apply Password action for the first installation or for rotating the password of the service's existing domain identity;
- protected WHD and Sage credential rotation with a separate Show control beside each secret;
- Admin-only shared WHD endpoint/authentication/username/schedule configuration, server Sage DSN/username/activity-item configuration, synchronization health and manual triggers, large-removal confirmation, and AD-user-to-WHD-technician mappings; and
- Check for Updates and Download & Install actions for the server service.

Minimizing or closing Server Manager hides it in the Windows notification area and clears every typed password or secret field. Double-click the tray icon or select **Open** to restore and activate it. Select **Exit** from the tray menu to terminate it. Only one Manager instance may run; a second launch directs the operator to the existing tray icon.

### Replace an alpha.9 through alpha.13 Manager

Download and extract the alpha.14 service ZIP once, close the old script-based Manager, open **Windows PowerShell as Administrator** in the extracted folder, and run:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Install-TechBenchServerManager.ps1
```

The deployment script verifies every compiled Manager file against `package-manifest.json`, installs it separately under Program Files, creates a direct EXE Start Menu shortcut, and opens it. It does not change the Windows service identity or either protected secret. In the compiled Manager, verify **SQL Server connection**, then select **Check for updates** and **Download & Install** to update the service. Future Manager launches and routine updates use compiled code.

The service-account password and WHD/Sage secrets are never placed in command-line arguments, configuration, output, or logs. They exist briefly in the visible form field when entered, are converted to `SecureString`, and are cleared immediately after use. Server Manager uses the elevated operator's Windows identity and Admin-only stored procedures to manage the non-secret WHD/Sage synchronization configuration in SQL Server. It never stores external-system secrets in SQL.

Routine service updates do not request the service-account password and do not recreate the Windows service. Server Manager downloads the exact versioned ZIP and SHA-256 sidecar from the public release repository, rejects unexpected URLs and unsafe archive paths, verifies the outer hash and every file in `package-manifest.json`, and verifies the required database schema through `tb_app.GetCurrentUserContext` using the current Windows identity. It blocks the update if SQL cannot be verified or the schema does not match; a DBA must apply the matching SQL installer first.

After verification, the compiled update helper stops the service, stages the new payload, preserves the exact installed `appsettings.json` and `%ProgramData%` secrets, and swaps the service and Manager directories. It recreates the direct EXE shortcut and restarts the service under its existing Windows identity. If installation or restart fails, it restores both previous directories and attempts to return the service to its prior working payload.

The Windows service credential is deliberately separate from routine updates. Use the PowerShell service installer shown above for a first installation; it creates the Windows service and installs the compiled Manager. For an existing service, the compiled Manager can apply the password for the displayed domain account without placing it on a command line. A gMSA ending in `$` may use a blank password. Use a controlled service reinstall for a deliberate identity migration.

## Store or rotate the WHD credential

The WHD API key, token, or password is prompted as a `SecureString` and passed to the executable over redirected standard input. It is never added to a command line, configuration file, package, transcript, or log by these scripts.

```powershell
.\Set-TechBenchSyncCredential.ps1
```

The executable protects the credential with machine-scoped DPAPI and writes it to `%ProgramData%\CSRI\TechBench Sync Service\whd.secret`. The elevated helper owns credential writes; the service identity receives read access only. `Set-TechBenchSyncCredential.ps1` restarts the service by default; use `-NoRestart` as part of a controlled maintenance window. To configure it during installation, add `-ConfigureWhdCredential` to the install command.

## Configure Sage customer synchronization

Open TechBench Server Manager with a Windows account in `CSRI\TechBench_Admins`. On the **Sage 50** tab, enter the server's 32-bit Sage **System DSN**, organization-wide Sage ODBC username, and shared activity item ID, then save. These non-secret values are shared in SQL Server.

On the service host, provision the separate Sage ODBC password:

```powershell
.\Set-TechBenchSageSyncCredential.ps1
```

The script protects it with machine-scoped DPAPI in `%ProgramData%\CSRI\TechBench Sync Service\sage.secret` and restarts `TechBenchWhdSync` by default. The WHD and Sage credentials are independent; do not enter the Windows service-account password or WHD secret at this prompt.

Sage customer synchronization has no automatic schedule. A TechBench Admin selects **Sync now** on Server Manager's **Sage 50** tab; the request is stored in SQL Server, and the service claims it. SQL Server rejects empty, malformed, over-length, or duplicate-ID snapshots without changing customer data. If a snapshot would remove at least 10 and at least 25 percent of 20 or more existing Sage mappings, the failed request reports the exact counts and Server Manager enables **Confirm large removal**. That second action displays a warning and queues a new request bound to the rejected request. It is accepted only within one hour and only when the fresh read has exactly the same read, existing, and stale counts; otherwise no customer data changes and the Admin must review the new proposal. WHD synchronization continues automatically every five minutes and may also be requested immediately from Server Manager.

## Verify and remove

```powershell
Get-Service TechBenchWhdSync
sc.exe qc TechBenchWhdSync
sc.exe qfailure TechBenchWhdSync

.\Uninstall-TechBenchSyncService.ps1
```

Exit Server Manager from its notification-area menu before uninstalling. The uninstaller refuses to proceed while the Manager is running or while an interrupted update journal still requires Manager recovery, so it cannot discard rollback state and leave an orphaned service payload. Uninstall removes the service binaries, Server Manager files and Start Menu shortcut, and the separate Manager update-state directory under `%ProgramData%`. It keeps the protected service-data directory by default so an upgrade does not discard either credential. To remove both service credentials permanently, run the uninstall script with `-RemoveCredential`. `-KeepBinaries` also preserves the Manager update state and emits a warning; omit that switch before a clean reinstall. Credential removal is irreversible.
