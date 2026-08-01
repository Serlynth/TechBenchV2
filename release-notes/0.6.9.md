# TechBench Server 0.6.9

- Adds server-enforced WatchGuard AuthPoint authentication when the Client Info beta opens, replacing repeated Reveal and Copy prompts.
- Adds SID- and client-instance-bound login sessions with random in-memory tokens, expiration, replay protection, early revocation, rate limiting, and secret-free audit events.
- Adds global-all-users and per-user **Require at login** rollout controls to the one shared Server Manager.
- Retains automatic Active Directory email-to-AuthPoint identity synchronization and server-local DPAPI protection for WatchGuard API credentials.
- Keeps Stable 0.6.5, FireDrill, WHD, Sage, and the shared schema-version-15 database compatible.

Run `TechBenchV2-SQLServer2016-0.6.9.sql` in SQLCMD mode before installing or restarting the 0.6.9 server package. This additive script is required even when the database already reports schema version 15.
