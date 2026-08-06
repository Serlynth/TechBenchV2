# Client Info beta test runbook

> Historical runbook: Client Information graduated into Stable TechBench 0.7.0.
> The `client-info-beta` feed now exists only to migrate installed Beta clients
> onto the Stable update channel. Use the current Stable release and SQL package
> for new installations.

## Compatibility boundary

The beta extends the existing TechBench SQL Server database. It does not create
a database per client and does not replace the stable database.

The extension deliberately records schema version 15. Stable TechBench 0.6.1
clients, the stable sync service, and Server Manager therefore continue to
accept the same database. New tables and stored procedures are additive and are
ignored by stable clients. The custom resource-field editor and `TB-CI-6`
promotion support in beta.10 require reapplying the updated standalone SQL
deployment to refresh stored procedures and grants; the schema version remains
15.

Server Manager, the Sync Service, and SQL deployment stay on one stable release
line. The current stable server package supports both desktop channels and its
updater ignores desktop prereleases. There is no beta Server Manager setting.

Stable and beta use the same TechBench installer identity with separate `v2`
and `client-info-beta` Velopack channels, matching the earlier Inventory Beta
workflow. Stable and beta both keep the existing FireDrill pane and profile
behavior. Only a build compiled for the beta adds the separate **CLIENTS >
Client Information** and **CLIENTS > Workbook Imports** workspaces for
canonical SQL records. The update-channel selector can move a workstation in
either direction without a manual reinstall.

## Initial beta installation

1. Back up the TechBench database and the database master key/certificates.
2. Download the updated beta
   `database/sqlserver2016/Deploy-CSRI-Standalone.sql` from the
   `codex/client-info-beta` branch. A DBA reviews it and executes it in SSMS
   with **SQLCMD Mode** enabled. The script is idempotent and keeps the required
   schema at 15.
3. In the existing Stable Server Manager, check for and install version 0.6.4.
   Alternatively, run the Stable `TechBenchServerSetup.exe` as administrator.
   This refreshes Server Manager and the Sync Service; it does not silently
   execute the separate DBA-controlled SQL bundle.
4. Let the existing stable client update to 0.6.2. Under **Settings**, select
   **Use Client Info Beta update channel**, then choose **Check for Updates**
   and install the offered beta. The same selector is available on the database
   connection window.
5. Connect to the existing TechBench SQL Server database with the normal
   Windows Integrated Authentication flow.
6. Confirm stable TechBench 0.6.1 can still connect and its current Client Info
   view still reads the server-owned FireDrill cache.

## Migrating one client

1. In the beta, open **CLIENTS > Workbook Imports**. Search by client name or
   internal client ID, then select **Prepare Import** (or double-click the
   client row).
2. Confirm the internal client ID in the header and choose **Create Migration
   Workbook**.
3. Copy the useful, cleaned information from the client's current workbook into
   the matching tabs: Locations, Users, Equipment, Servers & Infrastructure,
   Connection & Internet, Wi-Fi, Applications & Cloud, Domains & Email, Backup,
   Security, Vendors & Services, Passwords, Other Info, and Needs Sorting. Leave
   tabs or fields that do not apply blank. Switches and network appliances
   belong under Servers & Infrastructure; wireless networks and access points
   belong under Wi-Fi. Backup and restore rows belong under Backup, while
   antivirus, EDR, MFA, and filtering rows belong under Security.
   Use the category-specific IP/network columns. For an unusual field, add an
   optional column whose heading starts with `Custom:` such as `Custom: Rack`.
   Custom-column values become editable resource fields in Client Information.
4. Do not change the internal client ID or reuse the workbook for another
   client. Enter the primary AD password beside its user and the primary login
   beside its system or service. Use Passwords for additional or standalone
   credentials. Completed workbooks contain plaintext secrets until import, so
   secure and remove the files according to policy.
5. Set each populated row's **Review Status** to **Verified** or **Keep as-is**
   after reviewing it. Use **Needs review** while investigating uncertain data,
   or **Do not import** for a row that should be excluded.
6. Choose **Import Completed Workbook**. Importing is idempotent and does not
   change Client Information yet.
7. Review validation issues and use **Check Against FireDrill** to investigate
   matches and mismatches. Correct the workbook and import a new revision when
   needed.
