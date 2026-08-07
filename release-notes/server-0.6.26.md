# TechBench Server 0.6.26

This stable server release makes the TechBench internal client ID canonical across TechBench, Web Help Desk, and Sage. Existing Live TechBench clients absorb confidently matched WHD and Sage identities without losing their name, active state, Client Information profile, equipment, tickets, work entries, or related records.

When a new WHD client and Sage customer confidently match and no TechBench profile exists, the surviving shared client is promoted to a Live, Unverified Client Information record automatically. Ambiguous matches remain available for administrator review, and row-version, transaction, identity-conflict, and audit safeguards keep retries idempotent and prevent partial merges.

Existing WHD/Sage pairs created before this release are promoted to the same Live, Unverified canonical state during the SQL update.

Creating a manual client can also adopt one exact-name WHD- or Sage-only source record atomically, avoiding a duplicate while leaving ambiguous duplicate names for Client Match review.

The update also preserves canonical TechBench clients when a source is renamed or later becomes inactive and supports numbered WHD location families under one canonical client.

WHD removals are fail-safe: an empty snapshot or any snapshot that would remove at least 25 percent of current WHD locations must be repeated with the same exact missing-location set before links are retired. Retired WHD identities are retained in reconciliation history so a returning location reconnects to the same TechBench client instead of creating a duplicate.

Clients with a workbook migration or another Client Information cutover in progress are never consumed as match sources. Finish or discard that migration before linking the client.

Apply `TechBenchV2-SQLServer2016-0.6.26.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
