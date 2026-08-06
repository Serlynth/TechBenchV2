# TechBench Server 0.6.22

This stable SQL release fixes reimporting an identical Client Information workbook after its earlier review was discarded, superseded, or failed. Closed batches remain available for audit history, while the re-upload starts a fresh active review instead of trying to modify the closed batch.

Apply `TechBenchV2-SQLServer2016-0.6.22.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
