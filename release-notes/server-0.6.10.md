# TechBench Server 0.6.10

- Fixes Server Manager's AuthPoint Directory Identities refresh when SQL Server returns a nullable Boolean expression as integer `0` or `1`.
- Keeps the successfully saved WatchGuard configuration and protected API credentials unchanged.
- Leaves Client Info beta 0.6.6-beta.2, Stable 0.6.5, FireDrill, WHD, Sage, and schema-version-15 database behavior unchanged.

If the 0.6.9 SQL script already completed successfully, no SQL rerun is required for this Server Manager hotfix. New installations can use the 0.6.10 SQL asset included with this release.
