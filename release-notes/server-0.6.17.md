# TechBench Server 0.6.17

- Repairs the existing schema-15 Client Information staging constraint so `ResourceField` records are accepted during workbook imports.
- Applies the repair to existing TechBench databases as well as new deployments; no database rebuild or beta-only server package is required.
- Verifies the repaired constraint during deployment so a drifted or incomplete SQL update fails before a technician retries an import.
- Includes the complete standalone SQL Server 2016 deployment, checksum, Sync Service package, Server Manager, and one-click server installer.

Apply `TechBenchV2-SQLServer2016-0.6.17.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled before retrying the workbook import. `TechBenchServerSetup.exe` updates the Server Manager and Sync Service but does not execute the database deployment. The schema version remains 15.
