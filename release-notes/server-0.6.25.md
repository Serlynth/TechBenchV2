# TechBench Server 0.6.25

This stable server release adds the schema-15-compatible, admin-only operation used to create a brand-new manual client directly in Client Information. The operation atomically creates the shared client, its Live/Unverified profile, its completed lifecycle row, and its audit event so a partial client cannot be left behind.

It also advertises a dedicated client capability so older desktop clients remain compatible and TechBench 0.7.31 can disable **+ New client** until this update is installed.

Apply `TechBenchV2-SQLServer2016-0.6.25.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
