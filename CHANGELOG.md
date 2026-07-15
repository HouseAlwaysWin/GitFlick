# Changelog

All notable changes to GitFlick are recorded here. `release.ps1` promotes the `## Unreleased`
section to `## v<version> - <date>` when it cuts a release, so keep this list current as you go.

## Unreleased

- Multi-select staging, per-file history diffs, resizable/sortable history columns.
- Author and branch filters (fuzzy-searchable); the lane graph survives a branch filter.
- Double-click a branch badge to check it out, with an uncommitted-changes safety prompt.
- Idle memory trimming: the working set is released back to the OS while the app sits in the tray.
