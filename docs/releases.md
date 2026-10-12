# Releases

[Issue #92](https://github.com/fukuda-yuki/gh-projects-boards/issues/92) owns distribution. The [release workflow](../.github/workflows/release.yml) prepares a draft; a maintainer checks its contents and publishes it. Merging a PR does not release the app.

## Triggers and version

| Trigger | Result |
| --- | --- |
| Push a tag `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-rc.N` | Validate the source, build/test the package, then create a draft Release. RC drafts are marked prerelease. |
| Actions → release → Run workflow, branch `main`, input `tag` | Retry an existing tag, for example after its CI completes. This never creates or moves a tag. |
| Pull request changing release infrastructure or packaging inputs | Build/test a candidate artifact with version `0.0.0-pr.NUMBER`. No Release or tag is created; the token is read-only. |

The release tag must resolve to a commit reachable from `origin/main`, with the latest `ci.yml` **push-to-main** run for that exact commit completed successfully. A green PR run checks a different commit and is insufficient. Missing, pending, failed, cancelled or skipped CI blocks release. The workflow rejects malformed versions, tags moved since the triggering push and existing Releases (including drafts); it never replaces assets. Concurrent attempts for a tag are serialized without cancelling an active package build.

Use a single product version while this repository contains one app. RC numbers start at 1; numeric components have no leading zeros. The version is embedded in the assembly, ZIP name and distribution manifest; source commit and resolved runtime versions remain recorded. Keep tags immutable. Configure a repository tag ruleset for `v*` to limit tag creation and prohibit updates/deletions; workflow validation does not configure repository rules.

## Prepare a candidate

After merging the release workflow and desired changes, wait for main CI on the selected commit to succeed. On a clean checkout, choose the next unused version (these values are examples):

```bash
git fetch origin main --tags
git tag -a v0.1.0-rc.1 origin/main -m 'Internal evaluation candidate 0.1.0-rc.1'
git push origin refs/tags/v0.1.0-rc.1
```

The workflow verifies dependencies/notices using `Publish-Internal.ps1`, extracts the ZIP, checks its payload hashes and source/version, and runs the ordinary-app E2E journeys against **that extracted executable**, with fake gh and isolated storage. Evidence is retained as an Actions artifact; the Release receives only the versioned ZIP and `SHA256SUMS.txt`. The build job has no repository write permission; only the final draft job receives `contents: write`.

Check the workflow result, source commit, notes and download before selecting **Publish release**. Start with an RC while the clean-company-PC, physical Japanese IME and full-working-day checks in #92 are outstanding. CI success does not complete those checks. Release visibility follows repository visibility; a draft is not a private distribution mechanism after publication.

If CI was still running, wait for it, then retry via Run workflow with the same tag. A failure before draft creation can be rerun. If draft creation/upload partially failed, inspect the incomplete draft; deliberately delete that unpublished draft before retrying, or upload the already-verified missing asset yourself. Never delete a published release or move a tag to repair a version; issue a new version. Once a draft exists, download its original assets rather than rebuild it: SDK servicing may change resolved .NET runtime versions on a later run.

## Self-contained policy

Ship .NET and Windows App SDK app-locally in a portable **Windows 11 x64 ZIP**. Recipients do not install either runtime or a development SDK. WinUI XAML/PRI, native libraries, templates and notices stay alongside the executable; single-file publishing, trimming and Native AOT are not enabled. Extract the whole ZIP, and update into a new folder.

GitHub CLI remains a separately installed/authenticated dependency, following the [Japanese guide](internal-distribution-ja.md). Bundling it would add another binary inventory, notice audit and update responsibility; this workflow does not claim a machine with no prerequisites. Windows system components, company execution policies, network access and Project permissions still apply. ARM64, installers, signing and automatic updates have no delivery path in this workflow.

Self-contained distributions are larger and do not pick up machine-wide .NET fixes. The release owner must monitor .NET/Windows App SDK servicing, review the exact dependency inventory when upgrading, rebuild and distribute a new version for applicable security fixes. Retain original assets and hashes; rebuilding the same source is not a promise of identical bytes.
