# TechBench

Current application version: `1.2.19`.

Installed builds check the public binary-only GitHub release feed for stable updates.
When a new version is available, TechBench shows a Windows alert and can download it with visible progress,
create a verified database backup, install it, and restart automatically. See
[`docs/UPDATES.md`](docs/UPDATES.md) for installation and publishing details.

TechBench is a standalone Windows desktop worklog and ticket-notes application for small IT service workflows. It stores data in SQLite, opens to the daily worklog, and posts the Sage/WHD Note to SolarWinds Web Help Desk and native Time Tickets in Sage 50.

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

On first launch, TechBench offers to store its SQLite database on this PC or at another path. The default is:

```text
%LOCALAPPDATA%\TechBench\techbench.db
```

WHD and Sage credentials are stored as protected generic credentials in Windows Credential Manager, not in the SQLite database.

An existing installation continues using its current database. Settings provides **Move Database** and **Use Existing** commands, and TechBench creates a verified backup before either switch. Moving copies the database, verifies the copy, changes the active path, and retains the old file for rollback.

OneDrive and Dropbox paths are supported for moving a worklog between computers, but this is file synchronization, not a multi-user database. TechBench must be closed everywhere else before another computer opens the synced database.

The first launch creates the schema, note templates, and live-posting defaults. It does not create sample work entries.

TechBench creates one verified SQLite backup per day at startup and keeps the newest 14 copies in a `Backups` folder beside the active database. For the default path, that is:

```text
%LOCALAPPDATA%\TechBench\Backups
```

Settings also provides manual backup, integrity-check, database-location, and open-folder commands. Backups use SQLite's online backup API and are accepted only after `PRAGMA quick_check` succeeds.

## Current Features

- Note-first Today workspace with a full-size plain-text Sage/WHD Note editor and a dedicated Markdown Personal Note editor
- Recovery-only draft backup after a short pause, plus crash/close recovery without changing the committed note
- Work-note spell checking, undo, word/character counts, in-note find, timestamp and structured-note helpers
- Reusable tags with saved-tag suggestions in the editor, an autocomplete tag filter in Search, follow-up/waiting states, overdue badges, and closeout reminders
- Fast full-text search across Sage/WHD Notes, Personal Notes, clients, tickets, and tags, with a safe SQLite fallback
- Recent notes for the selected client, available directly beside the editor
- Grouped, branded Common Links workspace with protected admin shortcuts for WatchGuard Cloud, Microsoft 365, Barracuda, ESET PROTECT, and Email2Phone; a Hosted DNS section for GoDaddy and Network Solutions; an optional Chrome Incognito launch for Microsoft 365 Admin; and editable custom links
- Google Sheets CSV migration with preview, client matching, reusable aliases, duration conversion, duplicate warnings, a verified pre-import backup, and one transactional commit
- Daily worklog with entry cards, billable/non-billable totals, and pending WHD/Sage counts
- Weekly grouped worklog
- Client Matching workspace that pairs WHD company locations with Sage customers, automatically consolidates unique exact name matches, and presents fuzzy suggestions for manual review
- Local ticket creation and ticket filtering by client
- Entry editor with client picker, assigned-ticket picker, an authorized alternate WHD ticket-number option, time fields, hours/minutes duration, billable flag, Sage/WHD Note, and Markdown Personal Note
- Start/stop timer mode that fills start, end, and duration fields
- Search by keyword, client, date range, ticket text, and posting status
- CSV export for daily and weekly worklogs
- Live WHD and native Sage Time Ticket posting
- Exact-ID WHD TechNote verification and note synchronization, plus read-only Sage ODBC save tracking and verified linking of manually created Sage tickets
- Posting timestamps, Sage external references, status tracking, last-error storage, and posting payload logs
- Durable posting-attempt records and single-instance protection against duplicate external writes
- Configurable database location, first-run path choice, verified live database moves, and daily/manual backups with 14-copy retention
- Dark-first UI with a light theme toggle

## Note Workflow

The main editor stores a small recovery draft after a short pause, but it does not create or update the committed work entry. The editor remains visibly unsaved until you use **Save**. **New Entry** begins the next note, and the **Post** menu explicitly chooses WHD or Sage.

Use comma-separated tags for projects, locations, or work types. After an entry is saved, its tags are available from **Add saved tag** in the editor and from the autocomplete **Tags** filter in Search. Multiple Search tags require every listed tag to match. A note marked **Follow-up** or **Waiting** appears on its entry card and in Daily Closeout until it is marked **Completed** or **None**. Search can combine text, client, ticket, date, posting status, tags, and follow-up state.

Personal Notes accept CommonMark and GitHub-style Markdown. **Open Personal Note Editor** opens a modeless companion window with Source, Split, and Preview modes, and `F11` toggles full screen. The companion can remain open while the main TechBench window is used, follows the active entry, and writes only to that entry's in-memory editor until **Save Entry** (or the main **Save** action) commits it to the database. Normal unsaved-change protection still applies when switching entries. Historical and Sage-locked Personal Notes render as selectable, copyable Markdown and cannot be changed.

The Sage/WHD Note is always the plain-text note sent to WHD and Sage. A per-entry **Include Personal Note when posting to WHD** checkbox can append the Personal Note to WHD under a clearly marked Markdown section. It is off by default. Sage never receives the Personal Note.

## Client Matching

WHD synchronization reads company **Locations**, while Sage synchronization reads Sage customers. TechBench stores one combined client row when those records represent the same customer. Unique normalized company-name matches are consolidated automatically; punctuation, apostrophes, common legal suffixes, and a small set of company-name abbreviations are ignored for comparison.

