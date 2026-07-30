# TechBench V2 updates

TechBench V2 keeps the same Velopack update experience as V1, but uses a
completely separate GitHub Releases feed.

V2 must never use the V1 pack ID, installer name, or GitHub release feed. The V2 release script now uses:

- Pack ID: `CSRI.TechBenchV2`
- Main executable: `TechBenchV2.exe`
- Installer: `TechBenchV2Setup.exe`
- Title: `TechBench V2`
- Release feed: `https://github.com/Serlynth/TechBenchV2-Releases`

Every published version also exposes predictable direct-download assets:

- `TechBenchV2Setup.exe` and `TechBenchV2Setup.exe.sha256`
- `TechBenchSyncService-<version>-win-x64.zip` and its `.sha256` sidecar
- `TechBenchV2-SQLServer2016-<version>.sql` and its `.sha256` sidecar

Local packages can be built without a repository:

```powershell
.\scripts\Publish-TechBenchRelease.ps1 -Version <version> -AllowDirty
.\scripts\Publish-TechBenchServer.ps1 -Version <version> -AllowDirty
```

Publishing targets the V2-only release repository by default:

```powershell
.\scripts\Publish-TechBenchRelease.ps1 `
  -Version <version> `
  -Publish

.\scripts\Publish-TechBenchServer.ps1 `
  -Version <version> `
  -Publish
```

Run those commands in that order. The client publisher creates the GitHub
release and uploads the Velopack update artifacts plus the stable installer
name. The server publisher requires that exact non-draft `v<version>` release
to exist, then attaches the server ZIP and versioned standalone SQL file with
their checksums. It refuses to overwrite an existing asset; published versions
remain immutable.

For a release candidate, first build both packages locally without `-Publish`
and inspect/test the files under `dist`. After the source is committed, publish
the client release first and the server/SQL assets second. Do not manually
create the tag or GitHub release before the client publishing command.

For version `2.0.0-alpha.8`, the stable direct links are:

```text
https://github.com/Serlynth/TechBenchV2-Releases/releases/download/v2.0.0-alpha.8/TechBenchV2Setup.exe
https://github.com/Serlynth/TechBenchV2-Releases/releases/download/v2.0.0-alpha.8/TechBenchSyncService-2.0.0-alpha.8-win-x64.zip
https://github.com/Serlynth/TechBenchV2-Releases/releases/download/v2.0.0-alpha.8/TechBenchV2-SQLServer2016-2.0.0-alpha.8.sql
```

The V2 client includes prerelease updates while it is in the alpha/beta cycle.
The publishing script marks versions containing a prerelease suffix, such as
`2.0.0-alpha.1`, as GitHub prereleases automatically.
Before the first published installer:

1. Create the public `Serlynth/TechBenchV2-Releases` release-only repository.
2. Authenticate `gh` on the release workstation.
3. Build and test the client, server, and SQL assets locally.
4. Publish the client release, then attach the matching server and SQL assets.
5. Install V1 and V2 side by side.
6. Verify each client sees only its own release feed and installer identity.
