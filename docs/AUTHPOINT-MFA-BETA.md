# WatchGuard AuthPoint MFA for the Client Info beta

This release adds server-enforced AuthPoint step-up authentication to canonical Client Info credential **Reveal** and **Copy**. Windows Integrated Authentication remains the first factor. The Stable 0.6.5 client, FireDrill, WHD sync, and Sage sync continue to use the same TechBench database without an AuthPoint prompt.

AuthPoint starts disabled. Do not enable it until the SQL package, current server package, WatchGuard resource, policy, protected API credentials, and user mappings are all in place.

## Components and trust boundary

- The beta desktop requests a short-lived challenge but never receives WatchGuard API credentials.
- SQL Server binds each challenge and authorization to the caller's Windows SID, action, and exact secret.
- The TechBench Sync Service is the only component that contacts WatchGuard Cloud.
- The WatchGuard API access password and API key are protected with LocalMachine DPAPI in the server data directory. They are not stored in SQL or `appsettings.json`.
- A successful authorization expires after 60 seconds and is consumed by one Reveal or Copy operation. Reuse, the wrong Windows SID, the wrong action, or the wrong secret fails closed.
- Provider denials, timeouts, configuration errors, and outages fail closed after AuthPoint is enabled.

## 1. Prepare WatchGuard Cloud

Use the WatchGuard Cloud tenant that already owns CSRI's AuthPoint licenses.

1. Enable WatchGuard Cloud API access and record the API access ID, API access password, and API key.
2. In AuthPoint, create a **RESTful API Client** resource for TechBench.
3. Record the AuthPoint account ID and numeric REST resource ID.
4. Create an authentication policy that applies to this resource and the intended technician group.
5. Allow push authentication. Do not require the AuthPoint password, and do not permit a Forgot Token bypass for this resource.
6. Confirm each test technician is enrolled with the same email address stored in Active Directory and can receive AuthPoint pushes.
7. Identify the tenant's regional API URL, such as `https://api.usa.cloud.watchguard.com`. TechBench accepts only HTTPS `api.<region>.cloud.watchguard.com` hosts with no custom port or URL path.

## 2. Back up and install the additive SQL package

1. Take the normal TechBench database backup and confirm the existing TechBench encryption certificate/key backup is current.
2. Open the release's `TechBenchV2-SQLServer2016-0.6.6.sql` in SSMS on `CSRI-SQL`.
3. Enable **Query > SQLCMD Mode**.
4. Run the entire script as a SQL sysadmin and verify the final success message.

The database remains schema version 15. The deployment is additive and idempotent. It adds AuthPoint challenge, authorization, mapping, audit, and service procedures without changing FireDrill procedures or requiring a separate database.

## 3. Install the current shared server package

Run `TechBenchServerSetup.exe` from the TechBench Server 0.6.8 release on the TechBench server. This updates the one shared Server Manager and Sync Service; there is no beta Server Manager.

After setup:

1. Open **TechBench Server Manager**.
2. Confirm the Sync Service is running under the existing Windows service identity.
3. Confirm Server Manager reports version 0.6.8.
4. Leave AuthPoint disabled while configuring it.

The first AuthPoint-capable server package is a manual bootstrap update. Server 0.6.8 also corrects release discovery when the repository contains more than 100 older client releases. Subsequent server updates use independent `server-v...` releases so server updates no longer consume or depend on Stable/Beta client version tags.

## 4. Configure AuthPoint in Server Manager

1. Open **AuthPoint (Beta) > Server Configuration**.
2. Enter the regional API base URL, AuthPoint account ID, numeric REST resource ID, and WatchGuard API access ID.
3. Enter both the API access password and API key. Save them together. The values are DPAPI-protected on this server.
4. Keep **Require AuthPoint for Client Info beta secret reveal and copy** unchecked and save.
5. Open **AuthPoint (Beta) > Directory Identities** and select **Refresh from Active Directory**.
6. Confirm each authorized TechBench user shows **Ready**. TechBench uses the AD `mail` attribute automatically and falls back to the AD user principal name only when `mail` is blank; there is no routine manual mapping step.
7. Correct missing or incorrect identities in Active Directory, refresh again, and confirm the displayed identity matches the user's AuthPoint identity.
8. Return to Server Configuration, enable the requirement, and save.
9. Restart the Sync Service if Server Manager indicates it is not running the current package.

## 5. Test with the beta client

1. Install `TechBenchClientInfoBetaSetup.exe` from client-info beta 0.6.6-beta.1.
2. Confirm Settings shows version 0.6.6-beta.1 and the Client Info Beta update channel.
3. Open a client's separate canonical Client Info window.
4. Select a test credential and choose Reveal. Approve the AuthPoint push; the secret should appear only after approval.
5. Repeat with Copy and confirm a second one-time authorization is required.
6. Deny one request and let another expire. Both operations must remain blocked.
7. Confirm the Stable 0.6.5 client still opens and operates normally against the same database, and confirm FireDrill still works unchanged.

## Disable or roll back

For an AuthPoint outage or beta rollback, clear the AuthPoint requirement in Server Manager and save. This immediately stops requiring new AuthPoint authorizations while preserving mappings and audit history. Do not drop the additive tables or encryption objects. Stable clients and FireDrill do not need to be rolled back.

If the server package itself must be rolled back, use the Server Manager's verified package rollback/update path and leave `AuthPoint.Enabled` false until the current AuthPoint-capable Sync Service is restored.

## Emergency break-glass

Normal operations must not use break-glass. Membership in `tb_role_mfa_break_glass` is intentionally empty by default. A caller must also be a TechBench Admin, a second administrator must approve a different target user, the reason must be specific, and a grant lasts at most ten minutes for one exact secret and action.

The audited procedures are:

- `tb_app.AdminIssueMfaBreakGlassGrant`
- `tb_app.AdminRevokeMfaBreakGlassGrant`

Grant role membership only to a small emergency administrator group through the normal DBA change process. Remove it after the incident and review `MfaBreakGlassIssued`, `ClientSecretBreakGlassUsed`, and `MfaBreakGlassRevoked` audit events.

## Operational checks

- Do not paste API credentials into tickets, logs, SQL, workbooks, or command-line arguments.
- Rotate the API access password and API key together in Server Manager.
- Keep Windows Integrated Authentication and encrypted TDS enabled.
- Monitor MFA audit events and repeated rate-limit, denial, timeout, mapping, or provider-error results.
- Back up the SQL certificate and key after the normal database encryption-key procedure; AuthPoint does not replace existing SQL field encryption.
