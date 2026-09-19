# Maintainer publication handoff

Implementation: **A+B** on `codex/issue-51-apply-candidate`, based on main `5e5fb12b24e453dcd41d993b23b29ce78ec7f81c`. The branch is for source review; live 50-field adoption remains blocked as described in [the report](README.md). No push, new PR, merge, release or Issue closure was performed. PR #57 remains the separate UI delivery.

Commit purposes, in order:

1. `2437e64` — A: combine fresh initial observation queries while retaining full validation.
2. `1695faf` — B: consolidate immediate preflight and bind every contributing response to its viewer.
3. `21a9884` — corrected measurement/lifecycle harness, diagnostics and prospective fixed plan. This is the frozen candidate execution source.
4. `54d312b` — comparison output explicitly labels predeclared single-sample controls. No executable or sampling change.
5. Evidence commit containing this handoff — privacy-reviewed results, security review, isolated blocker and PR materials. Obtain its exact full hash with the command below; the local delivery packet also records the complete list.

PR title is in [pr-title.txt](pr-title.txt); exact body is in [pr-body.md](pr-body.md), with `Refs #51`. Do not use `Closes #51` while the acceptance gate remains unmet. The body link to this branch resolves only after publication.

The local branch has no upstream; no same-name remote branch existed at closeout. Repository-local `branch.autoSetupMerge=simple`, `push.default=simple` and `push.autoSetupRemote=true` are set. Before publication, verify any subsequently established upstream names this same feature branch, never `origin/main`. The commands below are prepared for the maintainer and were **not executed**:

```powershell
# Run from the repository root after reviewing the commits and evidence.
git switch codex/issue-51-apply-candidate
git log --reverse --format='%H %s' 5e5fb12b24e453dcd41d993b23b29ce78ec7f81c..HEAD
git config --get branch.codex/issue-51-apply-candidate.remote
git config --get branch.codex/issue-51-apply-candidate.merge
# Both absent is expected before first push; otherwise require origin and
# refs/heads/codex/issue-51-apply-candidate respectively.
git push -u origin codex/issue-51-apply-candidate
$evidence = 'tests/performance-evidence/issue51/2026-09-19-candidate'
& 'C:\Program Files\GitHub CLI\gh.exe' pr create --draft --repo fukuda-yuki/gh-projects-boards `
  --base main --head codex/issue-51-apply-candidate `
  --title 'Reduce Apply observation invocations with response-bound identity checks' `
  --body-file "$evidence/pr-body.md"
```

A verified local Git bundle accompanies this delivery under `TestResults/issue51-candidate/handoff/issue51-candidate.bundle`. It contains this feature branch's new commits and requires the pinned main prerequisite; it contains no ignored private run outputs. To import into another clone that already has the prerequisite, inspect it first:

```powershell
git bundle verify <absolute-path-to-issue51-candidate.bundle>
git fetch <absolute-path-to-issue51-candidate.bundle> `
  refs/heads/codex/issue-51-apply-candidate:refs/heads/codex/issue-51-apply-candidate
```

The [PR #57 Testing replacement](pr57-testing-replacement.md) is prepared only and uses #53/#54's own source-specific evidence. Publishing the new PR does not authorize editing or reusing PR #57, support submission, main merge or accepting unmeasured live throughput.
