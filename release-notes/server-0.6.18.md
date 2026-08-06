# TechBench Server 0.6.18

This stable server/SQL release fixes Client Information workbook imports that contain passwords. Authorized technicians can now stage encrypted workbook secrets without receiving direct permission to the database encryption key.

The same protected encryption boundary is used when a password is added or changed manually in Client Information. Caller authorization and audit identity still run as the signed-in Windows user; only the cryptographic operation runs in the database-owner context.

Apply `TechBenchV2-SQLServer2016-0.6.18.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled, then retry the same completed workbook. The workbook does not need to be recreated. The schema version remains 15.
