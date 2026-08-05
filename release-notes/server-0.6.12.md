# TechBench Server 0.6.12

- Implements WatchGuard's published push-status contract exactly: HTTP 202 means approval is pending, while HTTP 200 returns `pushResult: AUTHORIZED` after approval.
- Continues polling pending pushes for up to 90 seconds and completes TechBench login immediately after an authorized result.
- Leaves Client Info beta 0.6.6-beta.2, Stable 0.6.5, FireDrill, WHD, Sage, and schema-version-15 database behavior unchanged.

If the 0.6.9 SQL script already completed successfully, no SQL rerun is required for this Sync Service hotfix. New installations can use the 0.6.12 SQL asset included with this release.
