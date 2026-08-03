# TechBench Server 0.6.13

- Adds **Server Manager > Attachments** for configuring the shared client-file
  UNC root, maximum upload size, and allowed file extensions.
- Validates that storage is a dedicated folder beneath a share, blocks
  executable and script extensions, and performs a create/read/delete access
  test before settings are saved.
- Reports attachment storage usage and free space so the share can be monitored
  without manually browsing the server.
- Adds the schema-15-compatible Client Attachments metadata extension, secured
  stored procedures, optimistic concurrency, and upload/edit/archive/restore
  audit events.
- Stores only metadata and relative paths in SQL; files remain in the configured
  share and are automatically organized by immutable internal client ID.
- Reparents metadata safely during duplicate-client merges without moving files
  inside a SQL transaction.

Back up the TechBench database first, then apply
`TechBenchV2-SQLServer2016-0.6.13.sql` in SSMS with SQLCMD Mode enabled. The
database schema version remains 15. Configure a dedicated UNC subfolder in
Server Manager, grant the appropriate TechBench Windows groups share/NTFS
access, and include that folder in server backups before enabling uploads.