8. An authorized admin chooses **Approve Reviewed Workbook**, then explicitly
   chooses **Add to Client Information**.
9. Open **CLIENTS > Client Information**, select the same client, and verify or
   edit the imported records. Reveal/copy a sampling of secrets and confirm the
   access events appear in the audit trail.
10. Leave FireDrill sync enabled throughout beta testing.

The current workbook format is `TB-CI-8`. It intentionally hides technical row
keys, uses ordinary worksheet labels, separates Backup from Security, and
includes all current category-specific fields. Every resource sheet accepts
optional `Custom:` columns and provides Login Name, Username, and Password /
Secret columns that link the imported credential to that exact row. The Users
sheet provides AD Username and AD Password columns with the same behavior.
Workbooks generated by `TB-CI-7` through `TB-CI-1` remain importable.

In Client Information, select a technology record and choose **Custom Fields**
to add, edit, or delete unusual fields. Standard and custom fields appear as
columns in the appropriate category grid. Grid columns can be resized and
reordered during the session; the canonical data remains in SQL regardless of
the display order.

## Client attachments

Client Info beta can keep site photos, hardware pictures, diagrams, PDFs, and
ordinary office documents with the canonical client record. SQL stores the
attachment metadata, audit history, original filename, size, and SHA-256 hash;
the file itself stays in a server share so database backups do not become
unnecessarily large.

Before using attachments:

1. Install the current stable TechBench server package and apply its standalone
   SQL deployment in SSMS with **SQLCMD Mode** enabled. The attachment extension
   is additive and keeps the database schema version at 15.
2. In **Server Manager > Attachments**, choose a dedicated UNC folder below a
   share, for example
   `\\CSRI-SQL\TechBenchFiles\ClientAttachments`. Do not select the share root.
3. Set the maximum upload size and allowed extensions, then choose **Save
   settings**. Saving performs a create/read/delete test and reports current
   usage and free space.
4. Grant the normal TechBench Windows groups the file-share and NTFS access
   appropriate for their application permissions, and include the attachment
   root in the normal server backup plan.

TechBench creates `Client-<internal ID>\Photos` and
`Client-<internal ID>\Documents` automatically. Technicians do not organize
folders themselves. Client display-name changes do not affect attachment
paths. If duplicate clients are merged, SQL moves the metadata to the retained
internal client record while preserving the existing physical path so a file
operation cannot break the database transaction.

On the **Attachments** tab, files can be selected, dragged onto the page, or
pasted from the clipboard. Images have an in-app preview; other documents open
with their normal Windows application. Files can be copied, saved elsewhere,
categorized, captioned, archived, and restored. Archive is intentionally
non-destructive: it hides the record by default but retains both the file and
its audit history.

An attachment can optionally be linked to one active equipment record assigned
to the same client. Choose **Link equipment** from the attachment preview and
select the device; choose **Not linked** to remove the relationship. The file
does not move or get duplicated. Linked photos and documents appear beneath the
selected device on the client's Equipment page and in TechBench's regular
equipment details pane. The relationship uses the internal equipment ID, so
renaming the device or changing its asset tag does not break the link.

## Multiple editors

Edits are saved one small record at a time. Every update supplies the SQL
`rowversion` originally read. If another technician saved first, TechBench
rejects the stale write, reloads the current SQL value, and asks the user to
review and retry. Opening a second window for the same client in one process is
also prevented.

## Rollback

Client promotion is additive. If a promoted client needs correction, keep
stable 0.6.1 and FireDrill operational, correct the workbook or individual
canonical records in the beta, and re-verify. Do not drop the beta tables while
any beta client is running.

To stop beta testing on a workstation, clear **Use Client Info Beta update
channel**, choose **Check for Updates**, and install Stable. The canonical
tables remain dormant and stable clients ignore them.

## FireDrill retirement gate

FireDrill is not retired in this beta. Global retirement can occur only after:

- every active client is promoted and verified;
- FireDrill comparisons and secret sampling are complete;
- all workstations and server components are on a stable release that reads
  canonical Client Info;
- backup/restore of the client-secret certificate and key is tested;
- a rollback window and hypercare owner are assigned.

The later stable cutover removes the FireDrill workbook configuration, sync
reader, service schedule, source-shaped cache, repository naming, and UI only
after those gates pass. There is no permanent dual-entry mode.
