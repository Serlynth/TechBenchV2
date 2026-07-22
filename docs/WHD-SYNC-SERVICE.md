# TechBench Sync Service

`TechBench.SyncService` is the server-side worker that performs organization-wide Web Help Desk (WHD) synchronization, manually requested Sage customer synchronization, and the daily encrypted Credentials workbook import. Install it on a domain-joined Windows Server that can reach TechBench SQL Server, the WHD endpoint, Sage company data, and the Credentials file share.

## Build the service package

From the repository root, create a self-contained `win-x64` service package with its isolated self-contained `win-x86` Sage ODBC worker:

```powershell
.\scripts\Publish-TechBenchServer.ps1 -Version 0.5.18
```

The publisher creates the directly runnable `dist\TechBenchServerSetup.exe` and its SHA-256 sidecar. The setup executable embeds the complete verified `TechBenchSyncService-0.5.18-win-x64.zip` payload, including the x64 service, x86 Sage worker, compiled Server Manager, configuration template, runbook, credential helpers, and matching SQLCMD deployment. Do not place any external-system secret in the package or in `appsettings.json`.

The same command also creates the directly downloadable
`dist\TechBenchV2-SQLServer2016-0.5.18.sql` and its checksum. After the
matching client publisher has created GitHub release `v0.5.18`, attach
the installer, service ZIP, SQL file, and their checksums with:

```powershell
.\scripts\Publish-TechBenchServer.ps1 `
  -Version 0.5.18 `
  -Publish
```

The server publisher will not create a release or overwrite an asset. Publish
the client first with `Publish-TechBenchRelease.ps1 -Publish`, then publish the
server/SQL assets.

## Deploy the database first

Before installing 0.5.18, stop the old V2 clients and sync service, have the DBA back up `TechBench`, and apply schema version 8. Review `database\README-Deploy.md` and execute `database\Deploy-CSRI-Standalone.sql` in SSMS while connected to `CSRI-SQL` as a SQL Server sysadmin with **Query > SQLCMD Mode** enabled. Stop if any verification reports a failure. If the Results grid reports a newly created database-master-key recovery password, save it in the protected administrative password vault.

If you download the versioned standalone SQL asset instead of taking it from
the service ZIP, verify its sidecar before opening it in SSMS:

```powershell
$sql = '.\TechBenchV2-SQLServer2016-0.5.18.sql'
$expectedHash = ((Get-Content "$sql.sha256" -Raw) -split '\s+')[0]
$actualHash = (Get-FileHash $sql -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'TechBench SQL SHA-256 does not match.' }
```

## Prerequisites

- Run `TechBenchServerSetup.exe` on the target server and approve its Windows administrator prompt.
- Use the dedicated, least-privilege `CSRI\TechBench_Sync` AD domain account. The default SQL deployment maps that account directly to the service-only database role; no same-named AD group is required. Do not add it directly or through a nested group to `TechBench_Users`, `TechBench_Admins`, or any unrelated SQL role.
- For a gMSA, install the account on the host and grant that host permission to retrieve its password before installing the service. The installer calls `Test-ADServiceAccount` when the ActiveDirectory module is available.
- For an ordinary domain account, ensure the supplied account is allowed to log on as a service. The installer adds that right when local policy permits it; a domain GPO can override the local assignment.
- Review the package `appsettings.json`; it contains only the SQL Server name, database name, timeouts, worker paths, and worker tuning. TechBench Admins configure the WHD endpoint/service username/schedule and server Sage values in Server Manager, which stores the non-secret values in SQL Server.
- Confirm the service host trusts the SQL Server certificate used by `CSRI-SQL.CSRI.local`; production keeps `TrustServerCertificate` set to `false`.
- Install the supported 32-bit Sage ODBC driver on the service host. Use `%windir%\SysWOW64\odbcad32.exe` to create a **System DSN**; a User DSN or mapped drive will not be visible reliably to the Windows service.
- Give `CSRI\TechBench_Sync` read access to the Sage data location and any other file/share rights required by the Sage ODBC driver. Use UNC paths rather than mapped drive letters.
- Give the TechBench sync-service identity read access at both the share and NTFS levels to the configured Credentials workbook.

## Install or update

Download and run the one-click installer:

`https://github.com/Serlynth/TechBenchV2-Releases/releases/download/v0.5.18/TechBenchServerSetup.exe`

Windows requests administrator approval. For a new installation, leave the service account as `CSRI\TechBench_Sync`, select **Install**, and enter that account's password in the secure dialog. The password can be revealed temporarily for verification and is never written to the command line, configuration, package, output, or logs. After installation, Server Manager opens so the WHD, Sage, and Credentials secrets and shared configuration can be entered.

