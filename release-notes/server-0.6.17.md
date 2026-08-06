# TechBench Server 0.6.17

- Repairs the existing schema-15 Client Information staging constraint so `ResourceField` records are accepted during workbook imports.
- Applies the repair to existing TechBench databases as well as new deployments; no database rebuild or beta-only server package is required.
- Verifies the repaired constraint during deployment so a drifted or incomplete SQL update fails before a technician retries an import.
- Includes the complete standalone SQL Server 2016 deployment, checksum, Sync Service package, Server Manager, and one-click server installer.

Run `TechBenchServerSetup.exe` as Administrator on the shared TechBench server, or run the included standalone SQL deployment against the shared TechBench database. The schema version remains 15.
