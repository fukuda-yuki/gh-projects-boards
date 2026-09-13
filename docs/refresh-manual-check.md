# Manual refresh check

Human results remain pending until actually reported. Synthetic checks, automated live validation and human live acceptance are distinct. The product only reads GitHub and saves local work. The opt-in fixture utility below performs separate remote writes against exactly:

- Repository: https://github.com/fukuda-yuki/codex-sandbox (`R_kgDOUVKgAw`).
- Project: https://github.com/users/fukuda-yuki/projects/3 (`PVT_kwHOBGPKL84BjFYc`).
- Authorization record: https://github.com/fukuda-yuki/codex-sandbox/issues/1.

## Synthetic grid check

Build Release and run `scripts/Start-EditingCheck.ps1`. Select the saved `github.com / ID 42` profile and P1/P2, without checking a live connection. Verify direct Japanese typing, IME confirmation versus cell commit, candidate cancellation/reconversion, rectangular operations, scrolling and normal restart with `-DataRoot <printed-path> -Resume`. Earlier prototype acceptance does not establish registered-grid acceptance.

## Live setup, only when starting this check

From the repository in PowerShell, choose a new directory. The setup prints the exact disposable Issue URL, Issue/item IDs and titles A/B; retain its manifest. It refuses an existing manifest and never retries uncertain creation.

```powershell
$checkRoot = Join-Path (Get-Location) ('TestResults/manual-refresh-' + [guid]::NewGuid().ToString('N'))
$fixtureFile = Join-Path $checkRoot 'fixture.json'
.\scripts\Manage-RefreshFixture.ps1 -Action Start -Manifest $fixtureFile
$app = Join-Path (Get-Location) 'src/GhProjectsBoards.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/GhProjectsBoards.App.exe'
$start = [Diagnostics.ProcessStartInfo]::new($app)
$start.UseShellExecute = $false
$start.Environment['GHPB_DATA_ROOT'] = Join-Path $checkRoot 'live-data'
$appProcess = [Diagnostics.Process]::Start($start)
```

1. In the ordinary app, check the local gh connection for `github.com` and the intended account. Open registered Projects, add the exact Project URL above, and retrieve it. Locate the disposable Issue by its printed marker/ID. Do not edit the retained scope Issue or any other row.
2. Verify baseline title A. Enter the printed B title locally and commit the cell. Verify a local difference and no GitHub change. Type naturally; IME Enter confirms composition, the next Enter commits the cell.
3. Perform the separate external fixture change:

```powershell
.\scripts\Manage-RefreshFixture.ps1 -Action Change -Manifest $fixtureFile
```

4. Select `最新を取得`. The local cell stays B and shows a conflict. Open `競合・未確認を比較`; verify baseline A, local B, fetched C, IDs, ownership and time. Refresh again before choosing, if desired; the unresolved A/B/C conflict must remain.
5. Choose GitHub C, keep B, or enter another valid value. Only local state changes: C becomes baseline; choosing C is clean, another value stays changed. Confirm that the choice labels and consequences are understandable. Close normally and restart the same isolated data root; verify recovery. Do not count any untried choice or input scenario as human acceptance.
6. Independently verify GitHub still has C:

```powershell
$fixture = Get-Content -LiteralPath $fixtureFile -Raw | ConvertFrom-Json
& 'C:\Program Files\GitHub CLI\gh.exe' issue view $fixture.number --repo fukuda-yuki/codex-sandbox --json number,title,url
```

## Owned cleanup

Close the check app, then run cleanup. It verifies the unique marker and exact IDs, deletes only the owned fixture, and checks existing sandbox data. Keep the manifest/evidence. If a mutation reports failure or uncertainty, inspect the manifest and GitHub before any retry; do not run setup again blindly.

```powershell
.\scripts\Manage-RefreshFixture.ps1 -Action Cleanup -Manifest $fixtureFile
```

Report the chosen resolution, natural input observations, restart result and cleanup status. Screenshots, caches and raw evidence can contain private account/Project data; retain them locally. This is not Apply, new-row, broader-field or distribution acceptance.
