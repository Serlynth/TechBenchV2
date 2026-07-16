# TechBench V2 updates

TechBench V2 keeps the same Velopack update experience as V1, but uses a
completely separate GitHub Releases feed.

V2 must never use the V1 pack ID, installer name, or GitHub release feed. The V2 release script now uses:

- Pack ID: `CSRI.TechBenchV2`
- Main executable: `TechBenchV2.exe`
- Installer: `TechBenchV2Setup.exe`
- Title: `TechBench V2`
- Release feed: `https://github.com/Serlynth/TechBenchV2-Releases`

Local packages can be built without a repository:

```powershell
.\scripts\Publish-TechBenchRelease.ps1 -Version <version> -AllowDirty
```

Publishing targets the V2-only release repository by default:

```powershell
.\scripts\Publish-TechBenchRelease.ps1 `
  -Version <version> `
  -Publish
```

The V2 client includes prerelease updates while it is in the alpha/beta cycle.
The publishing script marks versions containing a prerelease suffix, such as
`2.0.0-alpha.1`, as GitHub prereleases automatically.
Before the first published installer:

1. Create the public `Serlynth/TechBenchV2-Releases` release-only repository.
2. Authenticate `gh` on the release workstation.
3. Publish a V2 alpha package.
4. Install V1 and V2 side by side.
5. Verify each client sees only its own release feed and installer identity.
