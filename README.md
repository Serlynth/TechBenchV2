# TechBench

Current application version: `1.2.8`.

Installed builds check the public binary-only GitHub release feed for stable updates.
When a new version is available, TechBench shows a Windows alert and can download it with visible progress,
create a verified database backup, install it, and restart automatically. See
[`docs/UPDATES.md`](docs/UPDATES.md) for installation and publishing details.

TechBench is a standalone Windows desktop worklog and ticket-notes application for small IT service workflows. It stores data locally in SQLite, opens to the daily worklog, and posts work notes to SolarWinds Web Help Desk and native Time Tickets to Sage 50.

## Requirements

- Windows
- .NET 8 SDK 8.0.422 (pinned by `global.json`)

This workspace was verified with a local .NET SDK at:

```powershell
C:\Users\skoog\.dotnet\dotnet.exe --version
```

## Run

```powershell
dotnet restore
dotnet run
```

If your PATH still points at an older SDK, run:

```powershell
C:\Users\skoog\.dotnet\dotnet.exe run
```

## Build

```powershell
dotnet build
```

Create a self-contained Windows build:

```powershell
dotnet publish -c Release -r win-x86 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

The published app will be under:

```text
dist\TechBench.exe
```

## Local Data

TechBench creates its SQLite database automatically on first launch:

```text
%LOCALAPPDATA%\TechBench\techbench.db
```

WHD and Sage credentials are stored as protected generic credentials in Windows Credential Manager, not in the SQLite database.

The first launch creates the schema, note templates, and live-posting defaults. It does not create sample work entries.

TechBench creates one verified SQLite backup per day at startup and keeps the newest 14 copies:

```text
%LOCALAPPDATA%\TechBench\Backups
```

Settings also provides manual backup, integrity-check, and open-backup-folder commands. Backups use SQLite's online backup API and are accepted only after `PRAGMA quick_check` succeeds.

## Current Features

- Note-first Today workspace with a full-size plain-text editor
- Recovery-only draft backup after a short pause, plus crash/close recovery without changing the committed note
- Work-note spell checking, undo, word/character counts, in-note find, timestamp and structured-note helpers
- Tags, follow-up/waiting states, overdue badges for existing dated follow-ups, and closeout reminders
- Fast full-text search across work notes, internal notes, clients, tickets, and tags, with a safe SQLite fallback
- Recent notes for the selected client, available directly beside the editor
- Grouped, branded Common Links workspace with protected admin shortcuts for WatchGuard Cloud, Microsoft 365, Barracuda, ESET PROTECT, and Email2Phone; a Hosted DNS section for GoDaddy and Network Solutions; an optional Chrome Incognito launch for Microsoft 365 Admin; and editable custom links
- Google Sheets CSV migration with preview, client matching, reusable aliases, duration conversion, duplicate warnings, a verified pre-import backup, and one transactional commit
- Daily worklog with entry cards, billable/non-billable totals, and pending WHD/Sage counts
- Weekly grouped worklog
- Read-only synced/imported client list with source, external ID, active status, and last synced timestamp
- Local ticket creation and ticket filtering by client
- Entry editor with client picker, ticket picker, manual ticket number, time fields, hours/minutes duration, billable flag, work note, and internal note
- Start/stop timer mode that fills start, end, and duration fields
- Search by keyword, client, date range, ticket text, and posting status
- CSV export for daily and weekly worklogs
- Live WHD and native Sage Time Ticket posting
- WHD TechNote ID verification plus read-only Sage ODBC save tracking and verified linking of manually created Sage tickets
- Posting timestamps, Sage external references, status tracking, last-error storage, and posting payload logs
- Durable posting-attempt records and single-instance protection against duplicate external writes
- Verified daily/manual local database backups with 14-copy retention and an integrity check
- Dark-first UI with a light theme toggle

## Note Workflow

The main editor stores a small recovery draft after a short pause, but it does not create or update the committed work entry. The editor remains visibly unsaved until you use **Save**. **New Entry** begins the next note, and the **Post** menu explicitly chooses WHD or Sage.

Use comma-separated tags for projects, locations, or work types. A note marked **Follow-up** or **Waiting** appears on its entry card and in Daily Closeout until it is marked **Completed** or **None**. Search can combine text, client, ticket, date, posting status, tags, and follow-up state.

`Ctrl+N` starts a new entry through the existing unsaved-change safeguards.

## Google Sheets Import

Export the old Google Sheet as CSV, then use **Settings > Worklog Import**. TechBench previews every recognized row before writing anything. The default duration rule matches the legacy sheet shown during development: values below 10 are interpreted as hours, while values of 10 or more are interpreted as minutes. The preview can switch all values to hours or all to minutes.

Client names are matched against synced TechBench, WHD, and Sage names. A mapping selected in the preview is remembered as an import alias. Unmatched rows can remain custom clients. Potential duplicates are unchecked by default. Immediately before import, TechBench creates and verifies a database backup, then writes all selected notes and aliases in one SQLite transaction.

## Posting Behavior

Posting starts from an explicit user command. No unattended background posting is implemented. Read-only Sage save verification may run in the background for an already-created or uncertain ticket.

Before either external write, TechBench saves the editor and creates a durable `PostingAttempts` row. Only one process and one attempt per entry/destination can run at a time. A crash, cancellation, or unconfirmed network result leaves an `Unknown` attempt that must be reconciled or explicitly abandoned before retrying.

WHD connections require HTTPS. Choose an explicit authentication mode in Settings, or use `Auto (detect once)` to detect and cache the first successful mode for that connection. A note is marked posted only after WHD returns a TechNote ID or TechBench reads back the exact note and duration from `TicketNotes`. Complete ticket syncs close local WHD tickets that are no longer assigned; partial or repeated-page syncs never reconcile missing tickets.

Sage opens its native Time Tickets window, enters and validates one ticket, and lets Sage assign the ticket number. When automatic saving is enabled, TechBench submits Sage's Save command, then verifies the committed row through read-only ODBC. ODBC runs in a short-lived hidden worker mode of the same x86 `TechBench.exe`; a hung Sage driver is terminated at the timeout and cannot leave TechBench stuck in an "already running" state. If Sage's ticket-number label was unavailable, TechBench accepts only one uniquely matching saved ticket using its date, duration, Activity Rate billing type, and work note. Ambiguous matches remain pending. `Check Sage Save` performs the same read-only ODBC verification and does not manipulate the open Sage form. For an older entry that was posted manually, `More > Link Existing Sage Ticket...` accepts the Sage ticket number and clears the pending state only after the exact saved Time Ticket passes read-only ODBC validation.

Editing an entry that has already been posted prompts for confirmation. After saving a posted entry, the UI marks it as modified after posting.

## Restore A Backup

1. Close TechBench.
2. Rename `%LOCALAPPDATA%\TechBench\techbench.db` so it remains available for diagnosis.
3. Copy the chosen backup from `%LOCALAPPDATA%\TechBench\Backups` to `%LOCALAPPDATA%\TechBench\techbench.db`.
4. Start TechBench and run **Settings > Check Integrity**.

The verified rollback archive created before the July 14, 2026 note-first changes is outside the workspace at:

```text
C:\Users\skoog\OneDrive\Documents\Coding\TechBench-NoteFirst-Rollback-20260714-102647.zip
```

## Project Structure

- `Models` - client sync metadata, ticket, work entry, templates, posting status, query, and log models
- `Data` - SQLite connection factory, schema creation, client sync metadata migration, seed data, and repository methods
- `Providers` - client/ticket provider interfaces plus live WHD and Sage posters
- `Services` - dialog, CSV export, durable posting coordination, isolated Sage ODBC work, Sage desktop automation, and theme services
- `ViewModels` - MVVM state, commands, editor state, weekly grouping, timer, search, settings, and posting orchestration
- `Converters` - WPF visibility converters
- `MainWindow.xaml` - the desktop UI

## Verification

The test project automatically uses x64 while the production application remains x86 for Sage:

```powershell
dotnet test TechBench.Tests\TechBench.Tests.csproj -c Release
dotnet list TechBench.csproj package --vulnerable --include-transitive
```
