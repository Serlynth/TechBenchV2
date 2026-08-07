# TechBench Server 0.6.24

This stable SQL release fixes FireDrill comparison for flexible credential fields. The comparison now uses the same `ClientKey|FieldKey` encryption authenticator as FireDrill storage and reports client-name matching separately from value availability.

Apply `TechBenchV2-SQLServer2016-0.6.24.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
