# TechBench Server 0.6.7

- Automatically synchronizes AuthPoint identities from each authorized technician's Active Directory `mail` attribute.
- Falls back to the AD user principal name only when `mail` is blank.
- Replaces manual AuthPoint mapping entry with a read-only directory identity/status view and refresh action.
- Expands the AuthPoint server configuration panel to use the available window width.
- Keeps the Client Info beta, Stable client, FireDrill, and schema-version-15 database compatible.
