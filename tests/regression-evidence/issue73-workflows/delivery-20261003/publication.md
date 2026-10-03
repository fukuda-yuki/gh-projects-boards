# Manual publication handoff

Review one branch range from `bcebff1d4e3765afb50f09692724b513bfe6a205` through the delivery commit on `codex/issue-73-weekly-recovery`. The source checkpoint is `138142df44f9314700827b675185d3dfbf39ceda`; the following evidence commit leaves all 272 executed build inputs unchanged. The owning Issue records the final delivery SHA after commit, avoiding a self-referential file hash.

Publication is blocked only at the enforced execution-policy boundary: `Pushing to a remote is denied; do it manually.` No retry or alternate write route was attempted. There is no main integration or release authorization. The branch has no upstream; repository-local settings are `branch.autoSetupMerge=simple`, `push.default=simple` and `push.autoSetupRemote=true`. No global Git setting was changed.

The owner can run the following in this checkout when publishing the prepared review unit:

```powershell
git switch codex/issue-73-weekly-recovery
git log --oneline origin/main..HEAD
git diff --stat origin/main...HEAD
git push --set-upstream origin codex/issue-73-weekly-recovery
& 'C:\Program Files\GitHub CLI\gh.exe' pr create `
  --repo fukuda-yuki/gh-projects-boards --base main `
  --head codex/issue-73-weekly-recovery --draft `
  --title 'Keep planning, weekly updates and publication recovery connected' `
  --body-file tests/regression-evidence/issue73-workflows/delivery-20261003/PR-body.md
```

The first push targets the same-name branch and establishes that upstream; it does not push main. If the remote rejects the push or already has divergent work, preserve it and review the divergence rather than forcing or rewriting history. Repository authentication remains with the ordinary CLI; no credentials are copied into this handoff.

The code/contracts/tests are committed. The selected evidence is committed separately for review clarity. Remaining untracked historical evidence is deliberately local and preserved, not missing production code. [Source binding](source-binding.json) and [copy manifest](curation-manifest.json) define the delivered boundary. Human acceptance, CI and release status must be assessed separately after publication.
