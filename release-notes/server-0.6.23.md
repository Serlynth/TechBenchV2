# TechBench Server 0.6.23

This stable SQL release prevents optional FireDrill password comparison from blocking Client Information workbook imports. FireDrill values that cannot be decrypted are excluded from comparison, and an unavailable workbook-secret comparison is recorded as `NotComparable` with a non-blocking warning.

Apply `TechBenchV2-SQLServer2016-0.6.23.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
