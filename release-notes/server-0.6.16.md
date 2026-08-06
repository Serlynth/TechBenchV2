# TechBench Server 0.6.16

- Adds the schema-15-compatible SQL procedure update required by Stable client 0.7.14 for explicit TechBench-only deletion of WHD-posted, Sage-unposted work entries.
- Preserves the permanent Sage-posted lock, row-version ownership checks, audit event, and active posting-attempt/lease guards.
- Removes the obsolete dependency on a prior WHD “note missing” synchronization error; the client now presents the explicit local-only confirmation instead.
- Does not call, edit, or delete anything in WHD and does not reintroduce WHD note synchronization.
- Includes the complete standalone SQL Server 2016 deployment, checksum, Sync Service package, Server Manager, and one-click server installer.

Run the standalone SQL deployment against the shared TechBench database before using the new delete action in client 0.7.14. The schema version remains 15.
