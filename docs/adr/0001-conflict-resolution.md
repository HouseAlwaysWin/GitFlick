# Manual conflict resolution covers every conflicted operation, and never says "ours/theirs"

GitFlick can start a merge, cherry-pick, revert, and rebase, so all four can stop on conflicts — the
resolver treats them as one thing (a [conflicted operation](../../CONTEXT.md)) rather than special-casing
merge. Detection returns *which* operation is in progress (from `MERGE_HEAD` / `CHERRY_PICK_HEAD` /
`REVERT_HEAD` / the `rebase-merge`|`rebase-apply` dir); finishing and abandoning are the uniform
`git <op> --continue` / `git <op> --abort` (with the editor suppressed so `--continue` can't hang), and
the UI is state-driven — after continuing a rebase that re-conflicts on the next commit, the window
simply repopulates rather than running any rebase-specific logic.

## Considered options

- **Merge-only** (rejected): the obvious minimum, but cherry-pick/revert/rebase conflicts would leave
  the user stuck with `U` files and no way in — and we already ship those three commands.
- **Label the take-a-side buttons "ours"/"theirs"** (rejected): correct in Git's plumbing but the
  meaning *inverts* under rebase (`--ours` is the branch you're rebasing onto, not your work), so the
  labels would be actively wrong. Instead the buttons show the names Git wrote into the conflict
  markers (`<<<<<<< HEAD` … `>>>>>>> feature`), which are always accurate; the top marker section maps
  to `--ours` and the bottom to `--theirs`, a mapping that is structurally stable across all four
  operations even though the *meaning* isn't.
- **Modal resolver window** (rejected): simpler to keep in sync, but it traps the user mid-resolve and
  breaks the app's modeless-window convention. The window is modeless and derives from the same
  "is there a conflicted operation" state the toolbar banner does, refreshed through the existing file
  watcher — so resolving in an external tool updates it too, and one source of truth keeps the banner
  and the window from disagreeing.

## Consequences

- Non-content conflicts degrade instead of pretending to be text: **modify/delete** offers keep
  (`git add`) vs accept-deletion (`git rm`); **binary** offers only take-a-side; the exotic
  rename/rename cases are listed but punt to the CLI.
- Because `git add` marks a path resolved without checking, the resolver **warns when a file still
  contains `<<<<<<<` markers** before it's marked resolved or the operation is completed.
