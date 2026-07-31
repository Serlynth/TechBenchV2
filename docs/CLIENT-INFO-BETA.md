# Client Info beta test runbook

## Compatibility boundary

The beta extends the existing TechBench SQL Server database. It does not create
a database per client and does not replace the stable database.

The extension deliberately records schema version 15. Stable TechBench 0.6.1
clients, the stable sync service, and Server Manager therefore continue to
accept the same database. New tables and stored procedures are additive and are
ignored by stable clients.

Stable and beta use the same TechBench installer identity with separate `v2`
and `client-info-beta` Velopack channels, matching the earlier Inventory Beta
workflow. Stable keeps the FireDrill pane; only a build compiled for the beta
channel opens canonical Client Info. The update-channel selector can move a
workstation in either direction without a manual reinstall.

## Initial beta installation

1. Back up the TechBench database and the database master key/certificates.
2. Download `TechBenchServerSetup.exe` from the current Client Info Beta GitHub
   prerelease. Alternatively, a DBA can review and run the matching standalone
   `TechBenchV2-SQLServer2016-*.sql` bundle; do not apply both deployment paths.
3. On the TechBench server, run `TechBenchServerSetup.exe` as an administrator.
   It applies the additive SQL bundle and refreshes the server components. The
   required schema remains 15.
4. Let the existing stable client update to 0.6.2. Under **Settings**, select
   **Use Client Info Beta update channel**, then choose **Check for Updates**
   and install the offered beta. The same selector is available on the database
   connection window.
5. Connect to the existing TechBench SQL Server database with the normal
   Windows Integrated Authentication flow.
6. Confirm stable TechBench 0.6.1 can still connect and its current Client Info
   view still reads the server-owned FireDrill cache.

## Migrating one client

1. In the beta, open **Client Info** and double-click the client.
2. Confirm the internal client ID in the header.
3. On **Migration**, choose **Create workbook**.
4. Prune and normalize the client's source workbook into the generated tabs.
   Keep the internal client ID and workbook ID unchanged. Put passwords only in
   the credential secret column.
5. Mark reviewed rows `Verified` or `AcceptedUnverified`.
6. Choose **Stage workbook**. Staging is idempotent and does not change
   canonical Client Info.
7. Review validation issues and the FireDrill match/mismatch counts. Correct the
   workbook and stage a new revision when needed.
8. An admin approves the reviewed batch, then explicitly promotes it.
9. Re-open the client, verify each tab, reveal/copy a sampling of secrets, and
   confirm the access events appear in the audit trail.
10. Leave FireDrill sync enabled throughout beta testing.

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
