# TechBench Server 0.6.6

- Adds the server-enforced WatchGuard AuthPoint worker used by the Client Info beta.
- Adds AuthPoint configuration and Windows-user mapping pages to the one shared Server Manager.
- Protects the WatchGuard API access password and API key with LocalMachine DPAPI; secret values are not stored in SQL, configuration JSON, logs, or clients.
- Adds the schema-version-15 AuthPoint SQL extension with SID-bound one-time authorization, replay prevention, rate limiting, auditing, and two-person break-glass controls.
- Moves server publication to independent `server-v...` release tags, allowing Server Manager and Sync Service updates without changing Stable or Beta client channels.
- Leaves FireDrill and existing Stable 0.6.5 clients compatible with the same database.

Install the additive SQL script before enabling AuthPoint, then run `TechBenchServerSetup.exe`. Follow `README-AUTHPOINT-MFA.md` included in the server package.
