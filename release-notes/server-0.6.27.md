# TechBench Server 0.6.27

This stable server release fixes the 0.6.26 SQL deployment failure during legacy WHD/Sage client reconciliation. Deployment-time canonical-client promotions are now audited as the registered TechBench sync service actor instead of the Windows or SQL account running the installer, preserving the audit foreign key and allowing a partially completed 0.6.26 deployment to resume safely.

The update is fully rerunnable. Clients already promoted by an earlier attempt remain unchanged, while any unprocessed legacy matches continue from the same idempotent reconciliation step.

WHD external identifiers retain their existing 500-character contract. Wide persisted, temporary, and table-variable identifier keys now use SQL Server 2016-safe nonclustered indexes, removing the 900-byte clustered-key warnings without truncating identifiers.

Apply `TechBenchV2-SQLServer2016-0.6.27.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
