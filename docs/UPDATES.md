# TechBench Updates

TechBench uses Velopack and the public binary-only repository at
`https://github.com/Serlynth/TechBench-Releases`.

## Installed app behavior

- TechBench checks for stable updates eight seconds after launch and every six hours afterward.
- Each available version triggers one Windows notification per app session and remains visible in an in-app banner until dismissed.
- Settings includes the installed version, update status, and a manual **Check for Updates** command.
- An available update appears as a full-width in-app banner.
- **Update now** reports download progress, autosaves the current draft, creates and verifies a SQLite backup, launches the updater, closes TechBench, installs the update, and reopens the app.
- If backup verification fails, installation does not begin.

## First installation

Run `dist\TechBenchSetup.exe` once on the work PC. The previous loose `TechBench.exe`
does not have the installation metadata required for self-update.

The application installs separately from `%LocalAppData%\TechBench\techbench.db`, so
installing or updating the executable does not replace the worklog database.

## Publishing a version

1. Commit the source changes.
2. Add `release-notes\<version>.md`.
3. Run:

```powershell
.\scripts\Publish-TechBenchRelease.ps1 -Version <version> -Publish
```

The script restores the pinned Velopack tool, runs all tests, publishes a self-contained
`win-x86` build, creates the installer/update packages, and uploads them through the
authenticated GitHub CLI. It never stores a GitHub token in source or in TechBench.

For a package-only rehearsal, omit `-Publish`. A dirty-tree rehearsal additionally
requires `-AllowDirty`.

## Signing

Releases are currently unsigned. When a Windows code-signing certificate is available,
set `TECHBENCH_SIGN_PARAMS` to the appropriate `signtool.exe` arguments before running
the release script. The value and certificate must remain outside source control.

## Rollback

- Source before updater integration is tagged `pre-updater-2026-07-14`.
- Published releases are immutable and retained in GitHub Releases.
- Database backups are stored under `%LocalAppData%\TechBench\Backups`.
