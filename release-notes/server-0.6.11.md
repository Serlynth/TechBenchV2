# TechBench Server 0.6.11

- Keeps checking a WatchGuard AuthPoint push transaction while approval is pending instead of treating the first pending response as a failure.
- Accepts approval for up to 90 seconds, while still failing closed on denial, expiration, provider errors, or service cancellation.
- Leaves Client Info beta 0.6.6-beta.2, Stable 0.6.5, FireDrill, WHD, Sage, and schema-version-15 database behavior unchanged.

If the 0.6.9 SQL script already completed successfully, no SQL rerun is required for this Sync Service hotfix. New installations can use the 0.6.11 SQL asset included with this release.
