# GitFlick

A tray-based Git GUI (Avalonia / .NET). This glossary pins the terms the UI and code use for
Git concepts, so the same idea isn't called three things across the view models, the git service,
and the localized strings.

## Language

**Conflicted operation**:
A merge, cherry-pick, revert, or rebase that Git has paused because it couldn't combine changes on
its own, leaving unmerged paths for the user to resolve before it can finish or be abandoned.
_Avoid_: "merge conflict" (too narrow — the same paused-with-conflicts state comes from four
different operations, and only one of them is a merge).

**Unmerged path**:
A single file Git could not auto-combine during a conflicted operation — status `U`, both sides
recorded in the index. What the conflict resolver lists and the user resolves one by one.
_Avoid_: conflict file, conflicting change.

**Conflict side**:
One of the two versions of an unmerged path, named by the label Git wrote into the conflict
markers (e.g. `HEAD`, a branch, a commit subject). The top marker section is always
`checkout --ours`, the bottom always `--theirs`, whatever the operation — but the resolver shows
Git's marker names, never the words "ours"/"theirs".
_Avoid_: ours, theirs (correct in Git's plumbing, but the meaning inverts under rebase and reads as
gibberish to the user).
