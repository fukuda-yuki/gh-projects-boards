# Maintainer publication

Title: **Restore Apply after interruption with explicit current-value review**

Branch: `codex/issue-51-live-apply-repair`, based on `13d8bd7` (`origin/main` after PR #58). The branch was created with `--no-track`. It must use only its same-name remote upstream. The source, regression cases, rendered evidence and validation ledger are delivered locally.

From the repository root, after reviewing the local commits:

```powershell
git status --short --branch
git log --oneline origin/main..codex/issue-51-live-apply-repair
git config --get branch.codex/issue-51-live-apply-repair.remote
git config --get branch.codex/issue-51-live-apply-repair.merge
git push -u origin codex/issue-51-live-apply-repair
& 'C:\Program Files\GitHub CLI\gh.exe' pr create --repo fukuda-yuki/gh-projects-boards --base main --head codex/issue-51-live-apply-repair --title 'Restore Apply after interruption with explicit current-value review' --body-file tests/regression-evidence/issue51-apply-recovery/pr-body.md
```

The two upstream checks should be empty before first publication, or name `origin` and `refs/heads/codex/issue-51-live-apply-repair`. The commands are prepared, not executed. A bundle and an exact commit list are retained privately under `TestResults/issue51-live-regression/handoff/`.
