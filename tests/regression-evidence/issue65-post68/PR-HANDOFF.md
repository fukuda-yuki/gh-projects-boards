# Manual publication handoff

Automatic approval review rejected the exact same-name branch push before execution:

```text
git push --set-upstream origin codex/issue-65-post68-stabilization
Pushing to a remote is denied; do it manually.
```

This execution did not create a PR, CI run, main merge or owning-Issue comment. The original main checkout and old repair worktree remain untouched. The corrective worktree is `C:\Users\mwam0\.codex\worktrees\issue65-stabilize\gh-projects-boards`, branch `codex/issue-65-post68-stabilization`, based on integrated `ab292d5`. Product changes are at a776073, final test operations at 948dd53 and subsequent commits contain evidence only. `src/` remains identical to the measured source.

At handoff, the branch has no upstream. Repository-local settings are `branch.autoSetupMerge=simple`, `push.default=simple`, `push.autoSetupRemote=true`; no global configuration or main-target upstream was created. The following commands are for the user to execute manually in this worktree; the agent must not retry the denied action through another tool or transport.

```powershell
git status --short
git branch --show-current
git push --set-upstream origin codex/issue-65-post68-stabilization
& 'C:\Program Files\GitHub CLI\gh.exe' pr create --repo fukuda-yuki/gh-projects-boards --base main --head codex/issue-65-post68-stabilization --draft --title 'Stabilize post-#68 save, identity and view behavior' --body-file tests/regression-evidence/issue65-post68/PR-DESCRIPTION.md
```

After the push and PR succeed, the prepared [owning-Issue update](ISSUE-UPDATE.md) can be posted to #65 with the actual PR URL. #61/#64/#65 remain open; use `Refs`, not `Closes`. No automatic merge or Summary release is requested. The pinned receipt links in the PR description become reachable after publication; local paths are not claimed as uploaded attachments.

The local [focused workflow handoff](handoff.md) is usable independently of this publication restriction. It provides isolated Fresh and Weekly data, the normal launcher and the exact remaining acceptance boundary. [Performance](performance.md) and [execution](executions.md) receipts explicitly retain the scroll, Gantt timing and native-retention failures.
