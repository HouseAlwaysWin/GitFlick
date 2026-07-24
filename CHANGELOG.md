# Changelog

All notable changes to GitFlick are recorded here. `release.ps1` promotes the `## Unreleased`
section to `## v<version> - <date>` when it cuts a release, so keep this list current as you go.

## Unreleased

- **Changes made outside GitFlick now show up on their own.** Committing or staging from VS Code (or the CLI) used to leave the window on a stale view until you hit ↻ — the background sync only ever watched the *remote*, which staging and local edits don't touch. GitFlick now watches the repository itself and reloads when something lands.
- **"Clear filters"** in History drops every filter at once — search, authors, branches, dates, the toggles and the sort — instead of clearing each dropdown in turn. It appears only when something is filtered.
- **The title bar says where the branch tracks:** `main → origin/main` for a tracked branch, `main (local only)` for one that has never been pushed.

## v0.4.0 - 2026-07-24

- **History search, reworked to a VS Code-style panel:** a query box with case-sensitive (`Aa`) and regex (`.*`) toggles, plus separate "files to include" / "files to exclude" path filters that combine with the query. Message searches commit text, File searches paths, Content is a pickaxe over file contents — each shows only the boxes that apply, and the path autocomplete drops down as you type and honours the exclusion live.
- **History adapts to a narrow window:** the filter row, the commit table's columns, and the "Load more" button now fit (or scroll) instead of being clipped when the pane is shrunk.
- **Loading indicator** is now a slim accent-coloured bar pinned to the top edge, so it no longer shifts the rest of the window as operations start and finish.
- Finished the Traditional/Simplified Chinese and Japanese translations for the History filter labels, column headers, and a few status messages that were still showing in English.

## v0.3.0 - 2026-07-23

- **Git accounts & identity:** see the signed-in GitHub account, switch accounts, and sign in — plus set your commit name/email globally or per repository.
- **Remotes:** add and remove remotes from within the app, and an opt-in background auto-fetch keeps the ahead/behind counts and history current without a manual refresh.
- **Branch flyout:** branches are split into LOCAL and REMOTE sections; publish an unpushed branch in one click, and checking out a remote branch creates a local tracking branch.
- **History — date-range filter:** filter commits by a date range, with quick presets (Today, Last 7 days, Last 30 days, This month).
- **History — configurable page size**, and a smarter "Load more" that keeps paging until more matching commits actually appear when a filter is active.
- **History:** show a commit's files as a folder tree, and view what a merge resolved by hand.
- **Command log:** a panel showing exactly what git printed for each operation.
- A loading indicator for long-running operations, a dismissable hotkey-conflict warning, and commit-card refs coloured by kind.
- Fixes: the History filter row no longer paints over the diff pane, and a transient identity read no longer shows as "no identity".

## v0.1.0 - 2026-07-20

- Built-in updater: check GitHub for new releases and install (or roll back to) any version from Settings, with an opt-in startup check.
- Multi-select staging, per-file history diffs, resizable/sortable history columns.
- Author and branch filters (fuzzy-searchable); the lane graph survives a branch filter.
- Double-click a branch badge to check it out, with an uncommitted-changes safety prompt.
- Idle memory trimming: the working set is released back to the OS while the app sits in the tray.