For an existing installation, select **Update / Repair**. Setup closes Server Manager, verifies its embedded package and every manifest hash, stops the service, preserves the Windows service identity, SQL configuration, and machine-protected WHD/Sage/Credentials secrets, replaces the service and Manager binaries, restores inherited read-and-execute access for the installed service identity, repairs the Start Menu shortcut, restarts the service, and opens the Manager. A same-schema update does not require the interactive setup operator to have SQL access. A release that requires a different schema remains blocked until the DBA applies the matching SQL deployment.

The setup EXE and its `.sha256` sidecar are published together. The versioned ZIP remains available for controlled advanced or unattended deployment, but normal installation and repair do not require extracting it, changing execution policy, or typing PowerShell commands. The standalone SQL asset is `TechBenchV2-SQLServer2016-0.5.18.sql`.

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
- protected WHD, Sage, and Credentials workbook credential rotation with a separate Show control beside each secret;
- Admin-only shared WHD endpoint/authentication/username/schedule configuration, server Sage DSN/username configuration, Credentials path/daily schedule, synchronization health and manual triggers, large-removal confirmation, and AD-user-to-WHD-technician mappings; and
- Check for Updates and Download & Install actions for the server service.

Minimizing or closing Server Manager hides it in the Windows notification area and clears every typed password or secret field. Double-click the tray icon or select **Open** to restore and activate it. Select **Exit** from the tray menu to terminate it. Only one Manager instance may run; a second launch directs the operator to the existing tray icon.

### Replace an earlier V2 Manager

Run the current `TechBenchServerSetup.exe` and select **Update / Repair**. This is the supported transition from every earlier script-based or compiled V2 Manager. It does not change the Windows service identity, SQL settings, or either protected secret.

The service-account password and WHD/Sage/Credentials source secrets are never placed in command-line arguments, configuration, output, or logs. They exist briefly in the visible form field when entered and are cleared immediately after use. Server Manager uses the elevated operator's Windows identity and Admin-only stored procedures to manage non-secret synchronization configuration in SQL Server. Source-system secrets remain machine-protected outside SQL.

Routine service updates do not request the service-account password and do not recreate the Windows service. Server Manager downloads the exact versioned ZIP and SHA-256 sidecar from the public release repository, rejects unexpected URLs and unsafe archive paths, and verifies the outer hash and every file in `package-manifest.json`. The one-click setup can update an existing installation without interactive SQL access when the installed and target packages declare the same required schema. A schema change still requires successful database verification after the matching DBA deployment.

After verification, the compiled update helper stops the service, stages the new payload, preserves the exact installed `appsettings.json` and `%ProgramData%` secrets, and swaps the service and Manager directories. It recreates the direct EXE shortcut and restarts the service under its existing Windows identity. If installation or restart fails, it restores both previous directories and attempts to return the service to its prior working payload.

The Windows service credential is deliberately separate from routine updates. The one-click setup prompts for it only during first installation. For an existing service, the compiled Manager can apply a rotated password for the displayed domain account without placing it on a command line. A gMSA ending in `$` may use a blank password. Use the advanced controlled deployment workflow for a deliberate identity migration.

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

## Configure Credentials synchronization

Open Server Manager's **Credentials** tab. Enter the workbook UNC path, leave daily synchronization enabled at **4:00 AM**, enter the workbook open password, and select **Save / Rotate**. Select **Save settings**, then **Sync now** for the initial import. The path is an Admin-only server setting and is not embedded in the public package or returned to ordinary clients. The service opens the file read-only with sharing enabled, so employees can keep it open for editing; a file caught mid-save is rejected and retried later without changing the current SQL snapshot. The first visible worksheet must use the documented headers exactly. All imported credential values are encrypted in SQL Server.

## Verify and remove

```powershell
Get-Service TechBenchWhdSync
sc.exe qc TechBenchWhdSync
sc.exe qfailure TechBenchWhdSync

.\Uninstall-TechBenchSyncService.ps1
```

Exit Server Manager from its notification-area menu before uninstalling. The uninstaller refuses to proceed while the Manager is running or while an interrupted update journal still requires Manager recovery, so it cannot discard rollback state and leave an orphaned service payload. Uninstall removes the service binaries, Server Manager files and Start Menu shortcut, and the separate Manager update-state directory under `%ProgramData%`. It keeps the protected service-data directory by default so an upgrade does not discard either credential. To remove both service credentials permanently, run the uninstall script with `-RemoveCredential`. `-KeepBinaries` also preserves the Manager update state and emits a warning; omit that switch before a clean reinstall. Credential removal is irreversible.
