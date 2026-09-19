# Reproduction and continuation

Execute serially on an unlocked Windows desktop with .NET SDK 10.0.401 and the repository's existing pinned dependencies. Retain new output directories, failures and source/binary mappings. Commands below do not publish anything.

## Fixed source and common harness

Main is `5e5fb12b24e453dcd41d993b23b29ce78ec7f81c`; candidate and corrected harness are `21a9884cacc1b8035ec70d83f79c9db5aed48e4d`. Candidate has no experiment switch. Main uses its own production logic with the exact candidate test `.cs`/`.csproj` source and common trace instrumentation. Do not apply A/B to the comparison baseline.

The retained source archives and all three relevant binary hashes are in [source-binary-map.json](source-binary-map.json). [common-harness-files.json](common-harness-files.json) records the independently compared source files: `sha256` hashes the byte-identical build checkouts, and `canonicalLfSha256` normalizes CRLF to LF for comparison with Git archive content. Fifteen candidate archive files have different line endings; all 42 common source files match after normalization. The private `main-common-harness.patch` records tracked overlays; the main source archive also contains new harness files. Assembly hashes differ between builds because their referenced production code/build provenance differs; source equality is verified separately.

To reconstruct the overlay in a separate detached worktree at the pinned main: copy `tests/GhProjectsBoards.Tests/*.cs` from candidate, keep the identical csproj, and copy `GhApi.cs`, `ApplyExecutor.cs` and `PerformanceTrace.cs` from candidate. Those three production files differ from main only by the trace span/counter additions. In main's `GhConnection.cs`, add the same `connection-gate-wait` span to the existing three `gate.WaitAsync` calls. Do not copy candidate's connection or reader implementations. Build each test executable in Release and freeze its complete output directory before sampling. Preserve `git diff --binary`, untracked source files, build logs and hashes.

```powershell
dotnet build tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release
# $mainExe and $candidateExe are absolute paths to the corresponding frozen outputs.
./scripts/Test-Performance.ps1 -RunId issue51-current-main -Workload CandidateCheck -NoBuild `
  -Executable $mainExe -SourceRevision 5e5fb12b24e453dcd41d993b23b29ce78ec7f81c
./scripts/Test-Performance.ps1 -RunId issue51-current-candidate -Workload CandidateCheck -NoBuild `
  -Executable $candidateExe -SourceRevision 21a9884cacc1b8035ec70d83f79c9db5aed48e4d `
  -ReferenceRoot (Join-Path $PWD 'TestResults/performance/issue51-current-main')
./scripts/Compare-Performance.ps1 `
  -Baseline (Join-Path $PWD 'TestResults/performance/issue51-current-main') `
  -Candidate (Join-Path $PWD 'TestResults/performance/issue51-current-candidate') `
  -Output (Join-Path $PWD 'TestResults/issue51-candidate/comparison') -AllowSingleSampleControls
```

## Selected behavioral checks

```powershell
dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release `
  --filter 'FullyQualifiedName~ObservationIdentityTests|FullyQualifiedName~LivePerformanceGuardTests|FullyQualifiedName~LivePerformanceStageTests' `
  --logger 'trx;LogFileName=results.trx' --results-directory TestResults/issue51-candidate/harness-final-v3
./scripts/Test-UiIntegration.ps1 `
  -Where "method =~ 'WithdrawingFailedApply|LateApplyOutcome|DeletedProblemTarget|CompletedApplyHistory|SuccessfulConfirmation|MixedResultsReturn'"
./scripts/Test-E2E.ps1 `
  -Filter 'FullyQualifiedName~OrdinaryApplyReviewsTitleThenVerifiesAndRestoresHistoryWithoutReplay|FullyQualifiedName~MixedApplyReturnsToProblemWithoutTakingNativeCompositionFocus&TestCategory!=GridIme'
```

The UI host first invocation used a nonexistent class name (the file contributes to partial `HostedTests`), selected zero cases and was rejected by the runner. It is retained as zero execution, not a pass. The corrected method selector above executed eight cases. Whole-app tests use an isolated external fake gh; no performance matrix is driven through desktop E2E.

## Live lifecycle

The prospective [plan](plan.md) remains authoritative. A plan JSON has absolute `FixtureRoot`, `LedgerRoots`, and binary `Executable` paths; binary records also contain exact `Source`, `CoreSha256`, `HarnessSha256`. It identifies `Fields`, `Order`, `Main`, `Candidate`, `SetupEvidence` and `Diagnostic`. Include old failed-attempt and cleanup ledgers, not merely the current run. New live work must first re-read sandbox Issue #1 and current rate/ownership state.

```powershell
# Initialization is offline. Each invocation uses a NEW RunId; never overwrite evidence.
./scripts/Test-Performance.ps1 -Mode AdapterStage -Stage Initialize -RunId diagnostic-init `
  -NoBuild -Executable $candidateExe -PlanPath $planPath
./scripts/Test-Performance.ps1 -Mode AdapterStage -Stage Prepare -RunId diagnostic-prepare `
  -NoBuild -Executable $candidateExe -FixtureRoot $fixtureRoot -Label candidate
# Inspect the retained outcome first. Never automatically retry Prepare on uncertainty.
./scripts/Test-Performance.ps1 -Mode AdapterStage -Stage Verify -RunId diagnostic-verify `
  -NoBuild -Executable $candidateExe -FixtureRoot $fixtureRoot -Label candidate
./scripts/Test-Performance.ps1 -Mode AdapterStage -Stage Cleanup -RunId diagnostic-cleanup `
  -NoBuild -Executable $candidateExe -FixtureRoot $fixtureRoot -Label candidate
```

Diagnostic plans require one field and an empty measurement order. The increment permits at most two single-item diagnostics, each with its own documented hypothesis; these commands do not authorize a second attempt. A failed/uncertain add is never replayed. A budget checkpoint supplies continuation arguments and earliest permitted time; use a new output directory after that time and re-admission. Cleanup reconciles known target identity before deletion and verifies complete baseline restoration.

The 50-field comparison remains gated. If a supported correction or validated containment is accepted, initialize a **new** non-diagnostic plan with 50 fields, setup evidence and order `[main,candidate,candidate,main]`. Run Prepare/Verify, then each Measure followed by Restore using pinned main. Verify unchanged baseline before each sample and cleanup once at the end. Follow checkpoint deferrals across windows; the full lifecycle is 600 planned logical mutations. A cleaned diagnostic's deleted identities are never reusable fixtures.
