# TechBench Server 0.6.28

This stable server release corrects the Client Information lifecycle introduced in Server 0.6.26. Matching WHD and Sage source records now reconciles their identities under one TechBench client without making that client's Client Information profile Live or marking its cutover Complete.

Client Information becomes Live only when an Admin directly creates a manual client or when an approved client workbook is promoted. Linking WHD or Sage sources to an already-Live TechBench client continues to preserve that canonical client and its internal ID.

The update safely repairs Server 0.6.26 and 0.6.27 databases. Profiles whose audit history proves they were activated only by source matching are returned to the appropriate Not Started, Staging, or Ready lifecycle state. Completed workbook imports and directly created manual clients remain Live. Existing profile fields, locations, users, resources, credentials, secrets, attachments, and other client data are retained; the correction changes lifecycle state only and records an audit event. An Admin may later use **+ New client** to explicitly promote a corrected exact-name profile when no workbook migration is active, without replacing its existing data.

The update is transactional and fully rerunnable. Apply `TechBenchV2-SQLServer2016-0.6.28.sql` to the shared TechBench database in SSMS with SQLCMD Mode enabled. The schema version remains 15.