The **Clients** workspace shows matched, WHD-only, and Sage-only totals. Select a WHD-only location to review TechBench's strongest Sage suggestion or choose the correct Sage customer manually. Fuzzy suggestions are never merged without confirmation. Matching reassigns existing local notes, tickets, and aliases to the combined client so duplicate client rows no longer split a customer's history.

`Ctrl+N` starts a new entry through the existing unsaved-change safeguards.

## Google Sheets Import

Export the old Google Sheet as CSV, then use **Settings > Worklog Import**. TechBench previews every recognized row before writing anything. The default duration rule matches the legacy sheet shown during development: values below 10 are interpreted as hours, while values of 10 or more are interpreted as minutes. The preview can switch all values to hours or all to minutes.

Client names are matched against synced TechBench, WHD, and Sage names. A mapping selected in the preview is remembered as an import alias. Unmatched rows can remain custom clients. Potential duplicates are unchecked by default. Immediately before import, TechBench creates and verifies a database backup, then writes all selected notes and aliases in one SQLite transaction.

## Posting Behavior

Posting starts from an explicit user command. No unattended background posting is implemented. Read-only Sage save verification may run in the background for an already-created or uncertain ticket.

Before either external write, TechBench saves the editor and creates a durable `PostingAttempts` row. Only one process and one attempt per entry/destination can run at a time. A crash, cancellation, or unconfirmed network result leaves an `Unknown` attempt that must be reconciled or explicitly abandoned before retrying.

WHD connections require HTTPS. Choose an explicit authentication mode in Settings, or use `Auto (detect once)` to detect and cache the first successful mode for that connection. A note is marked posted only after WHD returns a TechNote ID or TechBench reads back the exact note and duration from `TicketNotes`. Complete ticket syncs close local WHD tickets that are no longer assigned; partial or repeated-page syncs never reconcile missing tickets.

For a closed ticket or a ticket assigned to another technician, enable **Use another WHD ticket** and enter its number. TechBench first performs a read-only lookup and shows the ticket subject, WHD client, status, and local entry client for confirmation. It then uses the normal hidden-TechNote post and readback verification without requesting an assignment or status change. Web Help Desk permissions still apply; an inaccessible tech-group ticket remains pending with a permission error.

After a verified WHD post, the entry remains editable until it is posted to Sage. Saving compares the local WHD payload, the exact WHD TechNote, and the last verified snapshot: one-sided changes synchronize automatically, while competing changes require an explicit choice. **Sync WHD Note** can pull the WHD version on demand. Updates use `PUT` against the tracked TechNote ID and never fall back to creating another note. A WHD note without TechBench's Personal Note marker updates only the Sage/WHD Note and preserves the local Personal Note. Sage posting first verifies this WHD synchronization when the entry has a ticket.

Once a Sage ticket is verified as saved, the entry is permanently read-only. The editor has no unlock bypass, and the repository rejects later changes or deletion so the billed note cannot drift from Sage.

Sage opens its native Time Tickets window, enters and validates one ticket, and lets Sage assign the ticket number. When automatic saving is enabled, TechBench submits Sage's Save command, then verifies the committed row through read-only ODBC. ODBC runs in a short-lived hidden worker mode of the same x86 `TechBench.exe`; a hung Sage driver is terminated at the timeout and cannot leave TechBench stuck in an "already running" state. If Sage's ticket-number label was unavailable, TechBench accepts only one uniquely matching saved ticket using its date, duration, Activity Rate billing type, and Sage/WHD Note. Ambiguous matches remain pending. `Check Sage Save` performs the same read-only ODBC verification and does not manipulate the open Sage form. For an older entry that was posted manually, `More > Link Existing Sage Ticket...` accepts the Sage ticket number and clears the pending state only after the exact saved Time Ticket passes read-only ODBC validation.

WHD-posted entries remain editable and show a synchronization-pending state after local changes. Sage-posted entries remain available for viewing and copying, but cannot be changed.

## Restore A Backup

1. In Settings, note the current database and backup paths.
2. Close TechBench everywhere the database may be open.
3. Rename the current database so it remains available for diagnosis.
4. Copy the chosen file from the adjacent `Backups` folder to the active database path.
5. Start TechBench and run **Settings > Check Integrity**.

The verified rollback archive created before the July 14, 2026 note-first changes is outside the workspace at:

```text
C:\Users\skoog\OneDrive\Documents\Coding\TechBench-NoteFirst-Rollback-20260714-102647.zip
```

## Project Structure

- `Models` - client sync metadata, ticket, work entry, templates, posting status, query, and log models
- `Data` - SQLite connection factory, configurable database path, schema creation, client sync metadata migration, seed data, and repository methods
- `Providers` - client/ticket provider interfaces plus live WHD and Sage posters
- `Services` - client matching, database relocation/backup, dialog, CSV export, durable posting coordination, isolated Sage ODBC work, Sage desktop automation, and theme services
- `ViewModels` - MVVM state, commands, editor state, weekly grouping, timer, search, settings, and posting orchestration
- `Converters` - WPF visibility converters
- `MainWindow.xaml` - the desktop UI

## Verification

The test project automatically uses x64 while the production application remains x86 for Sage:

```powershell
dotnet test TechBench.Tests\TechBench.Tests.csproj -c Release
dotnet list TechBench.csproj package --vulnerable --include-transitive
```
